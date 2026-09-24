package com.codexquota.app.ui.settings

import com.codexquota.app.data.settings.AlertSettings
import com.codexquota.app.data.settings.InMemoryAlertSettings
import com.codexquota.app.data.settings.NotificationPreferences
import com.codexquota.app.data.settings.WatchFormat
import com.codexquota.app.domain.alerts.WindowRules
import com.codexquota.app.notifications.NotificationChannels
import com.codexquota.app.notifications.NotificationHealth
import com.codexquota.app.notifications.NotificationHealthChecker
import com.codexquota.app.sync.LocalNetworkPermissionState
import com.codexquota.app.sync.SyncMode
import com.codexquota.app.sync.SyncModeController
import com.codexquota.app.sync.SyncScheduler
import java.time.Duration
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.test.UnconfinedTestDispatcher
import kotlinx.coroutines.test.advanceUntilIdle
import kotlinx.coroutines.test.runCurrent
import kotlinx.coroutines.test.runTest

/**
 * The settings screen's honesty.
 *
 * The distinction these tests defend is between "the user asked for notifications" and "the system
 * will deliver them". Reporting the first as the second is the failure mode the spec names, so it
 * has a test of its own.
 */
class SettingsViewModelTest {

    /** A scheduler that records what it was asked to do. */
    private class RecordingScheduler : SyncScheduler {
        val calls = mutableListOf<String>()

        var liveRunning = false
        var periodicEnqueued = false

        override fun startLive() {
            calls += "startLive"
            liveRunning = true
        }

        override fun stopLive() {
            calls += "stopLive"
            liveRunning = false
        }

        override fun enqueuePeriodic() {
            calls += "enqueuePeriodic"
            periodicEnqueued = true
        }

        override fun cancelPeriodic() {
            calls += "cancelPeriodic"
            periodicEnqueued = false
        }

        override fun isLiveRunning(): Boolean = liveRunning

        override fun isPeriodicEnqueued(): Boolean = periodicEnqueued
    }

    private class FixedHealth(private val health: NotificationHealth) : NotificationHealthChecker {
        override fun read(): NotificationHealth = health

        override fun isChannelEnabled(channelId: String): Boolean = health.canDeliver(channelId)
    }

    /**
     * Builds the screen's ViewModel together with the controller that applies its mode.
     *
     * They are returned together because they are two halves of one behaviour: the ViewModel exposes
     * the mode, and the controller — started by the application, exactly as here — is what starts the
     * service or schedules the worker. A test that asserts something was actually scheduled has to
     * have both.
     */
    private class Harness(
        val viewModel: SettingsViewModel,
        val controller: SyncModeController,
        val scheduler: RecordingScheduler,
    )

    private fun harness(
        preferences: NotificationPreferences,
        health: NotificationHealth,
        scope: kotlinx.coroutines.CoroutineScope,
        settings: AlertSettings = InMemoryAlertSettings(preferences),
        lanPermission: StateFlow<LocalNetworkPermissionState> =
            MutableStateFlow(LocalNetworkPermissionState.Granted),
        sendTest: suspend (WatchFormat) -> Unit = { },
    ): Harness {
        val scheduler = RecordingScheduler()
        val controller = SyncModeController(settings, scheduler, scope)

        return Harness(
            viewModel = SettingsViewModel(
                settings = settings,
                health = FixedHealth(health),
                scheduler = scheduler,
                modeController = controller,
                lanPermission = lanPermission,
                bridgeReachable = { false },
                sendTestNotification = sendTest,
                scope = scope,
            ),
            controller = controller,
            scheduler = scheduler,
        )
    }

    @Test
    fun aSwitchThatIsOnWhileTheSystemDeniesDeliveryIsNotReportedAsHealthy() =
        runTest(UnconfinedTestDispatcher()) {
            val harness = harness(
                preferences = NotificationPreferences(statusNotificationEnabled = true, alertsEnabled = true),
                health = NotificationHealth(
                    permissionGranted = false,
                    statusChannelEnabled = true,
                    alertsChannelEnabled = true,
                ),
                scope = backgroundScope,
            )

            advanceUntilIdle()
            harness.viewModel.refresh()

            val state = harness.viewModel.state.value

            assertTrue(state.preferences.statusNotificationEnabled, "the user's choice is still recorded")
            assertFalse(state.statusDeliveryHealthy, "but the system will not deliver it")
            assertFalse(state.alertsDeliveryHealthy)
            assertFalse(state.notificationHealth.canDeliverAnything)
        }

    @Test
    fun aBlockedChannelIsReportedPerChannelRatherThanAsAWhole() =
        runTest(UnconfinedTestDispatcher()) {
            val harness = harness(
                preferences = NotificationPreferences(statusNotificationEnabled = true, alertsEnabled = true),
                health = NotificationHealth(
                    permissionGranted = true,
                    statusChannelEnabled = true,
                    alertsChannelEnabled = false,
                ),
                scope = backgroundScope,
            )

            advanceUntilIdle()
            harness.viewModel.refresh()

            val state = harness.viewModel.state.value

            assertTrue(state.statusDeliveryHealthy, "the status channel is fine")
            assertFalse(state.alertsDeliveryHealthy, "the alerts channel is blocked")
            assertTrue(state.notificationHealth.canDeliver(NotificationChannels.STATUS))
            assertFalse(state.notificationHealth.canDeliver(NotificationChannels.ALERTS))
        }

    @Test
    fun everythingHealthyIsReportedAsHealthy() = runTest(UnconfinedTestDispatcher()) {
        val harness = harness(
            preferences = NotificationPreferences(statusNotificationEnabled = true, alertsEnabled = true),
            health = NotificationHealth.Unknown,
            scope = backgroundScope,
        )

        advanceUntilIdle()
        harness.viewModel.refresh()

        val state = harness.viewModel.state.value

        assertTrue(state.statusDeliveryHealthy)
        assertTrue(state.alertsDeliveryHealthy)
    }

    @Test
    fun aPersistedLivePreferenceWithNoRunningServiceAsksToResume() =
        runTest(UnconfinedTestDispatcher()) {
            // The state a reboot leaves behind: the setting survived, the service did not.
            val harness = harness(
                preferences = NotificationPreferences(statusNotificationEnabled = true),
                health = NotificationHealth.Unknown,
                scope = backgroundScope,
            )

            advanceUntilIdle()
            harness.viewModel.refresh()

            val state = harness.viewModel.state.value

            assertTrue(state.preferences.statusNotificationEnabled)
            assertFalse(state.liveRunning, "nothing is running yet")
            assertTrue(state.liveResumeRequired, "the screen must offer to resume")
            assertTrue(
                harness.scheduler.calls.none { it == "startLive" },
                "the service must not be started behind the user's back",
            )
        }

    @Test
    fun resumingStartsLiveAndClearsTheResumePrompt() = runTest(UnconfinedTestDispatcher()) {
        val harness = harness(
            preferences = NotificationPreferences(statusNotificationEnabled = true),
            health = NotificationHealth.Unknown,
            scope = backgroundScope,
        )

        advanceUntilIdle()
        harness.viewModel.refresh()

        harness.viewModel.resumeLive()
        advanceUntilIdle()

        assertTrue(harness.scheduler.liveRunning, "resuming must start the live service")
        assertFalse(
            harness.viewModel.state.value.liveResumeRequired,
            "once running, there is nothing left to resume",
        )
        assertEquals(SyncMode.Live, harness.viewModel.state.value.mode)
    }

    @Test
    fun resumingCancelsTheWorkerBeforeStartingTheService() = runTest(UnconfinedTestDispatcher()) {
        val harness = harness(
            preferences = NotificationPreferences(statusNotificationEnabled = true),
            health = NotificationHealth.Unknown,
            scope = backgroundScope,
        )

        advanceUntilIdle()
        harness.scheduler.calls.clear()

        harness.viewModel.resumeLive()
        advanceUntilIdle()

        // The ordering rule that keeps live and periodic from overlapping, applied on the resume path
        // as well as on the ordinary transition.
        assertEquals(listOf("cancelPeriodic", "startLive"), harness.scheduler.calls)
    }

    @Test
    fun aLivePreferenceWithTheServiceAlreadyRunningDoesNotAskToResume() =
        runTest(UnconfinedTestDispatcher()) {
            val harness = harness(
                preferences = NotificationPreferences(statusNotificationEnabled = true),
                health = NotificationHealth.Unknown,
                scope = backgroundScope,
            )

            harness.scheduler.liveRunning = true

            advanceUntilIdle()
            harness.viewModel.refresh()

            assertFalse(harness.viewModel.state.value.liveResumeRequired)
            assertTrue(harness.viewModel.state.value.liveRunning)
        }

    @Test
    fun turningTheStatusSwitchOnMovesToLiveAndOffMovesToOff() = runTest(UnconfinedTestDispatcher()) {
        val harness = harness(
            preferences = NotificationPreferences(),
            health = NotificationHealth.Unknown,
            scope = backgroundScope,
        )

        advanceUntilIdle()
        harness.controller.start()

        harness.viewModel.setStatusNotificationEnabled(true)
        advanceUntilIdle()

        assertEquals(SyncMode.Live, harness.viewModel.state.value.mode)
        assertTrue(harness.scheduler.liveRunning, "the status notification is what makes live sync legitimate")
        assertFalse(harness.scheduler.periodicEnqueued)

        harness.viewModel.setStatusNotificationEnabled(false)
        advanceUntilIdle()

        assertEquals(SyncMode.Off, harness.viewModel.state.value.mode)
        assertFalse(harness.scheduler.liveRunning)
    }

    @Test
    fun alertsAloneMoveToBackgroundAndScheduleTheWorker() = runTest(UnconfinedTestDispatcher()) {
        val harness = harness(
            preferences = NotificationPreferences(),
            health = NotificationHealth.Unknown,
            scope = backgroundScope,
        )

        advanceUntilIdle()
        harness.controller.start()

        harness.viewModel.setAlertsEnabled(true)
        advanceUntilIdle()

        assertEquals(SyncMode.Background, harness.viewModel.state.value.mode)
        assertTrue(harness.scheduler.periodicEnqueued, "alerts alone are served by the periodic worker")
        assertFalse(harness.scheduler.liveRunning, "and not by a foreground service")
    }

    @Test
    fun bothSwitchesOnIsStillLiveOnly() = runTest(UnconfinedTestDispatcher()) {
        val harness = harness(
            preferences = NotificationPreferences(),
            health = NotificationHealth.Unknown,
            scope = backgroundScope,
        )

        advanceUntilIdle()
        harness.controller.start()

        harness.viewModel.setStatusNotificationEnabled(true)
        advanceUntilIdle()
        harness.viewModel.setAlertsEnabled(true)
        advanceUntilIdle()

        assertEquals(SyncMode.Live, harness.viewModel.state.value.mode)
        assertFalse(
            harness.scheduler.periodicEnqueued,
            "alerts being on as well must not add a second synchronisation path",
        )
    }

    @Test
    fun anImpossibleThresholdCannotBeStored() = runTest(UnconfinedTestDispatcher()) {
        val settings = InMemoryAlertSettings()

        val harness = harness(
            preferences = NotificationPreferences(),
            health = NotificationHealth.Unknown,
            scope = backgroundScope,
            settings = settings,
        )

        advanceUntilIdle()

        val rejected = runCatching {
            settings.update { it.copy(weekly = WindowRules(warningPercent = 10.0, criticalPercent = 10.0)) }
        }.exceptionOrNull()

        assertTrue(rejected is IllegalArgumentException)

        // The stored values are untouched: a rejected change leaves nothing half-applied.
        val stored = settings.preferences.first()

        assertEquals(20.0, stored.weekly.warningPercent)
        assertEquals(10.0, stored.weekly.criticalPercent)

        harness.viewModel.refresh()
    }

    @Test
    fun theWatchFormatIsCarriedToTheTestNotification() = runTest(UnconfinedTestDispatcher()) {
        var sent: WatchFormat? = null

        val harness = harness(
            preferences = NotificationPreferences(),
            health = NotificationHealth.Unknown,
            scope = backgroundScope,
            sendTest = { format -> sent = format },
        )

        advanceUntilIdle()

        harness.viewModel.setWatchFormat(WatchFormat.Chinese)
        advanceUntilIdle()

        harness.viewModel.sendTest()
        advanceUntilIdle()

        assertEquals(WatchFormat.Chinese, sent, "the test must use the format the user chose")
        assertTrue(harness.viewModel.state.value.testNotificationSent)

        harness.viewModel.acknowledgeTest()
        assertFalse(harness.viewModel.state.value.testNotificationSent)
    }

    @Test
    fun theLanPermissionIsSurfacedSoTheScreenCanExplainAnUnreachableBridge() =
        runTest(UnconfinedTestDispatcher()) {
            val harness = harness(
                preferences = NotificationPreferences(),
                health = NotificationHealth.Unknown,
                scope = backgroundScope,
                lanPermission = MutableStateFlow(LocalNetworkPermissionState.Required),
            )

            runCurrent()

            assertEquals(LocalNetworkPermissionState.Required, harness.viewModel.state.value.lanPermission)
        }

    @Test
    fun theRepeatIntervalOfferedMatchesTheApprovedSet() {
        assertEquals(
            listOf(Duration.ofMinutes(30), Duration.ofMinutes(60), Duration.ofMinutes(120)),
            NotificationPreferences.OFFERED_REPEAT_INTERVALS,
        )
    }
}
