package com.codexquota.app.data.ws

import com.codexquota.app.data.security.BridgeEndpoint
import com.codexquota.app.domain.QuotaSnapshot
import kotlinx.coroutines.flow.Flow

/**
 * What a Bridge WebSocket reports.
 *
 * Every quota update carries the **complete** snapshot, so there is nothing to reassemble and no
 * partial state to get wrong. The sequence number exists only to detect a gap, which is answered by
 * a REST reconciliation rather than by replaying frames.
 */
sealed interface BridgeWebSocketEvent {
    /** The server's first frame: version information only. */
    data class Hello(val apiVersion: String, val bridgeVersion: String) : BridgeWebSocketEvent

    /** A complete snapshot, with the sequence number the Bridge assigned it. */
    data class QuotaUpdated(val sequence: Long, val snapshot: QuotaSnapshot) : BridgeWebSocketEvent

    /** The Bridge is shutting down in an orderly way. */
    data object Shutdown : BridgeWebSocketEvent

    /** The connection ended. [reason] is for diagnosis, never for a decision. */
    data class Closed(val reason: String?) : BridgeWebSocketEvent

    /** The connection failed. A verification failure arrives here as a [SecurityFailure]. */
    data class Failed(val error: Throwable) : BridgeWebSocketEvent
}

/**
 * A live connection to the Bridge.
 *
 * It is an interface so the connection state machine can be driven deterministically in a test,
 * which is the only way to assert things like "a sequence gap triggers exactly one reconciliation".
 */
interface BridgeWebSocket {
    /** Frames from the server, until the connection ends. */
    fun events(): Flow<BridgeWebSocketEvent>

    /** Closes the connection. Safe to call more than once. */
    fun close()
}

/** Opens connections. A `fun interface`, so the composition root can supply one as a lambda. */
fun interface BridgeWebSocketFactory {
    /**
     * Connects to [endpoint].
     *
     * The returned socket is already authenticated: the credential travels in the upgrade request,
     * over a connection pinned to the paired Bridge identity.
     */
    fun connect(endpoint: BridgeEndpoint): BridgeWebSocket
}

/** The Bridge's identity is not the one that was pinned. No credential was sent. */
class SecurityFailure(message: String, cause: Throwable? = null) : Exception(message, cause)
