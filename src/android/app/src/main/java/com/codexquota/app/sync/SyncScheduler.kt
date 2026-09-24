package com.codexquota.app.sync

import android.content.Context
import android.content.Intent
import androidx.core.content.ContextCompat
import androidx.work.ExistingPeriodicWorkPolicy
import androidx.work.PeriodicWorkRequestBuilder
import androidx.work.WorkManager
import java.util.concurrent.TimeUnit

/**
 * The Android implementation of the mode transitions.
 *
 * The two halves are deliberately different mechanisms: live mode is a foreground service that the
 * user can see and stop, and background mode is unique periodic work that the system schedules.
 * Neither is derived from the other, which is why the mutual exclusion has to be arranged explicitly
 * in [SyncModeController] rather than falling out of the design.
 *
 * The running flags are the app's own record of what it asked for. They are not a substitute for
 * querying the system, but they are what the UI needs to say "live sync is enabled but not running",
 * which is the state a reboot leaves the phone in.
 */
class WorkManagerSyncScheduler(
    private val context: Context,
    private val workManager: WorkManager,
) : SyncScheduler {

    override fun startLive() {
        ContextCompat.startForegroundService(
            context,
            Intent(context, LiveBridgeService::class.java).setAction(LiveBridgeService.ACTION_START),
        )
    }

    override fun stopLive() {
        // `stopService` rather than a start intent carrying a stop action: it is permitted from any
        // process state, and the service's own `onDestroy` is where the connection is closed
        // gracefully. An explicit stop, not a "restart if it comes back": the service stays stopped
        // until the user asks for it again.
        context.stopService(Intent(context, LiveBridgeService::class.java))
    }

    override fun enqueuePeriodic() {
        // ExistingPeriodicWorkPolicy.UPDATE keeps exactly one job under this name, so re-applying the
        // same mode cannot accumulate a second schedule.
        workManager.enqueueUniquePeriodicWork(
            PERIODIC_WORK_NAME,
            ExistingPeriodicWorkPolicy.UPDATE,
            PeriodicWorkRequestBuilder<BackgroundSyncWorker>(
                PERIODIC_INTERVAL_MINUTES,
                TimeUnit.MINUTES,
            ).build(),
        )

        enqueued = true
    }

    override fun cancelPeriodic() {
        workManager.cancelUniqueWork(PERIODIC_WORK_NAME)

        enqueued = false
    }

    override fun isLiveRunning(): Boolean = LiveBridgeService.isRunning

    override fun isPeriodicEnqueued(): Boolean = enqueued

    /**
     * What this process last asked WorkManager for.
     *
     * WorkManager's own answer is asynchronous, and the question the UI asks — "did the user's
     * setting get applied?" — is about intent, not about the scheduler's internal bookkeeping.
     */
    @Volatile
    private var enqueued: Boolean = false
}
