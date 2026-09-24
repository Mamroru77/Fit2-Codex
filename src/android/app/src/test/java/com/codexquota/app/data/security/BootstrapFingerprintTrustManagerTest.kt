package com.codexquota.app.data.security

import com.codexquota.app.testing.TestPki
import java.security.cert.CertificateException
import java.security.cert.X509Certificate
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertNotNull
import kotlin.test.assertTrue

/**
 * The bootstrap verifier is the check that stands between "the phone has a fingerprint" and "the
 * phone has sent anything at all", so its refusals matter more than its acceptances.
 */
class BootstrapFingerprintTrustManagerTest {

    private val identity = TestPki.newAuthority("Codex Quota Bridge")

    @Test
    fun aLeafSignedByThePinnedIdentityIsAccepted() {
        val leaf = TestPki.leafFor(identity, "192.168.1.20")
        val trust = BootstrapFingerprintTrustManager(identity.fingerprint)

        trust.checkServerTrusted(arrayOf(leaf.certificate, identity.certificate), "RSA")

        // The recorded identity is the authority, never the leaf: pinning the leaf would break every
        // renewal, which is the mistake this test exists to prevent.
        assertEquals(identity.fingerprint, BridgeIdentity.spkiSha256(assertNotNull(trust.lastVerifiedIdentity)))
        assertTrue(trust.lastVerifiedIdentity != leaf.certificate)
    }

    @Test
    fun aLeafFromAReplacedIdentityIsRefused() {
        // A completely valid chain — just not the Bridge that was paired with.
        val replacement = TestPki.newAuthority("Codex Quota Bridge")
        val leaf = TestPki.leafFor(replacement, "192.168.1.20")
        val trust = BootstrapFingerprintTrustManager(identity.fingerprint)

        assertFailsWith<CertificateException> {
            trust.checkServerTrusted(arrayOf(leaf.certificate, replacement.certificate), "RSA")
        }
    }

    @Test
    fun aLeafWhoseIssuerIsNotThePinnedIdentityIsRefusedEvenIfTheChainLooksFine() {
        val impostor = TestPki.newAuthority("impostor")
        val impostorLeaf = TestPki.leafFor(impostor, "192.168.1.20")
        val trust = BootstrapFingerprintTrustManager(identity.fingerprint)

        // The chain is well formed and correctly signed; it simply is not the pinned authority.
        assertFailsWith<CertificateException> {
            trust.checkServerTrusted(arrayOf(impostorLeaf.certificate, impostor.certificate), "RSA")
        }
    }

    @Test
    fun anIdentityPresentedDirectlyIsAcceptedWhenItsFingerprintMatches() {
        // The self-signed case: the server presents its identity as the leaf. Comparing the SPKI is
        // correct here precisely because the certificate *is* the identity.
        val trust = BootstrapFingerprintTrustManager(identity.fingerprint)

        trust.checkServerTrusted(arrayOf(identity.certificate), "RSA")

        assertEquals(identity.fingerprint, BridgeIdentity.spkiSha256(assertNotNull(trust.lastVerifiedIdentity)))
    }

    @Test
    fun aChainThatIsNotSignedByItsIssuerIsRefused() {
        // A leaf from one authority presented next to a different authority's certificate. Without
        // the signature check, the pin below could be satisfied by an unrelated leaf.
        val other = TestPki.newAuthority("other")
        val leaf = TestPki.leafFor(other, "192.168.1.20")
        val trust = BootstrapFingerprintTrustManager(identity.fingerprint)

        assertFailsWith<CertificateException> {
            trust.checkServerTrusted(arrayOf(leaf.certificate, identity.certificate), "RSA")
        }
    }

    @Test
    fun anEmptyChainIsRefused() {
        val trust = BootstrapFingerprintTrustManager(identity.fingerprint)

        assertFailsWith<CertificateException> {
            trust.checkServerTrusted(emptyArray<X509Certificate>(), "RSA")
        }
    }

    @Test
    fun noIssuerIsEverTrustedInGeneral() {
        // A non-empty accepted-issuer list would turn this verifier into a general trust store.
        val trust = BootstrapFingerprintTrustManager(identity.fingerprint)

        assertEquals(0, trust.acceptedIssuers.size)
    }

    @Test
    fun aServerRoleCheckIsRefused() {
        val trust = BootstrapFingerprintTrustManager(identity.fingerprint)

        assertFailsWith<CertificateException> {
            trust.checkClientTrusted(arrayOf(identity.certificate), "RSA")
        }
    }

    @Test
    fun theFingerprintComparisonIgnoresCase() {
        val leaf = TestPki.leafFor(identity, "192.168.1.20")
        val trust = BootstrapFingerprintTrustManager(identity.fingerprint.lowercase())

        trust.checkServerTrusted(arrayOf(leaf.certificate, identity.certificate), "RSA")
    }

    @Test
    fun theVerificationCodeIsFourGroupsOfFour() {
        val code = BridgeIdentity.humanVerificationCode(identity.fingerprint)

        assertEquals(19, code.length)
        assertEquals(code.uppercase(), code)
        assertEquals(listOf(4, 4, 4, 4), code.split("-").map { it.length })
        assertTrue(identity.fingerprint.uppercase().startsWith(code.replace("-", "")))
    }

    @Test
    fun theVerificationCodeRefusesAFingerprintTooShortToCompare() {
        assertFailsWith<IllegalArgumentException> {
            BridgeIdentity.humanVerificationCode("ABCD")
        }
    }
}
