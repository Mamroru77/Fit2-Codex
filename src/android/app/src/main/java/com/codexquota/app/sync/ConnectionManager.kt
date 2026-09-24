package com.codexquota.app.sync

import com.codexquota.app.data.QuotaRepository
import com.codexquota.app.data.RefreshResult
import com.codexquota.app.data.api.DeviceUnauthorizedException
import com.codexquota.app.data.security.BridgeEndpoint
import com.codexquota.app.data.ws.BridgeWebSocket
import com.codexquota.app.data.ws.BridgeWebSocketEvent
import com.codexquota.app.data.ws.BridgeWebSocketFactory
import com.codexquota.app.data.ws.SecurityFailure
import com.codexquota.app.domain.ConnectionState
import java.time.Duration
import java.time.Instant
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.withTimeoutOrNull

/** How the phone is currently attached to the network. */
enum class NetworkKind {
    /** No usable network at all. */
    None,

    /** On the same local network as the Bridge is expected to be. */
    Lan,

    /** Cellular only. V1 is LAN-only, so this is not a connection the app can use. */
    CellularOnly,
}

/** Where the Bridge is. It changes with DHCP and with the interface, so it is asked for, not stored. */
fun interface EndpointProvider {
    /** The endpoint to dial, or `null` when there is no pairing or no known address. */
    suspend fun current(): BridgeEndpoint?
}

/** Reports network changes. An interface so a test can produce them deterministically. */
interface NetworkObserver {
    /** The current attachment, and every change to it. */
    val kind: StateFlow<NetworkKind>

    /** Whether the app currently has the platform's permission to use the local network. */
    fun hasLocalNetworkPermission(): Boolean
}

/**
 * The single connection truth of the app.
 *
 * Everything that wants to know whether the app is connected observes [state]; nothing else decides
 * it. The loop it runs is deliberately boring: ask where the Bridge is, check the network is one the
 * app can use, connect, consume frames, and when that ends decide whether to retry or to stop.
 *
 * The decisions worth naming:
 *
 * - a **verification failure is terminal**. Retrying would mean repeatedly offering a credential to
 *   something that is not the paired Bridge, so the loop stops and waits for the user.
 * - **cellular-only is not a retry**. V1 is LAN-only, so the app stops dialling a private address
 *   that cannot be reached and shows the cached values instead.
 * - a **sequence gap is answered by REST**, once, because replaying WebSocket frames is not
 *   something the protocol supports.
 * - a **network change cancels a pending backoff**, because the reason for waiting may no longer
 *   apply.
 */
class ConnectionManager(
    private val repository: QuotaRepository,
    private val webSockets: BridgeWebSocketFactory,
    private val endpoints: EndpointProvider,
    private val network: NetworkObserver,
    private val backoff: ReconnectBackoff = ReconnectBackoff(),
    private val clock: () -> Instant = Instant::now,
    private val onStateChanged: (ConnectionState) -> Unit = {},
) {
    private val _state = MutableStateFlow<ConnectionState>(ConnectionState.Unpaired)

    /** The canonical connection state. */
    val state: StateFlow<ConnectionState> = _state.asStateFlow()

    /** The highest sequence number seen on this connection. */
    private var lastSequence: Long? = null

    /**
     * Runs until cancelled.
     *
     * It never returns normally: the connection is either live, being retried, or stopped by the
     * caller cancelling this coroutine.
     */
    suspend fun run() {
        while (true) {
            val endpoint = endpoints.current()

            if (endpoint == null) {
                // Nothing to dial. Waiting for the pairing to appear is the caller's problem, not a
                // reason to spin.
                publish(ConnectionState.Unpaired)
                return
            }

            if (network.kind.value == NetworkKind.CellularOnly || !network.hasLocalNetworkPermission()) {
                publish(ConnectionState.OfflineCached(lastSuccessfulSyncAt()))
                awaitNetworkChange(from = network.kind.value)
                continue
            }

            publish(ConnectionState.Connecting)

            val outcome = connect(endpoint)

            if (outcome is Outcome.Stop) {
                return
            }
        }
    }

    /** One connection attempt and everything that follows from it. */
    private suspend fun connect(endpoint: BridgeEndpoint): Outcome {
        // The current snapshot is fetched before the socket is opened. It is what tells the app
        // whether the Bridge can serve data at all — a reachable Bridge that needs a Codex login is
        // a different state from an unreachable one — and the answer decides whether the socket is
        // worth opening.
        when (val initial = repository.refreshCurrent()) {
            is RefreshResult.SecurityError -> {
                publish(ConnectionState.SecurityError(initial.reason))
                return Outcome.Stop
            }

            RefreshResult.RepairRequired -> {
                publish(ConnectionState.RepairRequired)
                return Outcome.Stop
            }

            is RefreshResult.SourceAuthRequired -> {
                // Reachable but not usable until the user logs in. The socket is still opened,
                // because the Bridge may report the login completing on it.
                publish(ConnectionState.AuthRequired)
            }

            is RefreshResult.Offline -> {
                // The Bridge did not answer over REST. The socket is attempted anyway: a transient
                // REST failure is not proof that the Bridge is gone.
                publish(ConnectionState.OfflineCached(lastSuccessfulSyncAt()))
            }

            else -> Unit
        }

        val socket: BridgeWebSocket = try {
            webSockets.connect(endpoint)
        } catch (e: SecurityFailure) {
            // Terminal: the Bridge is not the paired one, and no credential was sent.
            publish(ConnectionState.SecurityError(e.message ?: "The Bridge identity does not match."))
            return Outcome.Stop
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            return backOffAfter("The Bridge could not be reached.")
        }

        var failed: Throwable? = null

        try {
            socket.events().collect { event -> handle(event) }
        } catch (e: CancellationException) {
            socket.close()
            throw e
        } catch (e: SecurityFailure) {
            publish(ConnectionState.SecurityError(e.message ?: "The Bridge identity does not match."))
            socket.close()
            return Outcome.Stop
        } catch (e: Exception) {
            failed = e
        } finally {
            socket.close()
        }

        // A drop is only a drop: the app keeps the last trusted values and tries again, unless the
        // state says the user has to do something first.
        if (currentStateIsTerminal()) {
            return Outcome.Stop
        }

        return backOffAfter(failed?.message ?: "The connection to the Bridge ended.")
    }

    /** Reacts to one frame from the Bridge. */
    private suspend fun handle(event: BridgeWebSocketEvent) {
        when (event) {
            is BridgeWebSocketEvent.Hello -> {
                // A successful handshake is what "connected" means, so the backoff restarts here.
                backoff.reset()
                publish(ConnectionState.Connected(clock()))
            }

            is BridgeWebSocketEvent.QuotaUpdated -> {
                reconcileSequence(event.sequence, event)

                backoff.reset()
                publish(ConnectionState.Connected(clock()))
            }

            BridgeWebSocketEvent.Shutdown -> {
                // The Bridge told us it is going away, which is not a failure and not something to
                // retry aggressively against. What it does mean is that the numbers are no longer
                // being refreshed.
                repository.markOffline()
                publish(ConnectionState.OfflineCached(lastSuccessfulSyncAt()))
            }

            is BridgeWebSocketEvent.Failed -> {
                when (event.error) {
                    // Terminal: the Bridge is not the paired one. Retrying would mean repeatedly
                    // offering a credential to something that is not the Bridge.
                    is SecurityFailure -> throw event.error

                    // Terminal: the credential is no longer accepted, which only re-pairing fixes.
                    is DeviceUnauthorizedException -> publish(ConnectionState.RepairRequired)

                    else -> {
                        repository.markOffline()
                        publish(ConnectionState.OfflineCached(lastSuccessfulSyncAt()))
                    }
                }
            }

            is BridgeWebSocketEvent.Closed -> Unit
        }
    }

    /**
     * Applies a snapshot, reconciling over REST when the sequence shows a gap.
     *
     * The gap is answered by fetching the current snapshot, exactly once, rather than by replaying
     * frames: the protocol has no replay, and the current snapshot is complete by definition.
     */
    private suspend fun reconcileSequence(sequence: Long, event: BridgeWebSocketEvent.QuotaUpdated) {
        val previous = lastSequence

        if (previous != null && sequence != previous + 1) {
            when (val reconciled = repository.refreshCurrent()) {
                is RefreshResult.Updated -> {
                    lastSequence = sequence
                    return
                }

                is RefreshResult.Unchanged -> {
                    lastSequence = sequence
                    return
                }

                is RefreshResult.SecurityError -> {
                    publish(ConnectionState.SecurityError(reconciled.reason))
                    throw SecurityFailure(reconciled.reason)
                }

                RefreshResult.RepairRequired -> {
                    publish(ConnectionState.RepairRequired)
                    return
                }

                else -> {
                    // The reconciliation itself failed. The stream is still ahead of what the app
                    // knows, so the sequence is not advanced and the next frame reconciles again.
                    return
                }
            }
        }

        lastSequence = sequence
        repository.applyRealtime(event.snapshot)
    }

    /** Waits out the backoff, but stops early when the network changes. */
    private suspend fun backOffAfter(reason: String): Outcome {
        // The values on screen are no longer being refreshed, so they are no longer current. Without
        // this the app would keep showing the last snapshot as real-time while it is disconnected.
        repository.markOffline()

        val delay = backoff.nextDelay()

        publish(ConnectionState.Reconnecting(backoff.attempt, delay))

        // The attachment is captured before waiting: comparing against a value read *after* the wait
        // would always be equal to itself and the early exit would never fire.
        val before = network.kind.value

        val changed = withTimeoutOrNull(delay.toMillis()) {
            network.kind.first { it != before }
        }

        // A network change is the reason the previous attempt failed, so it cancels the wait.
        if (changed != null) {
            backoff.reset()
        }

        return Outcome.Continue
    }

    /** Waits until the phone is on a network the app can use. */
    private suspend fun awaitNetworkChange(from: NetworkKind) {
        network.kind.first { it != from }
        backoff.reset()
    }

    /** Whether the state is one only the user can leave. */
    private fun currentStateIsTerminal(): Boolean = when (_state.value) {
        is ConnectionState.SecurityError,
        ConnectionState.RepairRequired,
        ConnectionState.Unpaired,
        -> true

        else -> false
    }

    /** The last successful sync time from the cache, for an honest "cached as of" label. */
    private suspend fun lastSuccessfulSyncAt(): Instant? = repository.current().receivedAt

    private fun publish(next: ConnectionState) {
        if (_state.value == next) {
            return
        }

        _state.value = next
        onStateChanged(next)
    }

    /** Whether the loop should keep going after one connection attempt. */
    private sealed interface Outcome {
        /** Try again. */
        data object Continue : Outcome

        /** Stop and wait for the user. */
        data object Stop : Outcome
    }
}

/** Convenience: the canonical state as a [Flow], for callers that only need to observe it. */
fun ConnectionManager.stateFlow(): Flow<ConnectionState> = state

/** The delay a reconnect is currently waiting out, for a UI that wants to show it. */
internal fun ConnectionState.retryDelay(): Duration? =
    (this as? ConnectionState.Reconnecting)?.nextRetryIn
