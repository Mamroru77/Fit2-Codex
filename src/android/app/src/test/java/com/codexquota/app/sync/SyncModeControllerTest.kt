package com.codexquota.app.sync

import com.codexquota.app.data.settings.InMemoryAlertSettings
import com.codexquota.app.data.settings.NotificationPreferences
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue
import kotlinx.coroutines.test.runTest

/**
 * The mode exclusivity invariant.
 *
 * "Live foreground sync and the periodic worker must never run at the same time" is the one rule this
 * class exists to hold, so these tests assert both the end state and the order of the calls that
 * produced it: a correct end state reached by an incorrect order still had a window where both were
 * running.
 */
class SyncModeControllerTest {

    /** A scheduler that records the order of everything it was asked to do. */
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

    private fun controller(scheduler: RecordingScheduler, scope: kotlinx.coroutines.CoroutineScope) =
        SyncModeController(
            settings = InMemoryAlertSettings(),
            scheduler = scheduler,
            scope = scope,
        )

    @Test
    fun bothSwitchesOffLeavesNothingRunning() = runTest {
        val scheduler = RecordingScheduler()
        val controller = controller(scheduler, backgroundScope)

        controller.applyMode(SyncMode.Off)

        assertFalse(scheduler.liveRunning)
        assertFalse(scheduler.periodicEnqueued)
        assertEquals(SyncMode.Off, controller.mode.value)
    }

    @Test
    fun alertsOnlyEnqueuesTheWorkerAndStopsTheService() = runTest {
        val scheduler = RecordingScheduler()
        val controller = controller(scheduler, backgroundScope)

        controller.applyMode(SyncMode.Background)

        assertFalse(scheduler.liveRunning)
        assertTrue(scheduler.periodicEnqueued)
        assertEquals(SyncMode.Background, controller.mode.value)
    }

    @Test
    fun theServiceIsStoppedBeforeTheWorkerIsEnqueued() = runTest {
        val scheduler = RecordingScheduler()
        val controller = controller(scheduler, backgroundScope)

        controller.applyMode(SyncMode.Background)

        // Order, not just outcome: enqueueing first would leave an instant where both could run.
        assertEquals(listOf("stopLive", "enqueuePeriodic"), scheduler.calls)
    }

    @Test
    fun statusOnTakesLiveModeAndCancelsTheWorkerFirst() = runTest {
        val scheduler = RecordingScheduler()
        val controller = controller(scheduler, backgroundScope)

        controller.applyMode(SyncMode.Live)

        assertTrue(scheduler.liveRunning)
        assertFalse(scheduler.periodicEnqueued)
        assertEquals(SyncMode.Live, controller.mode.value)

        // The same ordering rule, in the other direction.
        assertEquals(listOf("cancelPeriodic", "startLive"), scheduler.calls)
    }

    @Test
    fun statusOnWithAlertsOnIsStillLiveOnly() = runTest {
        val scheduler = RecordingScheduler()
        val controller = controller(scheduler, backgroundScope)

        controller.applyMode(SyncMode.Live)

        assertTrue(scheduler.liveRunning)
        assertFalse(
            scheduler.periodicEnqueued,
            "alerts being on as well must not add a second synchronisation path",
        )
    }

    @Test
    fun switchingFromLiveToBackgroundStopsTheServiceBeforeScheduling() = runTest {
        val scheduler = RecordingScheduler()
        val controller = controller(scheduler, backgroundScope)

        controller.applyMode(SyncMode.Live)
        scheduler.calls.clear()

        controller.applyMode(SyncMode.Background)

        assertEquals(listOf("stopLive", "enqueuePeriodic"), scheduler.calls)
        assertFalse(scheduler.liveRunning)
        assertTrue(scheduler.periodicEnqueued)
    }

    @Test
    fun switchingFromBackgroundToLiveCancelsTheWorkerBeforeStarting() = runTest {
        val scheduler = RecordingScheduler()
        val controller = controller(scheduler, backgroundScope)

        controller.applyMode(SyncMode.Background)
        scheduler.calls.clear()

        controller.applyMode(SyncMode.Live)

        assertEquals(listOf("cancelPeriodic", "startLive"), scheduler.calls)
        assertTrue(scheduler.liveRunning)
        assertFalse(scheduler.periodicEnqueued)
    }

    @Test
    fun applyingTheSameModeTwiceDoesNotScheduleASecondWorker() = runTest {
        val scheduler = RecordingScheduler()
        val controller = controller(scheduler, backgroundScope)

        controller.applyMode(SyncMode.Background)
        scheduler.calls.clear()

        controller.applyMode(SyncMode.Background)

        assertTrue(scheduler.calls.isEmpty(), "re-applying the same mode must be a no-op")
    }

    @Test
    fun applyingOffTwiceDoesNotDisturbAnything() = runTest {
        val scheduler = RecordingScheduler()
        val controller = controller(scheduler, backgroundScope)

        controller.applyMode(SyncMode.Off)
        scheduler.calls.clear()

        controller.applyMode(SyncMode.Off)

        assertTrue(scheduler.calls.isEmpty())
    }

    @Test
    fun resumingLiveStartsTheServiceEvenWhenTheModeAlreadySaysLive() = runTest {
        // This is the reboot case: the setting survived, the service did not.
        val scheduler = RecordingScheduler()
        val controller = controller(scheduler, backgroundScope)

        controller.applyMode(SyncMode.Live)
        scheduler.calls.clear()

        controller.resumeLive()

        assertEquals(listOf("cancelPeriodic", "startLive"), scheduler.calls)
        assertTrue(scheduler.liveRunning)
    }

    @Test
    fun theDecisionTableMatchesTheApprovedOne() {
        // (status, alerts) -> mode
        assertEquals(SyncMode.Off, SyncMode.forPreferences(preferences(status = false, alerts = false)))
        assertEquals(SyncMode.Background, SyncMode.forPreferences(preferences(status = false, alerts = true)))
        assertEquals(SyncMode.Live, SyncMode.forPreferences(preferences(status = true, alerts = false)))
        assertEquals(SyncMode.Live, SyncMode.forPreferences(preferences(status = true, alerts = true)))
    }

    @Test
    fun followingThePreferencesAppliesTheMatchingMode() = runTest {
        val scheduler = RecordingScheduler()
        val settings = InMemoryAlertSettings(preferences(status = false, alerts = true))
        val controller = SyncModeController(settings, scheduler, backgroundScope)

        controller.applyCurrent()

        assertEquals(SyncMode.Background, controller.mode.value)
        assertTrue(scheduler.periodicEnqueued)
    }

    private fun preferences(status: Boolean, alerts: Boolean) = NotificationPreferences(
        statusNotificationEnabled = status,
        alertsEnabled = alerts,
    )
}
