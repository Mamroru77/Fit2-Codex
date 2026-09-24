package com.codexquota.app.testing

import com.codexquota.app.data.CachedQuotaState
import com.codexquota.app.data.QuotaCache
import com.codexquota.app.data.api.BridgeApi
import com.codexquota.app.data.security.BridgeEndpoint
import com.codexquota.app.data.ws.BridgeWebSocket
import com.codexquota.app.data.ws.BridgeWebSocketEvent
import com.codexquota.app.data.ws.BridgeWebSocketFactory
import com.codexquota.app.domain.HistoryPoint
import com.codexquota.app.domain.QuotaEvent
import com.codexquota.app.domain.QuotaSnapshot
import com.codexquota.app.domain.QuotaSourceStatus
import com.codexquota.app.domain.QuotaWindow
import com.codexquota.app.sync.NetworkKind
import com.codexquota.app.sync.NetworkObserver
import java.io.IOException
import java.time.Instant
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.flow
import kotlinx.coroutines.flow.map

/** A quota snapshot with sensible defaults, so a test only states what it is about. */
fun snapshot(
    shortRemaining: Double = 72.0,
    weeklyRemaining: Double = 54.0,
    status: QuotaSourceStatus = QuotaSourceStatus.Online,
    generatedAt: Instant = Instant.parse("2026-09-22T13:30:00Z"),
    resetsAt: Instant = Instant.parse("2026-09-22T15:42:00Z"),
) = QuotaSnapshot(
    schemaVersion = 1,
    generatedAt = generatedAt,
    source = "codex_app_server",
    status = status,
    lastSuccessfulSyncAt = generatedAt,
    shortWindow = QuotaWindow(
        usedPercent = 100.0 - shortRemaining,
        remainingPercent = shortRemaining,
        windowMinutes = 300,
        resetsAt = resetsAt,
    ),
    weekly = QuotaWindow(
        usedPercent = 100.0 - weeklyRemaining,
        remainingPercent = weeklyRemaining,
        windowMinutes = 10080,
        resetsAt = Instant.parse("2026-09-28T00:00:00Z"),
    ),
)

/** A [BridgeApi] whose every answer is chosen by the test. */
class FakeBridgeApi : BridgeApi {
    var quota: () -> QuotaSnapshot = { snapshot() }
    var history: () -> List<HistoryPoint> = { emptyList() }
    var events: () -> List<QuotaEvent> = { emptyList() }

    var quotaCalls: Int = 0
        private set

    override suspend fun fetchQuota(): QuotaSnapshot {
        quotaCalls++
        return quota()
    }

    override suspend fun fetchHistory(hours: Int): List<HistoryPoint> = history()

    override suspend fun fetchEvents(hours: Int): List<QuotaEvent> = events()
}

/** A [QuotaCache] that keeps everything in memory and can be handed to a second repository. */
class InMemoryQuotaCache : QuotaCache {
    private val state = MutableStateFlow(CachedQuotaState.Empty)
    private var history: List<HistoryPoint> = emptyList()
    private var events: List<QuotaEvent> = emptyList()

    override fun observe(): Flow<CachedQuotaState> = state.asStateFlow()

    override suspend fun read(): CachedQuotaState = state.value

    override suspend fun writeCurrent(snapshot: QuotaSnapshot, receivedAt: Instant) {
        state.value = CachedQuotaState(snapshot, receivedAt, stale = false)
    }

    override suspend fun markStale() {
        state.value = state.value.copy(stale = true)
    }

    override suspend fun replaceHistory(points: List<HistoryPoint>) {
        history = points
    }

    override suspend fun readHistory(): List<HistoryPoint> = history

    override suspend fun replaceEvents(events: List<QuotaEvent>) {
        this.events = events
    }

    override suspend fun readEvents(): List<QuotaEvent> = events
}

/**
 * A [BridgeWebSocket] the test pushes frames into.
 *
 * It is backed by a channel rather than a shared flow because a socket *ends*: a shared flow never
 * completes, so a consumer collecting one would wait forever and the state machine would never learn
 * that the connection had gone.
 */
class FakeBridgeWebSocket : BridgeWebSocket {
    // Unbounded and non-suspending: a frame pushed before the consumer has subscribed must still
    // arrive, exactly as a real socket buffers what it has already received.
    private val channel = Channel<BridgeWebSocketEvent>(Channel.UNLIMITED)

    var closed: Boolean = false
        private set

    /**
     * The events this socket emits, completing after a terminal one.
     *
     * The flow ends by *observing* a terminal event rather than by the channel being closed, because
     * closing a channel right after pushing to it races with the consumer: the element can be lost,
     * and a socket that silently swallows its own close is not a socket any test should trust.
     */
    override fun events(): Flow<BridgeWebSocketEvent> = flow {
        while (true) {
            val event = channel.receive()

            emit(event)

            if (event is BridgeWebSocketEvent.Closed || event is BridgeWebSocketEvent.Failed) {
                return@flow
            }
        }
    }

    override fun close() {
        closed = true
        channel.close()
    }

    /** Pushes one frame. */
    fun emit(event: BridgeWebSocketEvent) = deliver(event)

    /** Ends the stream, as a server-side close would. */
    fun end(reason: String? = null) = deliver(BridgeWebSocketEvent.Closed(reason))

    /** Fails the stream. */
    fun fail(error: Throwable) = deliver(BridgeWebSocketEvent.Failed(error))

    /**
     * Delivers a frame, or fails the test.
     *
     * A frame the fake could not hand over would make the test assert the wrong thing for a reason
     * nobody can see, so it is reported here instead of being swallowed.
     */
    private fun deliver(event: BridgeWebSocketEvent) {
        check(channel.trySend(event).isSuccess) {
            "The fake socket could not deliver $event; its channel is already closed."
        }
    }
}

/** A [BridgeWebSocketFactory] that records every connection attempt. */
class FakeBridgeWebSocketFactory(
    private val socket: () -> BridgeWebSocket = { FakeBridgeWebSocket() },
) : BridgeWebSocketFactory {
    val connections = mutableListOf<Pair<BridgeEndpoint, BridgeWebSocket>>()

    /** Thrown instead of connecting, when a test wants the dial itself to fail. */
    var failWith: Throwable? = null

    override fun connect(endpoint: BridgeEndpoint): BridgeWebSocket {
        failWith?.let { throw it }

        val created = socket()
        connections += endpoint to created

        return created
    }

    /** The most recent socket, for a test that wants to push frames. */
    val last: FakeBridgeWebSocket get() = connections.last().second as FakeBridgeWebSocket
}

/** A [NetworkObserver] the test drives. */
class FakeNetworkObserver(
    initial: NetworkKind = NetworkKind.Lan,
    private val permission: Boolean = true,
) : NetworkObserver {
    private val state = MutableStateFlow(initial)

    override val kind: StateFlow<NetworkKind> = state.asStateFlow()

    override fun hasLocalNetworkPermission(): Boolean = permission

    /** Moves the phone onto another network. */
    fun moveTo(kind: NetworkKind) {
        state.value = kind
    }
}

/** Turns a flow of one cache's state into the value a test asserts on. */
internal fun Flow<CachedQuotaState>.latest(): Flow<QuotaSnapshot?> = map { it.snapshot }

/** An [IOException] that names what went wrong. */
fun offline(message: String = "The Bridge is unreachable."): IOException = IOException(message)
