package com.codexquota.app

import android.content.Context
import androidx.room.Room
import androidx.test.core.app.ApplicationProvider
import androidx.test.ext.junit.runners.AndroidJUnit4
import org.junit.runner.RunWith
import com.codexquota.app.data.QuotaRepository
import com.codexquota.app.data.RefreshResult
import com.codexquota.app.data.api.ApiErrorCodes
import com.codexquota.app.data.api.OkHttpBridgeApi
import com.codexquota.app.data.db.AppDatabase
import com.codexquota.app.data.db.RoomQuotaCache
import com.codexquota.app.data.discovery.BridgeIdentityProbe
import com.codexquota.app.data.pairing.PairingClient
import com.codexquota.app.data.pairing.PairingOutcome
import com.codexquota.app.data.security.BridgeEndpoint
import com.codexquota.app.data.security.KeystoreSecretBox
import com.codexquota.app.data.security.PairedBridge
import com.codexquota.app.data.security.PairedBridgeStore
import com.codexquota.app.data.security.SecretBox
import com.codexquota.app.data.security.SecretStorage
import com.codexquota.app.data.ws.BridgeWebSocketEvent
import com.codexquota.app.data.ws.OkHttpBridgeWebSocket
import com.codexquota.app.testing.FakeBridgeServer
import com.codexquota.app.testing.TestPki
import kotlin.test.AfterTest
import kotlin.test.BeforeTest
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertIs
import kotlin.test.assertNotNull
import kotlin.test.assertTrue
import java.util.concurrent.CopyOnWriteArrayList
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.runBlocking

/**
 * The Stage C runtime gate.
 *
 * Everything below runs against a real TLS server presenting a real certificate chain, over the
 * app's real networking code. Nothing here is a fake standing in for the thing under test: the pin
 * is the app's pin, the JSON is the app's mapper, and the handshake is a real handshake. That is the
 * difference between this and the JVM tests, and it is the reason the gate needs a device.
 *
 * The scenario is the approved one end to end: discover, confirm, pair, fetch, stream, disconnect.
 */
@RunWith(AndroidJUnit4::class)
class StageCIntegrationTest {

    private lateinit var context: Context
    private lateinit var bridge: FakeBridgeServer
    private lateinit var endpoint: BridgeEndpoint
    private var database: AppDatabase? = null

    @BeforeTest
    fun setUp() {
        context = ApplicationProvider.getApplicationContext()
        bridge = FakeBridgeServer()
        endpoint = bridge.start()
    }

    @AfterTest
    fun tearDown() {
        bridge.stop()
        database?.close()
    }

    private fun repository(): QuotaRepository {
        val db = database()

        return QuotaRepository(
            OkHttpBridgeApi.create(pairedBridge(), credential()),
            RoomQuotaCache(db.quotaDao()),
        )
    }

    /**
     * An in-memory database, one per test.
     *
     * A file-backed one persisted between tests, so a test could observe a snapshot an earlier test
     * had cached — which made "a bad document must not overwrite the cache" and "a fresh fetch is
     * `Updated`" fail for reasons unrelated to the behaviour under test.
     */
    private fun database(): AppDatabase {
        database?.let { return it }

        val db = Room.inMemoryDatabaseBuilder(context, AppDatabase::class.java)
            .allowMainThreadQueries()
            .build()

        database = db

        return db
    }

    /** A store over an in-memory file, so nothing of this test reaches the device's real storage. */
    private fun store(secretBox: SecretBox = KeystoreSecretBox()): PairedBridgeStore =
        PairedBridgeStore(storage = MemoryStorage(), secretBox = secretBox)

    private fun pairedBridge(): PairedBridge = runBlocking {
        val discovered = BridgeIdentityProbe().probe(endpoint)
        bridge.approvePairing()

        val client = PairingClient(
            http = PairingClient.pinnedTo(discovered.identityFingerprint),
            baseUrl = endpoint.httpsBaseUrl,
            displayName = "Android phone",
        )

        val outcome = client.pairByDiscovery(discovered.confirmedByUser(), bridge.bridgeId)
        val paired = assertIs<PairingOutcome.Paired>(outcome)

        val store = store()

        store.save(
            PairedBridge(
                bridgeId = paired.bridgeId,
                displayName = "Android phone",
                identityCertificateDer = paired.identityCertificateDer,
                identityFingerprint = paired.identityFingerprint,
                endpoint = endpoint,
                credential = store.sealCredential(paired.credential.token.toByteArray(Charsets.UTF_8)),
            ),
        )

        return@runBlocking assertNotNull(store.load(), "the pairing must be stored")
    }

    private fun credential(): ByteArray =
        FakeBridgeServer.SAMPLE_TOKEN.toByteArray(Charsets.UTF_8)

    // --- the approved end-to-end scenario -------------------------------------------------------

    @Test
    fun discoverConfirmPairFetchAndStream() = runBlocking {
        // 1. Discovery observes the identity without trusting it, over a real handshake.
        val discovered = BridgeIdentityProbe().probe(endpoint)

        assertEquals(bridge.identityFingerprint, discovered.identityFingerprint)
        assertEquals(bridge.verificationCode, discovered.verificationCode)

        // The probe touched only the anonymous liveness route, and carried no credential.
        assertEquals(listOf("/api/v1/health"), bridge.paths())
        assertTrue(
            bridge.authorizationHeaders().isEmpty(),
            "the identity probe must never carry a credential",
        )

        // 2. Nothing was paired merely by looking.
        assertTrue(
            bridge.paths().none { it.startsWith("/api/v1/pairing") },
            "observing an identity must not start a pairing",
        )

        // 3. A person confirms, and only then does a pairing request exist.
        bridge.approvePairing()

        val pairingClient = PairingClient(
            http = PairingClient.pinnedTo(discovered.identityFingerprint),
            baseUrl = endpoint.httpsBaseUrl,
            displayName = "Android phone",
        )

        val paired = assertIs<PairingOutcome.Paired>(
            pairingClient.pairByDiscovery(discovered.confirmedByUser(), bridge.bridgeId),
        )

        assertEquals(bridge.identityFingerprint, paired.identityFingerprint)
        assertEquals(FakeBridgeServer.SAMPLE_TOKEN, paired.credential.token)

        // The pairing handshake still carried no Bearer: the credential does not exist until the
        // very end of it, and it is never sent back.
        assertTrue(
            bridge.authorizationHeaders().isEmpty(),
            "no request before the credential exists may carry one",
        )

        // 4. The credential is sealed, and the plaintext is not durable.
        val secretBox = KeystoreSecretBox()
        val store = store(secretBox)
        val sealed = store.sealCredential(paired.credential.token.toByteArray(Charsets.UTF_8))

        assertFalse(
            sealed.ciphertext.toString(Charsets.UTF_8).contains(FakeBridgeServer.SAMPLE_TOKEN),
            "the sealed credential must not contain the plaintext token",
        )
        assertEquals(
            FakeBridgeServer.SAMPLE_TOKEN,
            secretBox.open(sealed).toString(Charsets.UTF_8),
            "the Keystore-backed box must round-trip the credential",
        )

        val pairedBridge = PairedBridge(
            bridgeId = paired.bridgeId,
            displayName = "Android phone",
            identityCertificateDer = paired.identityCertificateDer,
            identityFingerprint = paired.identityFingerprint,
            endpoint = endpoint,
            credential = sealed,
        )

        // 5. The authenticated API is built from the pairing, and it does carry the credential.
        val api = OkHttpBridgeApi.create(pairedBridge, secretBox.open(sealed))

        val snapshot = api.fetchQuota()

        assertEquals(72.0, snapshot.shortWindow.remainingPercent)
        assertEquals(54.0, snapshot.weekly.remainingPercent)
        assertEquals(1, bridge.authorizationHeaders().size)

        // 6. The 24-hour history comes from the same shared fixture the Windows tests use.
        val history = api.fetchHistory(24)

        assertEquals(3, history.size)
        assertEquals(81.0, history.first().shortWindowRemainingPercent)

        // 7. The live path: a real WebSocket upgrade, and one real frame.
        val socket = OkHttpBridgeWebSocket.create(pairedBridge, secretBox.open(sealed)).connect(endpoint)

        // The event flow is collected ONCE, into a list, and the test waits on the list.
        //
        // Collecting it twice was wrong. `events()` opens a new WebSocket each time it is collected,
        // and completing the flow closes the socket that collection owned — so the second collection
        // dialled a second connection while the pushed frame went to the first one, and the test
        // timed out with nothing to show for it.
        val received = CopyOnWriteArrayList<BridgeWebSocketEvent>()
        val collector = launch { socket.events().collect { received += it } }

        try {
            val hello = awaitEvent(received, "a hello") { it is BridgeWebSocketEvent.Hello }

            assertIs<BridgeWebSocketEvent.Hello>(hello)
            assertTrue(bridge.hasLiveConnection, "the app did not hold an upgraded connection")

            bridge.pushQuotaFrame(sequence = 1, shortRemainingPercent = 8.0)

            val frame = assertIs<BridgeWebSocketEvent.QuotaUpdated>(
                awaitEvent(received, "the pushed frame") { it is BridgeWebSocketEvent.QuotaUpdated },
            )

            assertEquals(1L, frame.sequence)
            assertEquals(8.0, frame.snapshot.shortWindow.remainingPercent)
            // Used and remaining are complements in the v1 contract; the frame is internally sound.
            assertEquals(92.0, frame.snapshot.shortWindow.usedPercent)
        } finally {
            collector.cancel()
            socket.close()
        }
    }

    /**
     * Waits for an event, and names what did arrive when none does.
     *
     * A bare timeout says nothing about where the live path stopped; the list and the paths the
     * server saw say whether the upgrade happened at all.
     */
    private suspend fun awaitEvent(
        received: List<BridgeWebSocketEvent>,
        description: String,
        timeoutMillis: Long = TIMEOUT_MILLIS,
        predicate: (BridgeWebSocketEvent) -> Boolean,
    ): BridgeWebSocketEvent {
        val deadline = System.currentTimeMillis() + timeoutMillis

        while (System.currentTimeMillis() < deadline) {
            received.firstOrNull(predicate)?.let { return it }

            delay(POLL_MILLIS)
        }

        throw AssertionError(
            description + " never arrived within " + timeoutMillis + "ms. Events received: " +
                received + ". Paths the server saw: " + bridge.paths() +
                " - an upgrade request is /api/v1/ws.",
        )
    }

    @Test
    fun aTrustedSnapshotSurvivesTheConnectionGoingAwayAndIsLabelledStale() = runBlocking {
        val repository = repository()

        // A live connection produced a snapshot, so it is current.
        val refreshed = repository.refreshCurrent()

        assertIs<RefreshResult.Updated>(refreshed)
        assertEquals(72.0, repository.current().snapshot?.shortWindow?.remainingPercent)
        assertFalse(repository.current().stale, "a freshly fetched snapshot is not stale")

        // The Bridge goes away.
        bridge.stop()

        val offline = repository.refreshCurrent()

        assertIs<RefreshResult.Offline>(offline)

        // The trusted values are still visible...
        val cached = repository.current()

        assertEquals(
            72.0,
            cached.snapshot?.shortWindow?.remainingPercent,
            "the last trusted snapshot must remain visible while offline",
        )
        assertEquals(54.0, cached.snapshot?.weekly?.remainingPercent)

        // ...and are explicitly labelled as not current, rather than being presented as real-time.
        assertTrue(cached.stale, "an unreachable Bridge means the cached data is stale")
    }

    // --- the security and protocol boundaries the gate must hold --------------------------------

    @Test
    fun aMissingPercentageIsAProtocolErrorAndNeverBecomesZero() = runBlocking {
        val repository = repository()

        repository.refreshCurrent()

        // The Bridge answers, but its document is not a v1 quota document.
        bridge.quotaJson = """{"schemaVersion":1,"windows":{"shortWindow":{"usedPercent":28.0}}}"""

        val result = repository.refreshCurrent()

        assertIs<RefreshResult.ProtocolError>(result)

        val cached = repository.current()

        // The point of the assertion: a missing field must never be rendered as 0%.
        assertEquals(72.0, cached.snapshot?.shortWindow?.remainingPercent)
        assertTrue(cached.stale)
    }

    @Test
    fun anOutOfRangePercentageIsRefusedRatherThanClamped() = runBlocking {
        val repository = repository()

        bridge.quotaJson = bridge.quotaJson.replace("\"remainingPercent\": 72.0", "\"remainingPercent\": 172.0")

        val result = repository.refreshCurrent()

        assertIs<RefreshResult.ProtocolError>(result)
        assertEquals(null, repository.current().snapshot, "nothing may be invented from a bad document")
    }

    @Test
    fun aBridgeWithAReplacedIdentityNeverReceivesTheCredential() = runBlocking {
        // A second Bridge, with its own identity, listening on its own port.
        val impostor = FakeBridgeServer(TestPki.newAuthority("Codex Quota Bridge"))

        try {
            val impostorEndpoint = impostor.start()

            val paired = pairedBridge()

            // The app is pinned to the first Bridge's identity, but is now pointed at the second's
            // address — which is what a replaced identity looks like on the wire.
            val redirected = paired.copy(endpoint = impostorEndpoint)

            val failure = runCatching {
                OkHttpBridgeApi.create(redirected, credential()).fetchQuota()
            }.exceptionOrNull()

            assertNotNull(failure, "a replaced identity must fail the handshake")

            // The strongest form of the assertion: the impostor received nothing at all, so no
            // credential and no request ever reached it.
            assertTrue(
                impostor.received.isEmpty(),
                "no request may reach a Bridge whose identity is not the pinned one, but it saw " +
                    "${impostor.received.map { it.path }}",
            )
            assertTrue(impostor.authorizationHeaders().isEmpty())
        } finally {
            impostor.stop()
        }
    }

    @Test
    fun theCredentialIsAttachedOnlyAfterPairedTrustExists() = runBlocking {
        // Before pairing: the probe and the pairing handshake carry nothing.
        BridgeIdentityProbe().probe(endpoint)
        bridge.approvePairing()

        val discovered = BridgeIdentityProbe().probe(endpoint)
        val pairingClient = PairingClient(
            http = PairingClient.pinnedTo(discovered.identityFingerprint),
            baseUrl = endpoint.httpsBaseUrl,
            displayName = "Android phone",
        )

        assertIs<PairingOutcome.Paired>(pairingClient.pairByDiscovery(discovered.confirmedByUser(), bridge.bridgeId))

        assertTrue(
            bridge.authorizationHeaders().isEmpty(),
            "no bootstrap request may carry the device credential",
        )

        // After pairing: the authenticated request carries exactly one, and it is a Bearer.
        val paired = pairedBridge()
        val credential = credential()

        OkHttpBridgeApi.create(paired, credential).fetchQuota()

        val headers = bridge.authorizationHeaders()

        assertEquals(1, headers.size)
        assertTrue(headers.single().startsWith("Bearer "), "the credential travels as a Bearer token")
        assertTrue(headers.single().contains(FakeBridgeServer.SAMPLE_TOKEN))
    }

    @Test
    fun aRejectedCredentialIsReportedAsRepairRequiredAndKeepsTheCachedQuota() = runBlocking {
        val repository = repository()

        repository.refreshCurrent()

        bridge.quotaFailure = 401 to ApiErrorCodes.DEVICE_UNAUTHORIZED

        val result = repository.refreshCurrent()

        assertEquals(RefreshResult.RepairRequired, result)
        assertEquals(
            72.0,
            repository.current().snapshot?.shortWindow?.remainingPercent,
            "a rejected credential must not erase the last trusted values",
        )
    }

    @Test
    fun anUnreachableBridgeIsAnOfflineStateAndNotASecurityState() = runBlocking {
        val repository = repository()

        bridge.stop()

        val result = repository.refreshCurrent()

        // The distinction matters: offline is retried, a security error is not.
        assertIs<RefreshResult.Offline>(result)

        Unit
    }

    /** A storage double, so the test never touches the device's real pairing file. */
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

    private companion object {
        const val TIMEOUT_MILLIS = 15_000L
        const val POLL_MILLIS = 50L
    }
}
