package com.codexquota.app.data.security

import java.security.MessageDigest
import java.security.cert.CertificateException
import java.security.cert.X509Certificate

/**
 * The Bridge's stable identity, and how it is compared.
 *
 * The identity is a long-lived certificate that anchors replaceable TLS leaf certificates. The
 * pinned value is the SHA-256 of the identity's **SubjectPublicKeyInfo**, not of the certificate
 * body: the SPKI is stable across re-issuance of the same key, so a renewed identity certificate
 * keeps the same pin, while a genuinely replaced identity does not.
 *
 * The single most important rule in this file: the pin is always computed over the *identity*, and
 * never over the leaf the server happens to present. A leaf rotates freely; equating it with the
 * identity would break every renewal, and pinning it would be pinning the wrong thing.
 */
object BridgeIdentity {

    /** Number of hex characters that make up the human comparison code. */
    const val VERIFICATION_CODE_LENGTH = 16

    /**
     * SHA-256 over the certificate's `SubjectPublicKeyInfo`, as uppercase hex.
     *
     * Uppercase hex matches the Windows Bridge's `CertificateFingerprint.ComputeSpkiSha256`, so a
     * value read off the Windows pairing window compares equal to one computed here.
     */
    fun spkiSha256(certificate: X509Certificate): String =
        MessageDigest.getInstance("SHA-256")
            .digest(certificate.publicKey.encoded)
            .joinToString(separator = "") { "%02X".format(it) }

    /**
     * Turns the fingerprint prefix into the `A1B2-C3D4-E5F6-7890` form a person reads aloud.
     *
     * This is what the user compares between the Windows pairing window and the phone, so it has to
     * be produced identically on both sides.
     */
    fun humanVerificationCode(spkiSha256: String): String {
        require(spkiSha256.length >= VERIFICATION_CODE_LENGTH) {
            "A fingerprint needs at least $VERIFICATION_CODE_LENGTH hex characters."
        }

        val prefix = spkiSha256.take(VERIFICATION_CODE_LENGTH).uppercase()

        return prefix.chunked(4).joinToString("-")
    }

    /** Whether two fingerprints name the same identity. Case is not significant in hex. */
    fun matches(expected: String, actual: String): Boolean = expected.equals(actual, ignoreCase = true)
}

/**
 * Verifies that a presented chain really belongs to the expected Bridge identity.
 *
 * This is the check the app performs *before* it will send a device credential, and it is the reason
 * the app can bootstrap trust from a QR code that carries only a public fingerprint.
 *
 * It is not a trust-all verifier. It refuses a chain that does not terminate at the expected
 * identity, and it refuses one whose signatures or validity dates do not hold.
 */
object BridgeIdentityVerifier {

    /** What `getBasicConstraints` returns for a certificate that is not a CA. */
    private const val NOT_A_CA = -1

    /**
     * Checks that a chain is internally sound, without pinning it to anything.
     *
     * This is the part of the check that can be made before the expected identity is known: every
     * certificate is in date, each one is signed by the next, and anything that issued another
     * certificate really is a certificate authority.
     *
     * @throws CertificateException when the chain is not a usable certificate chain.
     */
    fun verifyShape(chain: Array<X509Certificate>) {
        if (chain.isEmpty()) {
            throw CertificateException("The server presented no certificate.")
        }

        // Every certificate must be signed by the one after it. Without this, a caller could present
        // an unrelated leaf next to the real identity certificate and pass the pin check below.
        for (index in 0 until chain.size - 1) {
            chain[index].checkValidity()

            try {
                chain[index].verify(chain[index + 1].publicKey)
            } catch (e: Exception) {
                throw CertificateException(
                    "Certificate $index is not signed by certificate ${index + 1}.",
                    e,
                )
            }
        }

        val identity = chain.last()
        identity.checkValidity()

        // `X509Certificate.getBasicConstraints` is easy to read backwards, so it is spelled out:
        //   -1                 -> the subject is NOT a CA
        //   n >= 0             -> a CA whose paths may be at most n certificates long
        //   Integer.MAX_VALUE  -> a CA with no path-length limit
        // Only the first of those is a refusal.
        if (chain.size > 1 && identity.basicConstraints == NOT_A_CA) {
            throw CertificateException("The Bridge identity certificate is not a certificate authority.")
        }
    }

    /**
     * Checks a server-presented chain against an expected identity fingerprint.
     *
     * @throws CertificateException when the chain is not the expected Bridge identity.
     */
    fun verify(chain: Array<X509Certificate>, expectedIdentityFingerprint: String) {
        verifyShape(chain)

        val identity = chain.last()
        val actual = BridgeIdentity.spkiSha256(identity)

        if (!BridgeIdentity.matches(expectedIdentityFingerprint, actual)) {
            throw CertificateException(
                "The Bridge identity does not match the pinned one. " +
                    "Expected $expectedIdentityFingerprint but the server presented $actual.",
            )
        }
    }

    /**
     * The identity certificate of a presented chain.
     *
     * The identity is the last certificate: with the Bridge's model that is the CA that signed the
     * rotating leaf, and for a self-signed presentation it is the leaf itself.
     */
    fun identityOf(chain: Array<X509Certificate>): X509Certificate {
        if (chain.isEmpty()) {
            throw CertificateException("The server presented no certificate.")
        }

        return chain.last()
    }
}
