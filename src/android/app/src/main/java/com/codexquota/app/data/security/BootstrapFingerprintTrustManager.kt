package com.codexquota.app.data.security

import java.security.cert.CertificateException
import java.security.cert.X509Certificate
import javax.net.ssl.X509TrustManager

/**
 * The trust manager used **only** while bootstrapping trust.
 *
 * It exists for the window between "the phone has learned the Bridge identity fingerprint" and "the
 * phone has stored the identity certificate", which is exactly the pairing handshake. During that
 * window there is no stored trust anchor to build a normal PKIX trust manager from, so this one
 * checks the chain against the fingerprint the user confirmed.
 *
 * It is deliberately narrow:
 *
 * - it accepts nothing unless the presented chain terminates at the expected identity;
 * - it never returns accepted issuers, so it cannot be reused as a general-purpose trust store;
 * - it never answers "true" to a hostname question — hostname verification is left to the caller,
 *   which keeps OkHttp's normal SAN checking in force.
 */
class BootstrapFingerprintTrustManager(
    private val expectedIdentityFingerprint: String,
) : X509TrustManager {

    /**
     * The identity certificate of the last chain that passed verification.
     *
     * It is recorded here rather than taken from the caller so that what gets pinned is provably the
     * certificate this verifier accepted, not a value that travelled alongside it.
     */
    @Volatile
    var lastVerifiedIdentity: X509Certificate? = null
        private set

    override fun checkClientTrusted(chain: Array<out X509Certificate>?, authType: String?) {
        // The app is a TLS client; a server-role check is a programming error, not a request failure.
        throw CertificateException("This trust manager only validates servers.")
    }

    override fun checkServerTrusted(chain: Array<out X509Certificate>?, authType: String?) {
        if (chain == null) {
            throw CertificateException("The server presented no certificate.")
        }

        @Suppress("UNCHECKED_CAST")
        val presented = chain as Array<X509Certificate>

        BridgeIdentityVerifier.verify(presented, expectedIdentityFingerprint)

        lastVerifiedIdentity = BridgeIdentityVerifier.identityOf(presented)
    }

    /**
     * Empty on purpose.
     *
     * A non-empty list would tell the TLS stack that these certificates are acceptable issuers in
     * general. The whole point of this verifier is that trust is not general: it is one identity,
     * for one handshake.
     */
    override fun getAcceptedIssuers(): Array<X509Certificate> = emptyArray()
}
