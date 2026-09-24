package com.codexquota.app.data.security

import java.io.ByteArrayInputStream
import java.security.KeyStore
import java.security.cert.CertificateFactory
import java.security.cert.X509Certificate
import javax.net.ssl.SSLContext
import javax.net.ssl.TrustManagerFactory
import javax.net.ssl.X509TrustManager
import java.security.cert.PKIXBuilderParameters
import java.security.cert.X509CertSelector
import javax.net.ssl.CertPathTrustManagerParameters

/**
 * The trust a paired phone uses from then on.
 *
 * Once pairing has happened the identity certificate is known, so this is ordinary PKIX validation
 * anchored at that one certificate: the presented leaf has to chain to the stored Bridge identity,
 * and nothing else is trusted. A leaf renewal keeps working because the anchor is the identity, and
 * a replaced identity stops working because the anchor is not the replacement.
 *
 * Hostname verification is deliberately not touched here. OkHttp keeps its normal verifier, which
 * checks the leaf's SAN against the address the phone dialled.
 */
class PairedBridgeTrustManagerFactory(identityCertificateDer: ByteArray) {

    private val identity: X509Certificate = parse(identityCertificateDer)

    /** The pinned fingerprint of the stored identity, for display and for comparison. */
    val identityFingerprint: String get() = BridgeIdentity.spkiSha256(identity)

    /** The human comparison code for the stored identity. */
    val identityVerificationCode: String
        get() = BridgeIdentity.humanVerificationCode(identityFingerprint)

    /** A trust manager that accepts exactly the chains anchored at the stored Bridge identity. */
    fun trustManager(): X509TrustManager {
        val keyStore = KeyStore.getInstance(KeyStore.getDefaultType()).apply {
            load(null)
            setCertificateEntry(IDENTITY_ALIAS, identity)
        }

        val factory = TrustManagerFactory.getInstance("PKIX")

        // A PKIX trust manager with an explicit builder is what turns "these are trusted roots" into
        // "a chain must terminate here", which is the property being relied on.
        val parameters = PKIXBuilderParameters(keyStore, X509CertSelector()).apply {
            // The Bridge is a LAN service with no CRL or OCSP responder, so revocation checking would
            // fail closed on every connection. Disabling it is a deliberate, documented choice; it is
            // not a bypass of the identity check above.
            isRevocationEnabled = false
        }

        factory.init(CertPathTrustManagerParameters(parameters))

        return factory.trustManagers
            .filterIsInstance<X509TrustManager>()
            .firstOrNull()
            ?: error("The PKIX TrustManagerFactory produced no X509TrustManager.")
    }

    /** An SSL context whose only trust anchor is the stored Bridge identity. */
    fun sslContext(): SSLContext = SSLContext.getInstance("TLS").apply {
        init(null, arrayOf(trustManager()), null)
    }

    companion object {
        private const val IDENTITY_ALIAS = "codexquota-bridge-identity"

        /** Parses a DER-encoded certificate. */
        fun parse(der: ByteArray): X509Certificate =
            CertificateFactory.getInstance("X.509")
                .generateCertificate(ByteArrayInputStream(der)) as X509Certificate
    }
}
