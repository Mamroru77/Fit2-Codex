package com.codexquota.app.data.discovery

import com.codexquota.app.data.pairing.DiscoveredBridgeIdentity
import com.codexquota.app.data.security.BridgeEndpoint
import com.codexquota.app.data.security.BridgeIdentity
import com.codexquota.app.data.security.BridgeIdentityVerifier
import java.io.IOException
import java.security.cert.CertificateException
import java.security.cert.X509Certificate
import javax.net.ssl.SSLContext
import javax.net.ssl.X509TrustManager
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import okhttp3.OkHttpClient
import okhttp3.Request

/**
 * A Bridge that DNS-SD announced on the local network.
 *
 * These fields are what the Bridge chose to publish, and the spec restricts that to non-personal
 * metadata: no account details, no username, no token, no key.
 */
data class DiscoveredService(
    val serviceName: String,
    val host: String,
    val port: Int,
    val bridgeId: String?,
    val apiVersion: String?,
) {
    /** The endpoint to connect to. */
    val endpoint: BridgeEndpoint? get() = runCatching { BridgeEndpoint(host, port) }.getOrNull()
}

/**
 * Finds Bridges on the local network.
 *
 * It is an interface because the mechanism is Android-specific (`NsdManager`) and the code that
 * reacts to a discovery is not.
 */
interface BridgeDiscovery {
    /**
     * Announces services until cancelled.
     *
     * Discovery is a stream rather than a one-shot query because the Bridge may appear, move or
     * disappear while the user is looking at the screen.
     */
    fun services(): kotlinx.coroutines.flow.Flow<DiscoveredService>
}

/**
 * Reads the identity a Bridge presents, **before** trust exists.
 *
 * This is the one place the app completes a TLS handshake without already knowing which identity to
 * expect, and it exists because automatic discovery has to show the user a verification code derived
 * from the certificate the Bridge actually presents. Discovery alone must never establish trust, so
 * this verifier is deliberately incapable of granting any:
 *
 * - it records the chain and hands it back for a human to compare, and nothing else;
 * - it accepts only a well-formed chain (in date, correctly signed, CA identity), so a malformed
 *   chain is still refused rather than displayed;
 * - hostname verification is **not** disabled, so OkHttp still checks the leaf's SAN against the
 *   address that was dialled;
 * - the only request it will ever make is the anonymous `GET /api/v1/health`, which carries no
 *   credential and reveals nothing about the Codex account.
 *
 * Nothing downstream may use this verifier for a request that carries a credential. The pairing
 * client is built separately, pinned to the fingerprint a person confirmed.
 */
class DiscoveryIdentityProbeTrustManager : X509TrustManager {

    @Volatile
    var lastObservedChain: Array<X509Certificate> = emptyArray()
        private set

    override fun checkClientTrusted(chain: Array<out X509Certificate>?, authType: String?) {
        throw CertificateException("This trust manager only validates servers.")
    }

    override fun checkServerTrusted(chain: Array<out X509Certificate>?, authType: String?) {
        if (chain == null) {
            throw CertificateException("The server presented no certificate.")
        }

        @Suppress("UNCHECKED_CAST")
        val presented = chain as Array<X509Certificate>

        // Shape only. Whether this is the *right* Bridge is the question the user answers by
        // comparing the verification code.
        BridgeIdentityVerifier.verifyShape(presented)

        lastObservedChain = presented.copyOf()
    }

    override fun getAcceptedIssuers(): Array<X509Certificate> = emptyArray()
}

/** Reads the identity a discovered Bridge presents. */
class BridgeIdentityProbe(
    private val base: OkHttpClient = OkHttpClient(),
) {
    /**
     * Connects to [endpoint], reads the presented identity, and returns it unconfirmed.
     *
     * @throws CertificateException when the Bridge does not present a usable certificate chain.
     * @throws IOException when the Bridge cannot be reached at all.
     */
    suspend fun probe(endpoint: BridgeEndpoint): DiscoveredBridgeIdentity = withContext(Dispatchers.IO) {
        val trust = DiscoveryIdentityProbeTrustManager()
        val context = SSLContext.getInstance("TLS").apply {
            init(null, arrayOf<X509TrustManager>(trust), null)
        }

        val client = base.newBuilder()
            .sslSocketFactory(context.socketFactory, trust)
            .build()

        // The anonymous liveness route, and nothing else. It returns a constant, so observing it
        // tells the app nothing about the account behind the Bridge.
        client.newCall(
            Request.Builder().url("${endpoint.httpsBaseUrl}/api/v1/health").get().build(),
        ).execute().use { response ->
            if (!response.isSuccessful) {
                throw IOException("The Bridge answered the liveness probe with ${response.code}.")
            }
        }

        val chain = trust.lastObservedChain

        if (chain.isEmpty()) {
            throw CertificateException("The Bridge presented no certificate.")
        }

        val identity = BridgeIdentityVerifier.identityOf(chain)

        DiscoveredBridgeIdentity(
            endpoint = endpoint,
            identityCertificateDer = identity.encoded,
            identityFingerprint = BridgeIdentity.spkiSha256(identity),
        )
    }
}
