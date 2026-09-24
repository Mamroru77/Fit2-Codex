package com.codexquota.app.domain

import java.time.Duration
import java.time.Instant

/**
 * The single connection truth of the app.
 *
 * Every screen observes one `StateFlow<ConnectionState>`; nothing else decides whether the app is
 * connected. The states are distinct on purpose, because the user-visible difference matters:
 * "reconnecting" and "offline, showing cached data" must never be presented as the same thing, and
 * a security error must never be shown as a generic offline state.
 */
sealed interface ConnectionState {
    /** No Bridge has been paired yet. */
    data object Unpaired : ConnectionState

    /** Looking for the paired Bridge, or for one to pair with. */
    data object Discovering : ConnectionState

    /** A connection attempt is in flight. */
    data object Connecting : ConnectionState

    /** Live: a WebSocket is open and the data is current. */
    data class Connected(val since: Instant) : ConnectionState

    /** The connection dropped and a retry is scheduled. */
    data class Reconnecting(val attempt: Int, val nextRetryIn: Duration) : ConnectionState

    /**
     * Not reachable, and the last trusted data is being shown instead. This is the LAN-only V1
     * answer to "the phone left the network": it must not keep dialling a private address.
     */
    data class OfflineCached(val lastSuccessfulSyncAt: Instant?) : ConnectionState

    /** The Bridge is reachable but Codex needs a login. Cached quota is still worth showing. */
    data object AuthRequired : ConnectionState

    /** The device credential was rejected: the phone must pair again. */
    data object RepairRequired : ConnectionState

    /**
     * The Bridge's identity does not match the pinned one. Terminal until the user acts: the app must
     * not retry into a state where it would send the device credential to an impostor.
     */
    data class SecurityError(val reason: String) : ConnectionState
}

/**
 * The subset of [ConnectionState] that may be written to disk.
 *
 * Transient states are deliberately not persistable: restoring a process into `Connecting` would be
 * a claim the app cannot support. [toPersisted] maps each transient state to the honest thing the
 * app knows when it comes back.
 */
enum class PersistedConnectionState {
    Unpaired,
    OfflineCached,
    AuthRequired,
    RepairRequired,
    SecurityError,
}

/** The persistable form of this state. */
fun ConnectionState.toPersisted(): PersistedConnectionState = when (this) {
    ConnectionState.Unpaired -> PersistedConnectionState.Unpaired

    // Anything that is not live comes back as "cached until proven current".
    ConnectionState.Discovering,
    ConnectionState.Connecting,
    is ConnectionState.Reconnecting,
    is ConnectionState.OfflineCached,
    -> PersistedConnectionState.OfflineCached

    is ConnectionState.Connected -> PersistedConnectionState.OfflineCached

    ConnectionState.AuthRequired -> PersistedConnectionState.AuthRequired
    ConnectionState.RepairRequired -> PersistedConnectionState.RepairRequired
    is ConnectionState.SecurityError -> PersistedConnectionState.SecurityError
}
