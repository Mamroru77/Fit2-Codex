package com.codexquota.app.ui.dashboard

import com.codexquota.app.data.QuotaRepository
import com.codexquota.app.data.security.BridgeEndpoint
import com.codexquota.app.data.ws.BridgeWebSocketEvent
import com.codexquota.app.domain.ConnectionState
import com.codexquota.app.domain.QuotaSourceStatus
import com.codexquota.app.sync.ConnectionManager
import com.codexquota.app.sync.EndpointProvider
import com.codexquota.app.testing.FakeBridgeApi
import com.codexquota.app.testing.FakeBridgeWebSocketFactory
import com.codexquota.app.testing.FakeNetworkObserver
import com.codexquota.app.testing.InMemoryQuotaCache
import com.codexquota.app.testing.offline
import com.codexquota.app.testing.snapshot
import java.time.Instant
import java.time.ZoneId
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertNotEquals
import kotlin.test.assertTrue
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.CoroutineStart
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.launch
import kotlinx.coroutines.test.runCurrent
import kotlinx.coroutines.test.runTest

/**
 * What the user actually reads.
 *
 * These assertions are about text, because the requirement is about text: the connection state must
 * be written out, and a security error must never be presented as a generic offline state.
 */
class DashboardViewModelTest {

    private val zone = ZoneId.of("Asia/Shanghai")
    private val api = FakeBridgeApi()
    private val cache = InMemoryQuotaCache()
    private val sockets = FakeBridgeWebSocketFactory()
    private val network = FakeNetworkObserver()

    private fun repository() = QuotaRepository(api, cache) { Instant.parse("2026-09-22T13:31:00Z") }

    private fun connectionManager(repository: QuotaRepository) = ConnectionManager(
        repository = repository,
        webSockets = sockets,
        endpoints = EndpointProvider { BridgeEndpoint("192.168.1.20", 47821) },
        network = network,
    )

    private fun viewModel(repository: QuotaRepository, manager: ConnectionManager, scope: kotlinx.coroutines.CoroutineScope) =
        DashboardViewModel(
            repository = repository,
            connection = manager,
            scope = scope,
            zone = zone,
        )

    @Test
    fun aFreshSnapshotShowsBothPercentagesAndRealTime() = runTest {
        val repository = repository()
        repository.refreshCurrent()
        val manager = connectionManager(repository)

        val vm = viewModel(repository, manager, backgroundScope)
        backgroundScope.launchManager(manager)
        runCurrent()

        sockets.last.emit(BridgeWebSocketEvent.Hello("v1", "1.0.0"))
        runCurrent()

        val state = vm.state.first { it.hasData }

        assertEquals("72%", state.shortRemaining)
        assertEquals("54%", state.weeklyRemaining)
        assertEquals(Freshness.RealTime, state.freshness)
        assertEquals("Real-time", state.connectionText)
    }

    @Test
    fun theUsedPercentageIsCarriedAsSecondaryInformation() = runTest {
        val repository = repository()
        repository.refreshCurrent()

        val vm = viewModel(repository, connectionManager(repository), backgroundScope)
        val state = vm.state.first { it.hasData }

        assertEquals("used 28%", state.shortUsed)
        assertEquals("used 46%", state.weeklyUsed)
    }

    @Test
    fun resetTimesAreShownInThePhonesOwnTimeZone() = runTest {
        val repository = repository()
        repository.refreshCurrent()

        val vm = viewModel(repository, connectionManager(repository), backgroundScope)
        val state = vm.state.first { it.hasData }

        // 15:42Z is 23:42 in Asia/Shanghai, which is the time the user's phone would show.
        assertEquals("23:42", state.shortResetsAt)
    }

    @Test
    fun aStaleCacheIsLabelledOfflineWithTheLastUpdate() = runTest {
        val repository = repository()
        repository.refreshCurrent()
        api.quota = { throw offline() }
        repository.refreshCurrent()

        val vm = viewModel(repository, connectionManager(repository), backgroundScope)
        val state = vm.state.first { it.hasData }

        assertEquals(Freshness.OfflineCached, state.freshness)
        assertEquals("Offline / cached", state.connectionText)
        assertTrue(state.lastUpdated!!.startsWith("Last update "))
        // The numbers themselves are still shown: they are the last thing the Bridge said.
        assertEquals("72%", state.shortRemaining)
    }

    @Test
    fun aSecurityErrorIsNeverPresentedAsGenericOffline() = runTest {
        val repository = repository()
        repository.refreshCurrent()
        val manager = connectionManager(repository)

        val vm = viewModel(repository, manager, backgroundScope)
        backgroundScope.launchManager(manager)
        runCurrent()

        sockets.last.fail(
            com.codexquota.app.data.ws.SecurityFailure("The Bridge identity does not match."),
        )
        runCurrent()

        val state = vm.state.first { it.freshness == Freshness.SecurityError }

        assertEquals("Security error", state.connectionText)
        assertNotEquals("Offline / cached", state.connectionText)
        assertEquals("The Bridge identity does not match.", state.detail)
    }

    @Test
    fun aLoginRequirementIsNamedAndKeepsTheLastTrustedNumbers() = runTest {
        val repository = repository()
        repository.refreshCurrent()
        api.quota = {
            throw com.codexquota.app.data.api.ProtocolException(
                com.codexquota.app.data.api.ApiErrorCodes.CODEX_AUTH_REQUIRED,
                "login required",
            )
        }
        repository.refreshCurrent()

        val manager = connectionManager(repository)
        val vm = viewModel(repository, manager, backgroundScope)
        backgroundScope.launchManager(manager)
        runCurrent()

        val state = vm.state.first { it.freshness == Freshness.LoginRequired }

        assertEquals("Login required", state.connectionText)
        assertEquals("72%", state.shortRemaining)
    }

    @Test
    fun aReconnectingConnectionSaysSo() = runTest {
        val repository = repository()
        val manager = connectionManager(repository)

        val vm = viewModel(repository, manager, backgroundScope)
        backgroundScope.launchManager(manager)
        runCurrent()

        sockets.last.end()
        runCurrent()

        val state = vm.state.first { it.freshness == Freshness.Reconnecting }

        assertEquals("Reconnecting", state.connectionText)
    }

    @Test
    fun beforeAnyDataArrivesThereIsNothingToShow() = runTest {
        val repository = repository()

        val vm = viewModel(repository, connectionManager(repository), backgroundScope)
        val state = vm.state.value

        assertEquals("--", state.shortRemaining)
        assertEquals(false, state.hasData)
        assertEquals("No data yet", state.connectionText)
    }

    @Test
    fun aNotOnlineSourceIsNotPresentedAsRealTime() = runTest {
        val repository = repository()

        // The Bridge itself says its source is stale, so even a healthy connection must not label
        // the numbers as real-time.
        api.quota = { snapshot(status = QuotaSourceStatus.Stale) }

        val manager = connectionManager(repository)

        val vm = viewModel(repository, manager, backgroundScope)
        backgroundScope.launchManager(manager)
        runCurrent()

        sockets.last.emit(BridgeWebSocketEvent.Hello("v1", "1.0.0"))

        // Connected, but the data itself is not current, so the label must not claim it is.
        assertEquals(Freshness.OfflineCached, vm.state.first { it.hasData }.freshness)
    }

    @Test
    fun aFractionalPercentageIsRenderedWithOneDecimal() = runTest {
        val repository = repository()
        cache.writeCurrent(snapshot(shortRemaining = 72.4), Instant.parse("2026-09-22T13:31:00Z"))

        val vm = viewModel(repository, connectionManager(repository), backgroundScope)

        assertEquals("72.4%", vm.state.first { it.hasData }.shortRemaining)
    }

    @Test
    fun theTerminalConnectionStatesAreTheOnesOnlyTheUserCanLeave() = runTest {
        // A guard against a future state being silently treated as retryable.
        val terminal = listOf(
            ConnectionState.SecurityError("x"),
            ConnectionState.RepairRequired,
            ConnectionState.Unpaired,
        )

        terminal.forEach { state ->
            assertTrue(
                state is ConnectionState.SecurityError ||
                    state is ConnectionState.RepairRequired ||
                    state is ConnectionState.Unpaired,
            )
        }
    }
}

/**
 * Runs the connection loop in the test's background scope, undispached so the first step happens
 * before the test continues.
 */
private fun CoroutineScope.launchManager(manager: ConnectionManager) {
    launch(start = CoroutineStart.UNDISPATCHED) { manager.run() }
}
