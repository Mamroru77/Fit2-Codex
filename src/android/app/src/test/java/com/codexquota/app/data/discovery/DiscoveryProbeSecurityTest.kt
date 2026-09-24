package com.codexquota.app.data.discovery

import com.codexquota.app.data.api.OkHttpBridgeApi
import com.codexquota.app.data.pairing.IdentityMismatchException
import com.codexquota.app.data.pairing.PairingClient
import com.codexquota.app.data.pairing.PairingOutcome
import com.codexquota.app.data.security.BridgeEndpoint
import com.codexquota.app.data.security.BridgeIdentity
import com.codexquota.app.data.security.BridgeIdentityVerifier
import com.codexquota.app.data.security.BootstrapFingerprintTrustManager
import com.codexquota.app.data.security.PairedBridge
import com.codexquota.app.data.security.PairedBridgeStore
import com.codexquota.app.data.security.SecretBox
import com.codexquota.app.data.security.SealedSecret
import com.codexquota.app.data.security.SecretStorage
import com.codexquota.app.testing.FakeBridgeServer
import com.codexquota.app.testing.TestPki
import java.security.cert.CertificateException
import java.security.cert.X509Certificate
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertIs
import kotlin.test.assertNotNull
import kotlin.test.assertTrue
import kotlinx.coroutines.runBlocking

/**
 * The discovery bootstrap boundary.
 *
 * `BridgeIdentityProbe` is the one place the app completes a TLS handshake without already knowing
 * which identity to expect. The approved design requires it — automatic discovery has to show the
 * user a code derived from the certificate the Bridge actually presents, and discovery alone must
 * never establish trust — so the question a review has to answer is not "does it accept a
 * certificate before a pin exists" (it must) but **"can anything secret reach the wire before a pin
 * exists"** (it must not).
 *
 * Every assertion below is one half of that boundary, and they are written against a real HTTPS
 * server presenting a real chain rather than against a stub.
 */
class DiscoveryProbeSecurityTest {

    private val store = PairedBridgeStore(storage = MemoryStorage(), secretBox = PlainBox())

    // --- A: the probe can make only the anonymous health request --------------------------------

    @Test
    fun theProbeTouchesOnlyTheAnonymousLivenessRoute() = runBlocking {
        val bridge = FakeBridgeServer()

        try {
            val endpoint = bridge.start()

            BridgeIdentityProbe().probe(endpoint)

            assertEquals(
                listOf("/api/v1/health"),
                bridge.paths(),
                "the probe must not touch any route other than the anonymous liveness one",
            )
        } finally {
            bridge.stop()
        }
    }

    // --- B: no credential exists at probe time --------------------------------------------------

    @Test
    fun theProbeCarriesNoAuthorizationHeader() = runBlocking {
        val bridge = FakeBridgeServer()

        try {
            BridgeIdentityProbe().probe(bridge.start())

            assertTrue(
                bridge.authorizationHeaders().isEmpty(),
                "no request the probe makes may carry a credential, but it sent ${bridge.authorizationHeaders()}",
            )
        } finally {
            bridge.stop()
        }
    }

    @Test
    fun noHeaderTheProbeSendsCarriesAnythingSecret() = runBlocking {
        val bridge = FakeBridgeServer()

        try {
            BridgeIdentityProbe().probe(bridge.start())

            // The whole header set, not just `Authorization`: a credential smuggled into a custom
            // header would be just as bad.
            val headers = bridge.received.flatMap { request ->
                request.headers.names().map { name -> name to request.getHeader(name).orEmpty() }
            }

            assertTrue(
                headers.none { (name, _) -> name.equals("Authorization", ignoreCase = true) },
                "the probe must send no Authorization header, but sent $headers",
            )
            assertTrue(
                headers.none { (_, value) -> value.contains(SAMPLE_CREDENTIAL) },
                "no header the probe sends may carry a credential, but sent $headers",
            )
            assertTrue(
                headers.none { (name, _) -> name.contains("token", ignoreCase = true) },
                "the probe must send no token header, but sent $headers",
            )
        } finally {
            bridge.stop()
        }
    }

    // --- C: malformed, expired and mis-signed chains fail ---------------------------------------

    @Test
    fun anExpiredLeafIsRefusedByTheProbe() = runBlocking {
        val identity = TestPki.newAuthority("Codex Quota Bridge")
        val expiredLeaf = TestPki.leafFor(
            authority = identity,
            host = FakeBridgeServer.LOCALHOST,
            notBefore = TestPki.expiredNotBefore(daysAgo = 400),
            notAfter = TestPki.expiredNotAfter(daysAgo = 30),
        )

        val bridge = FakeBridgeServer(identity = identity, leafOverride = expiredLeaf)

        try {
            val endpoint = bridge.start()

            val failure = runCatching { BridgeIdentityProbe().probe(endpoint) }.exceptionOrNull()

            assertNotNull(failure, "an expired leaf must not be accepted, even for observation")
            assertTrue(
                failure.isCertificateProblem(),
                "expected a certificate failure but got $failure",
            )
        } finally {
            bridge.stop()
        }
    }

    @Test
    fun aLeafThatIsNotSignedByItsIssuerIsRefused() {
        val identity = TestPki.newAuthority("Codex Quota Bridge")
        val stranger = TestPki.newAuthority("somebody else")
        val leafFromStranger = TestPki.leafFor(stranger, FakeBridgeServer.LOCALHOST)

        // A well-formed chain of two certificates that do not actually relate to each other. Without
        // the signature check, an unrelated leaf could ride along beside the real identity.
        assertFailsWith<CertificateException> {
            BridgeIdentityVerifier.verifyShape(arrayOf(leafFromStranger.certificate, identity.certificate))
        }
    }

    @Test
    fun aNonCertificateAuthorityIssuingAnotherCertificateIsRefused() {
        val identity = TestPki.newAuthority("Codex Quota Bridge")
        val leaf = TestPki.leafFor(identity, FakeBridgeServer.LOCALHOST)

        // The leaf is not a CA, so it cannot anchor anything.
        assertFailsWith<CertificateException> {
            BridgeIdentityVerifier.verifyShape(arrayOf(identity.certificate, leaf.certificate))
        }
    }

    @Test
    fun anEmptyChainIsRefused() {
        assertFailsWith<CertificateException> {
            BridgeIdentityVerifier.verifyShape(emptyArray<X509Certificate>())
        }
    }

    @Test
    fun anOutOfDateIdentityIsRefused() {
        val expired = TestPki.newAuthority(
            "Codex Quota Bridge",
            notBefore = TestPki.expiredNotBefore(daysAgo = 4000),
            notAfter = TestPki.expiredNotAfter(daysAgo = 30),
        )

        assertFailsWith<CertificateException> {
            BridgeIdentityVerifier.verifyShape(arrayOf(expired.certificate))
        }
    }

    // --- D: a hostname or SAN mismatch fails ----------------------------------------------------

    @Test
    fun aLeafIssuedForAnotherNameIsRefused() = runBlocking {
        val identity = TestPki.newAuthority("Codex Quota Bridge")

        // The server answers on `localhost` but presents a certificate for somewhere else.
        val elsewhere = TestPki.leafFor(identity, "not-the-bridge.local")
        val bridge = FakeBridgeServer(identity = identity, leafOverride = elsewhere)

        try {
            val endpoint = bridge.start()

            val failure = runCatching { BridgeIdentityProbe().probe(endpoint) }.exceptionOrNull()

            assertNotNull(
                failure,
                "the hostname check must still apply to the probe; it is not a trust-all client",
            )

            Unit
        } finally {
            bridge.stop()
        }
    }

    // --- E: observing an identity never stores it as trusted ------------------------------------

    @Test
    fun observingAnIdentityStoresNothing() = runBlocking {
        val bridge = FakeBridgeServer()

        try {
            val observed = BridgeIdentityProbe().probe(bridge.start())

            assertEquals(bridge.identityFingerprint, observed.identityFingerprint)

            // Observing is not pairing: nothing was persisted, so the next launch knows nothing
            // about this Bridge.
            assertEquals(
                null,
                store.load(),
                "observing an identity must not create a pairing",
            )
        } finally {
            bridge.stop()
        }
    }

    @Test
    fun anObservedIdentityExposesAComparisonCodeAndNothingElse() = runBlocking {
        val bridge = FakeBridgeServer()

        try {
            val observed = BridgeIdentityProbe().probe(bridge.start())

            // The code is what a person compares; the certificate is what a pin is computed from.
            // Neither is trust.
            assertEquals(bridge.verificationCode, observed.verificationCode)
            assertEquals(19, observed.verificationCode.length)
            assertEquals(4, observed.verificationCode.split("-").size)

            assertEquals(null, store.load())
        } finally {
            bridge.stop()
        }
    }

    // --- F: no pairing request before explicit confirmation -------------------------------------

    @Test
    fun noPairingRequestExistsBeforeTheUserConfirms() = runBlocking {
        val bridge = FakeBridgeServer()

        try {
            BridgeIdentityProbe().probe(bridge.start())

            assertTrue(
                bridge.paths().none { it.startsWith("/api/v1/pairing") },
                "no pairing may begin without a confirmation, but the Bridge saw ${bridge.paths()}",
            )
            assertEquals(null, store.load())
        } finally {
            bridge.stop()
        }
    }

    // `ConfirmedBridgeIdentity` is the compile-time half of this property: `pairByDiscovery` takes
    // one, and only `DiscoveredBridgeIdentity.confirmedByUser()` produces one, so a caller cannot
    // reach the pairing path without a confirmation. The behavioural half is
    // `noPairingRequestExistsBeforeTheUserConfirms`, above.

    // --- G: after confirmation, a new client pinned to the confirmed fingerprint ----------------

    @Test
    fun confirmingStartsAPairingPinnedToTheConfirmedFingerprint() = runBlocking {
        val bridge = FakeBridgeServer()

        try {
            val endpoint = bridge.start()
            val observed = BridgeIdentityProbe().probe(endpoint)

            bridge.approvePairing()

            val client = PairingClient(
                http = PairingClient.pinnedTo(observed.identityFingerprint),
                baseUrl = endpoint.httpsBaseUrl,
                displayName = "Android phone",
            )

            val paired = assertIs<PairingOutcome.Paired>(
                client.pairByDiscovery(observed.confirmedByUser(), bridge.bridgeId),
            )

            assertEquals(observed.identityFingerprint, paired.identityFingerprint)
            assertTrue(bridge.paths().any { it.startsWith("/api/v1/pairing/request") })
        } finally {
            bridge.stop()
        }
    }

    @Test
    fun aClientPinnedToTheWrongFingerprintSendsNothingAtAll() = runBlocking {
        val bridge = FakeBridgeServer()

        try {
            val endpoint = bridge.start()

            // Somebody confirmed a different identity — or the identity was replaced after the code
            // was read. Either way the handshake must fail before a request exists.
            val otherIdentity = TestPki.newAuthority("somebody else")

            val client = PairingClient(
                http = PairingClient.pinnedTo(otherIdentity.fingerprint),
                baseUrl = endpoint.httpsBaseUrl,
                displayName = "Android phone",
            )

            val observed = BridgeIdentityProbe().probe(endpoint)

            val failure = runCatching {
                client.pairByDiscovery(observed.confirmedByUser(), bridge.bridgeId)
            }.exceptionOrNull()

            assertNotNull(failure, "a pin that does not match the Bridge must fail the handshake")
            assertTrue(
                bridge.authorizationHeaders().isEmpty(),
                "no credential may be sent to a Bridge that failed the pin",
            )
        } finally {
            bridge.stop()
        }
    }

    // --- H: identity mismatch in the pinned pairing connection fails before any credential -------

    @Test
    fun aPinnedPairingConnectionToAReplacedIdentityFailsBeforeAnythingIsSent() = runBlocking {
        val real = FakeBridgeServer()
        val impostor = FakeBridgeServer(TestPki.newAuthority("Codex Quota Bridge"))

        try {
            val realEndpoint = real.start()
            val impostorEndpoint = impostor.start()

            // Pinned to the real Bridge's identity, but pointed at the impostor's address.
            val client = PairingClient(
                http = PairingClient.pinnedTo(real.identityFingerprint),
                baseUrl = impostorEndpoint.httpsBaseUrl,
                displayName = "Android phone",
            )

            val failure = runCatching {
                client.pairByDiscovery(
                    BridgeIdentityProbe().probe(realEndpoint).confirmedByUser(),
                    real.bridgeId,
                )
            }.exceptionOrNull()

            assertNotNull(failure)
            assertTrue(
                impostor.received.isEmpty(),
                "the impostor must receive nothing, but it saw ${impostor.received.map { it.path }}",
            )
        } finally {
            real.stop()
            impostor.stop()
        }
    }

    @Test
    fun thePinnedTrustManagerRefusesAReplacedIdentity() {
        val real = TestPki.newAuthority("Codex Quota Bridge")
        val replacement = TestPki.newAuthority("Codex Quota Bridge")

        val trust = BootstrapFingerprintTrustManager(real.fingerprint)

        // A completely valid chain, just not the pinned one.
        val leaf = TestPki.leafFor(replacement, FakeBridgeServer.LOCALHOST)

        assertFailsWith<CertificateException> {
            trust.checkServerTrusted(arrayOf(leaf.certificate, replacement.certificate), "RSA")
        }
    }

    // --- I: a changed already-paired identity never receives the credential ---------------------

    @Test
    fun aChangedIdentityAfterPairingReceivesNoCredential() = runBlocking {
        val real = FakeBridgeServer()
        val impostor = FakeBridgeServer(TestPki.newAuthority("Codex Quota Bridge"))

        try {
            val realEndpoint = real.start()
            val impostorEndpoint = impostor.start()

            val paired = pairedBridge(real, realEndpoint)

            // The same pairing, now pointing at a different Bridge's address — a replaced identity.
            val redirected = paired.copy(endpoint = impostorEndpoint)

            val failure = runCatching {
                OkHttpBridgeApi.create(redirected, SAMPLE_CREDENTIAL.toByteArray(Charsets.UTF_8)).fetchQuota()
            }.exceptionOrNull()

            assertNotNull(failure, "a replaced identity must fail the handshake")
            assertTrue(
                impostor.received.isEmpty(),
                "the credential must never reach a replaced identity, but it saw " +
                    "${impostor.received.map { it.path }}",
            )
            assertTrue(impostor.authorizationHeaders().isEmpty())
        } finally {
            real.stop()
            impostor.stop()
        }
    }

    @Test
    fun theSameIdentityOnANewAddressStillWorks() = runBlocking {
        // The identity is the trust anchor and the address is only a location, so a Bridge that has
        // moved must not require re-pairing.
        val bridge = FakeBridgeServer()

        try {
            val endpoint = bridge.start()
            val paired = pairedBridge(bridge, endpoint)

            val snapshot = OkHttpBridgeApi
                .create(paired, SAMPLE_CREDENTIAL.toByteArray(Charsets.UTF_8))
                .fetchQuota()

            assertEquals(72.0, snapshot.shortWindow.remainingPercent)
        } finally {
            bridge.stop()
        }
    }

    @Test
    fun theIdentityThatWasVerifiedIsTheOneThatIsStored() = runBlocking {
        val bridge = FakeBridgeServer()

        try {
            val endpoint = bridge.start()
            val paired = pairedBridge(bridge, endpoint)

            // The pin must be the identity, not the leaf that happened to be presented: the leaf
            // rotates and the identity does not.
            assertEquals(bridge.identityFingerprint, paired.identityFingerprint)
            assertEquals(
                bridge.identityFingerprint,
                BridgeIdentity.spkiSha256(
                    java.security.cert.CertificateFactory.getInstance("X.509")
                        .generateCertificate(paired.identityCertificateDer.inputStream()) as X509Certificate,
                ),
            )
        } finally {
            bridge.stop()
        }
    }

    // --- helpers --------------------------------------------------------------------------------

    private suspend fun pairedBridge(bridge: FakeBridgeServer, endpoint: BridgeEndpoint): PairedBridge {
        val observed = BridgeIdentityProbe().probe(endpoint)

        bridge.approvePairing()

        val client = PairingClient(
            http = PairingClient.pinnedTo(observed.identityFingerprint),
            baseUrl = endpoint.httpsBaseUrl,
            displayName = "Android phone",
        )

        val outcome = assertIs<PairingOutcome.Paired>(
            client.pairByDiscovery(observed.confirmedByUser(), bridge.bridgeId),
        )

        val stored = PairedBridge(
            bridgeId = outcome.bridgeId,
            displayName = "Android phone",
            identityCertificateDer = outcome.identityCertificateDer,
            identityFingerprint = outcome.identityFingerprint,
            endpoint = endpoint,
            credential = store.sealCredential(SAMPLE_CREDENTIAL.toByteArray(Charsets.UTF_8)),
        )

        store.save(stored)

        return assertNotNull(store.load())
    }

    /** Whether a failure is the app refusing a certificate rather than a transport problem. */
    private fun Throwable.isCertificateProblem(): Boolean =
        this is CertificateException ||
            this is IdentityMismatchException ||
            cause is CertificateException ||
            (this is javax.net.ssl.SSLException)

    /** A storage double, so nothing of this test reaches a real file. */
    private class MemoryStorage : SecretStorage {
        private var content: String? = null

        override fun read(): String? = content

        override fun write(content: String) {
            this.content = content
        }

        override fun clear() {
            content = null
        }
    }

    /** A reversible box, so the test does not depend on a Keystore the JVM does not have. */
    private class PlainBox : SecretBox {
        override fun seal(plaintext: ByteArray): SealedSecret =
            SealedSecret(ciphertext = plaintext.copyOf(), iv = byteArrayOf(1, 2, 3, 4))

        override fun open(sealed: SealedSecret): ByteArray = sealed.ciphertext.copyOf()
    }

    private companion object {
        const val SAMPLE_CREDENTIAL = "sample-device-token-not-a-secret"
    }
}
