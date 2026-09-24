package com.codexquota.app.data.pairing

import com.codexquota.app.data.security.BridgeEndpoint
import okhttp3.OkHttpClient

/**
 * Runs a pairing handshake.
 *
 * It is an interface so the connection UI can be tested without a Bridge, and so the one place that
 * decides which identity to pin is the implementation rather than the caller.
 */
interface PairingCoordinator {
    /** Claims a QR session and carries it through to a credential. */
    suspend fun pairByQr(payload: PairingQrPayload): PairingOutcome

    /** Requests a discovery session, from an identity a person has confirmed. */
    suspend fun pairByDiscovery(confirmed: ConfirmedBridgeIdentity, bridgeId: String): PairingOutcome
}

/**
 * The real coordinator.
 *
 * For every attempt it builds a fresh HTTP client pinned to the identity that attempt is for. That
 * is the property worth naming: the pin is derived from the QR payload or from the confirmed
 * discovery identity, and a client built for one Bridge can never be reused for another.
 */
class OkHttpPairingCoordinator(
    private val displayName: String,
    private val base: OkHttpClient = OkHttpClient(),
    private val clock: () -> java.time.Instant = java.time.Instant::now,
) : PairingCoordinator {

    override suspend fun pairByQr(payload: PairingQrPayload): PairingOutcome =
        clientFor(payload.endpoint, payload.identityFingerprint).pairByQr(payload)

    override suspend fun pairByDiscovery(
        confirmed: ConfirmedBridgeIdentity,
        bridgeId: String,
    ): PairingOutcome =
        clientFor(confirmed.endpoint, confirmed.identityFingerprint)
            .pairByDiscovery(confirmed, bridgeId)

    private fun clientFor(endpoint: BridgeEndpoint, fingerprint: String): PairingClient =
        PairingClient(
            http = PairingClient.pinnedTo(fingerprint, base),
            baseUrl = endpoint.httpsBaseUrl,
            displayName = displayName,
            clock = clock,
        )
}
