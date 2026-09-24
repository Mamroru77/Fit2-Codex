package com.codexquota.app.testing

import com.codexquota.app.data.security.BridgeIdentity
import java.math.BigInteger
import java.security.KeyPair
import java.security.KeyPairGenerator
import java.security.cert.X509Certificate
import java.util.Date
import org.bouncycastle.asn1.x500.X500Name
import org.bouncycastle.asn1.x509.BasicConstraints
import org.bouncycastle.asn1.x509.Extension
import org.bouncycastle.asn1.x509.GeneralName
import org.bouncycastle.asn1.x509.GeneralNames
import org.bouncycastle.cert.jcajce.JcaX509CertificateConverter
import org.bouncycastle.cert.jcajce.JcaX509v3CertificateBuilder
import org.bouncycastle.operator.jcajce.JcaContentSignerBuilder

/**
 * A throwaway certificate authority and the leaves it signs.
 *
 * It mirrors the Bridge's real model: one long-lived identity that signs short-lived leaves. Every
 * certificate is generated in the test process, so no private key is committed and a test that
 * "replaces the identity" really does present a different authority rather than a fixture.
 */
object TestPki {

    /** A certificate authority, standing in for a Bridge identity. */
    class Authority(
        val certificate: X509Certificate,
        val keyPair: KeyPair,
    ) {
        /** The pinned value, computed exactly as the app computes it. */
        val fingerprint: String = BridgeIdentity.spkiSha256(certificate)

        /** The human comparison code, exactly as the app derives it. */
        val verificationCode: String = BridgeIdentity.humanVerificationCode(fingerprint)
    }

    /** A server leaf and the key it belongs to. */
    class Leaf(
        val certificate: X509Certificate,
        val keyPair: KeyPair,
    )

    private var serial: Long = 1

    /** Creates an identity authority. */
    fun newAuthority(commonName: String): Authority {
        val keyPair = newKeyPair()
        val name = X500Name("CN=$commonName")

        val builder = JcaX509v3CertificateBuilder(
            name,
            nextSerial(),
            Date(System.currentTimeMillis() - DAY_MILLIS),
            Date(System.currentTimeMillis() + 3650L * DAY_MILLIS),
            name,
            keyPair.public,
        )

        // The identity is a CA: it exists to sign leaves, and the verifier refuses to treat a
        // non-CA as an anchor.
        builder.addExtension(Extension.basicConstraints, true, BasicConstraints(true))

        val certificate = JcaX509CertificateConverter()
            .getCertificate(builder.build(JcaContentSignerBuilder(SIGNATURE).build(keyPair.private)))

        return Authority(certificate, keyPair)
    }

    /** Signs a leaf for [host] with [authority]. */
    fun leafFor(authority: Authority, host: String): Leaf {
        val keyPair = newKeyPair()
        val subject = X500Name("CN=$host")

        val builder = JcaX509v3CertificateBuilder(
            X500Name(authority.certificate.subjectX500Principal.name),
            nextSerial(),
            Date(System.currentTimeMillis() - DAY_MILLIS),
            Date(System.currentTimeMillis() + 365L * DAY_MILLIS),
            subject,
            keyPair.public,
        )

        builder.addExtension(Extension.basicConstraints, true, BasicConstraints(false))
        builder.addExtension(
            Extension.subjectAlternativeName,
            false,
            GeneralNames(GeneralName(GeneralName.dNSName, host)),
        )

        val certificate = JcaX509CertificateConverter()
            .getCertificate(builder.build(JcaContentSignerBuilder(SIGNATURE).build(authority.keyPair.private)))

        return Leaf(certificate, keyPair)
    }

    /** A leaf that claims [host] but was signed by an authority nobody pinned. */
    fun leafFromAnotherAuthority(host: String): Leaf = leafFor(newAuthority("impostor"), host)

    private fun newKeyPair(): KeyPair =
        KeyPairGenerator.getInstance("RSA").apply { initialize(2048) }.generateKeyPair()

    private fun nextSerial(): BigInteger = BigInteger.valueOf(serial++)

    private const val SIGNATURE = "SHA256withRSA"
    private const val DAY_MILLIS = 24L * 60L * 60L * 1000L
}
