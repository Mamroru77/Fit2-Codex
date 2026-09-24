package com.codexquota.app.sync

import com.codexquota.app.data.QuotaRepository
import com.codexquota.app.data.api.DeviceUnauthorizedException
import com.codexquota.app.data.security.BridgeEndpoint
import com.codexquota.app.data.ws.BridgeWebSocketEvent
import com.codexquota.app.data.ws.SecurityFailure
import com.codexquota.app.domain.ConnectionState
import com.codexquota.app.testing.FakeBridgeApi
import com.codexquota.app.testing.FakeBridgeWebSocketFactory
import com.codexquota.app.testing.FakeNetworkObserver
import com.codexquota.app.testing.InMemoryQuotaCache
import com.codexquota.app.testing.offline
import com.codexquota.app.testing.snapshot
import java.time.Duration
import java.time.Instant
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertIs
import kotlin.test.assertTrue
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.launch
import kotlinx.coroutines.test.UnconfinedTestDispatcher
import kotlinx.coroutines.test.advanceUntilIdle
import kotlinx.coroutines.test.runCurrent
import kotlinx.coroutines.test.runTest
import kotlinx.coroutines.withTimeout

/**
 * The connection state machine.
 *
 * Everything it depends on is injected, so these tests describe the app's behaviour rather than a
 * network's: a frame is pushed, a network moves, and the resulting state is asserted.
 */
class ConnectionManagerTest {

    /**
     * The tests run on an unconfined dispatcher.
     *
     * The state machine is driven by frames arriving on a socket, and a coroutine resumed by a
     * channel is not guaranteed to have run by the next line of a standard-dispatcher test. Running
     * everything eagerly makes the assertions about the state machine rather than about scheduling.
     */
    private fun test(body: suspend kotlinx.coroutines.test.TestScope.() -> Unit) =
        runTest(UnconfinedTestDispatcher()) { body() }

    private val bridgeAddress = BridgeEndpoint("192.168.1.20", 47821)
    private val now = Instant.parse("2026-09-22T13:31:00Z")

    private class Harness(
        val sockets: FakeBridgeWebSocketFactory = FakeBridgeWebSocketFactory(),
        val network: FakeNetworkObserver = FakeNetworkObserver(),
    ) {
        val api = FakeBridgeApi()
        val cache = InMemoryQuotaCache()
        val repository = QuotaRepository(api, cache) { Instant.parse("2026-09-22T13:31:00Z") }
    }

    private fun manager(
        h: Harness,
        backoff: ReconnectBackoff = ReconnectBackoff(jitterFraction = 0.0),
    ) = ConnectionManager(
        repository = h.repository,
        webSockets = h.sockets,
        endpoints = EndpointProvider { bridgeAddress },
        network = h.network,
        backoff = backoff,
        clock = { now },
    )

    @Test
    fun anOpenSocketBecomesConnected() = test {
        val h = Harness()
        val manager = manager(h)

        backgroundScope.launch { manager.run() }
        runCurrent()

        h.sockets.last.emit(BridgeWebSocketEvent.Hello("v1", "1.0.0"))
        runCurrent()

        val state = assertIs<ConnectionState.Connected>(manager.state.value)
        assertEquals(now, state.since)
    }

    /**
     * Waits for a state rather than assuming the scheduler has drained.
     *
     * A coroutine resumed by a channel push is not guaranteed to have run by the time the next line
     * of the test executes, so the tests wait for the state they are about.
     */
    private suspend fun ConnectionManager.await(
        description: String,
        predicate: (ConnectionState) -> Boolean,
    ): ConnectionState = withTimeout(5_000) { state.first(predicate) }

    /**
     * Records every state the manager passes through.
     *
     * A `StateFlow` conflates, so a state that is replaced quickly — a reconnect announcement, an
     * offline label — can be missed by reading the current value. Collecting them is the only way to
     * assert that they happened.
     */
    private fun kotlinx.coroutines.test.TestScope.recordStates(
        manager: ConnectionManager,
        into: MutableList<ConnectionState>,
    ) {
        backgroundScope.launch { manager.state.collect { into += it } }
    }

    @Test
    fun aDropReconnectsAndTriesAgain() = test {
        val h = Harness()
        val manager = manager(h)
        val seen = mutableListOf<ConnectionState>()
        recordStates(manager, seen)

        backgroundScope.launch { manager.run() }
        runCurrent()

        h.sockets.last.emit(BridgeWebSocketEvent.Hello("v1", "1.0.0"))
        runCurrent()

        h.sockets.last.end("server closed")
        advanceUntilIdle()

        // The drop is announced as a retry rather than as a failure, and the manager is not in a
        // terminal state: it is waiting to try again. The dial itself is covered by
        // `returningToTheLanReconnectsWithoutWaitingOutTheBackoff`, which does not have to advance a
        // virtual-time backoff to observe it.
        assertTrue(seen.any { it is ConnectionState.Reconnecting }, "the retry was never announced; states=$seen")

        val state = manager.state.value
        assertTrue(
            state is ConnectionState.Reconnecting,
            "a drop must leave the manager waiting to retry, but it was $state",
        )
    }

    @Test
    fun aReconnectIsAnnouncedWithItsAttemptAndDelay() = test {
        val h = Harness()
        val manager = manager(h)

        backgroundScope.launch { manager.run() }
        runCurrent()

        h.sockets.last.end()

        val state = assertIs<ConnectionState.Reconnecting>(manager.await("a retry") { it is ConnectionState.Reconnecting })
        assertEquals(1, state.attempt)
        assertEquals(Duration.ofSeconds(1), state.nextRetryIn)
    }

    @Test
    fun anIdentityMismatchIsTerminalAndIsNeverRetried() = test {
        val h = Harness()
        h.sockets.failWith = SecurityFailure("The Bridge identity does not match.")

        val manager = manager(h)

        backgroundScope.launch { manager.run() }

        assertIs<ConnectionState.SecurityError>(manager.await("a security error") { it is ConnectionState.SecurityError })

        // Terminal means terminal: a second dial would mean offering the credential to something that
        // is not the paired Bridge. Not a single socket was opened, so no credential was sent.
        assertEquals(0, h.sockets.connections.size)
        assertEquals(1, h.api.quotaCalls, "the REST probe ran once and then stopped")
    }

    @Test
    fun aRejectedCredentialAsksForRepairRatherThanRetrying() = test {
        val h = Harness()
        val manager = manager(h)

        backgroundScope.launch { manager.run() }
        runCurrent()

        h.sockets.last.fail(DeviceUnauthorizedException("The credential was rejected."))

        assertEquals(ConnectionState.RepairRequired, manager.await("repair required") { it == ConnectionState.RepairRequired })
        assertEquals(1, h.sockets.connections.size)
    }

    @Test
    fun cellularOnlyShowsCachedDataAndDoesNotDialAPrivateAddress() = test {
        val h = Harness(network = FakeNetworkObserver(initial = NetworkKind.CellularOnly))
        val manager = manager(h)

        backgroundScope.launch { manager.run() }
        runCurrent()

        assertIs<ConnectionState.OfflineCached>(manager.state.value)
        // V1 is LAN-only, so dialling a 192.168 address from cellular can only fail slowly.
        assertEquals(0, h.sockets.connections.size)
    }

    @Test
    fun withoutTheLanPermissionTheAppDoesNotDial() = test {
        val h = Harness(network = FakeNetworkObserver(permission = false))
        val manager = manager(h)

        backgroundScope.launch { manager.run() }
        runCurrent()

        assertIs<ConnectionState.OfflineCached>(manager.state.value)
        assertEquals(0, h.sockets.connections.size)
    }

    @Test
    fun returningToTheLanReconnectsWithoutWaitingOutTheBackoff() = test {
        val network = FakeNetworkObserver(initial = NetworkKind.CellularOnly)
        val h = Harness(network = network)
        val manager = manager(h)

        backgroundScope.launch { manager.run() }
        runCurrent()

        assertIs<ConnectionState.OfflineCached>(manager.state.value)

        network.moveTo(NetworkKind.Lan)

        manager.await("a dial") { it is ConnectionState.Connecting || it is ConnectionState.Connected }

        assertEquals(1, h.sockets.connections.size)
    }

    @Test
    fun aSequenceGapTriggersExactlyOneRestReconciliation() = test {
        val h = Harness()
        val manager = manager(h)

        backgroundScope.launch { manager.run() }
        runCurrent()

        // The connection probe already made one REST call; what matters is how many the frames add.
        val afterConnecting = h.api.quotaCalls

        h.sockets.last.emit(BridgeWebSocketEvent.QuotaUpdated(100, snapshot(shortRemaining = 30.0)))
        runCurrent()
        assertEquals(afterConnecting, h.api.quotaCalls, "the first frame is not a gap")

        // 101 never arrives, so the app has missed something and must not guess what.
        h.sockets.last.emit(BridgeWebSocketEvent.QuotaUpdated(102, snapshot(shortRemaining = 8.0)))
        runCurrent()

        assertEquals(afterConnecting + 1, h.api.quotaCalls, "a gap must be reconciled exactly once")
    }

    @Test
    fun consecutiveSequencesAreAppliedWithoutAnyRestCall() = test {
        val h = Harness()
        val manager = manager(h)

        backgroundScope.launch { manager.run() }
        runCurrent()

        val afterConnecting = h.api.quotaCalls

        h.sockets.last.emit(BridgeWebSocketEvent.QuotaUpdated(1, snapshot(shortRemaining = 30.0)))
        h.sockets.last.emit(BridgeWebSocketEvent.QuotaUpdated(2, snapshot(shortRemaining = 20.0)))
        h.sockets.last.emit(BridgeWebSocketEvent.QuotaUpdated(3, snapshot(shortRemaining = 10.0)))
        runCurrent()

        assertEquals(afterConnecting, h.api.quotaCalls, "consecutive frames need no reconciliation")
        assertEquals(10.0, h.repository.current().snapshot?.shortWindow?.remainingPercent)
    }

    @Test
    fun aFrameIsWrittenToTheCacheAsFresh() = test {
        val h = Harness()
        val manager = manager(h)

        backgroundScope.launch { manager.run() }
        runCurrent()

        h.sockets.last.emit(BridgeWebSocketEvent.QuotaUpdated(1, snapshot(shortRemaining = 8.0)))
        runCurrent()

        assertEquals(8.0, h.repository.current().snapshot?.shortWindow?.remainingPercent)
        assertTrue(!h.repository.current().stale)
    }

    @Test
    fun anUnpairedPhoneReportsUnpairedAndDoesNotDial() = test {
        val h = Harness()
        val manager = ConnectionManager(
            repository = h.repository,
            webSockets = h.sockets,
            endpoints = EndpointProvider { null },
            network = h.network,
        )

        backgroundScope.launch { manager.run() }
        advanceUntilIdle()

        assertEquals(ConnectionState.Unpaired, manager.state.value)
        assertEquals(0, h.sockets.connections.size)
    }

    @Test
    fun anOrdinaryTransportFailureKeepsTheCachedValuesAndRetries() = test {
        val h = Harness()
        h.repository.refreshCurrent()
        val manager = manager(h)
        val seen = mutableListOf<ConnectionState>()
        recordStates(manager, seen)

        backgroundScope.launch { manager.run() }
        runCurrent()

        h.sockets.last.fail(offline())
        advanceUntilIdle()

        assertTrue(
            seen.any { it is ConnectionState.Reconnecting },
            "an ordinary drop must be retried; states=$seen",
        )

        // The numbers themselves survive and are labelled stale: they are still the last thing the
        // Bridge said, and they are no longer being refreshed.
        val cached = h.repository.current()
        assertEquals(72.0, cached.snapshot?.shortWindow?.remainingPercent)
        assertTrue(cached.stale, "the cached values must be marked stale while offline")
    }

    @Test
    fun aBridgeShutdownIsNotTreatedAsAFailure() = test {
        val h = Harness()
        val manager = manager(h)

        backgroundScope.launch { manager.run() }
        runCurrent()

        h.sockets.last.emit(BridgeWebSocketEvent.Shutdown)

        assertIs<ConnectionState.OfflineCached>(
            manager.await("offline") { it is ConnectionState.OfflineCached },
        )
    }
}
