package com.codexquota.app.data.security

import java.io.ByteArrayInputStream
import java.security.cert.CertificateException
import java.security.cert.CertificateFactory
import java.security.cert.X509Certificate
import javax.net.ssl.SSLContext
import javax.net.ssl.X509TrustManager

/**
 * The trust a paired phone uses from then on.
 *
 * Once pairing has happened the identity certificate is known, so this accepts exactly the chains
 * that terminate at that identity: the presented leaf has to be signed by the stored Bridge identity,
 * and nothing else is trusted. A leaf renewal keeps working because the anchor is the identity, and a
 * replaced identity stops working because the anchor is not the replacement.
 *
 * Hostname verification is deliberately not touched here. OkHttp keeps its normal verifier, which
 * checks the leaf's SAN against the address the phone dialled.
 *
 * ## Why this is a hand-written trust manager rather than a `TrustManagerFactory`
 *
 * It used to build a PKIX `TrustManagerFactory` from `CertPathTrustManagerParameters`. That works on
 * the JVM, which is why every JVM test passed, and **fails on Android**: the platform's
 * `RootTrustManagerFactorySpi` accepts only its own `ApplicationConfigParameters` and throws
 * `InvalidAlgorithmParameterException: Unsupported spec` for anything else. The effect would have
 * been that the authenticated REST and WebSocket clients could never connect on a real device — the
 * app would pair successfully and then fail every request with a TLS error.
 *
 * The instrumentation gate found it. `BridgeIdentityVerifier` already performs the same checks that
 * matter — every certificate in date, each signed by the next, an issuer that really is a certificate
 * authority, and a chain that terminates at the pinned identity — and it does so with no
 * platform-specific API.
 */
class PairedBridgeTrustManagerFactory(identityCertificateDer: ByteArray) {

    private val identity: X509Certificate = parse(identityCertificateDer)

    /** The pinned fingerprint of the stored identity, for display and for comparison. */
    val identityFingerprint: String = BridgeIdentity.spkiSha256(identity)

    /** The human comparison code for the stored identity. */
    val identityVerificationCode: String
        get() = BridgeIdentity.humanVerificationCode(identityFingerprint)

    /** A trust manager that accepts exactly the chains anchored at the stored Bridge identity. */
    fun trustManager(): X509TrustManager = PinnedIdentityTrustManager(identityFingerprint)

    /** An SSL context whose only trust anchor is the stored Bridge identity. */
    fun sslContext(): SSLContext = SSLContext.getInstance("TLS").apply {
        init(null, arrayOf(trustManager()), null)
    }

    companion object {
        /** Parses a DER-encoded certificate. */
        fun parse(der: ByteArray): X509Certificate =
            CertificateFactory.getInstance("X.509")
                .generateCertificate(ByteArrayInputStream(der)) as X509Certificate
    }
}

/**
 * Accepts a server chain only when it is the pinned Bridge identity.
 *
 * Revocation is not checked. The Bridge is a LAN service with no CRL or OCSP responder, so checking
 * would fail closed on every connection. That is a deliberate, documented reduction in checking; it
 * does not weaken the identity check, which is the property being relied on.
 */
internal class PinnedIdentityTrustManager(
    private val expectedIdentityFingerprint: String,
) : X509TrustManager {

    override fun checkClientTrusted(chain: Array<out X509Certificate>?, authType: String?) {
        throw CertificateException("This trust manager only validates servers.")
    }

    override fun checkServerTrusted(chain: Array<out X509Certificate>?, authType: String?) {
        if (chain == null || chain.isEmpty()) {
            throw CertificateException("The server presented no certificate.")
        }

        @Suppress("UNCHECKED_CAST")
        val presented = chain as Array<X509Certificate>

        // Validity dates, signatures, certificate-authority shape, and the pin — the same check the
        // pairing handshake performs, so a paired connection is held to the standard that established
        // it rather than to a weaker one.
        BridgeIdentityVerifier.verify(presented, expectedIdentityFingerprint)
    }

    /**
     * No issuer is ever trusted in general.
     *
     * A non-empty list would turn this into a trust store for something other than the paired
     * Bridge.
     */
    override fun getAcceptedIssuers(): Array<X509Certificate> = emptyArray()
}
