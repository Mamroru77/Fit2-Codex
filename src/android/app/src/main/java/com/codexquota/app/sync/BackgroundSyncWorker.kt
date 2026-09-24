package com.codexquota.app.sync

import android.content.Context
import androidx.work.CoroutineWorker
import androidx.work.ListenableWorker
import androidx.work.WorkerFactory
import androidx.work.WorkerParameters
import com.codexquota.app.AppContainer
import com.codexquota.app.data.RefreshResult

/**
 * The low-power mode's one job.
 *
 * It performs a single short REST refresh and nothing else: no WebSocket, no foreground service, and
 * no retry loop of its own. When the Bridge is unreachable it asks WorkManager to try again later,
 * which is the platform's backoff rather than a loop this app would have to police.
 *
 * The alert decision is not made here: it is made by [LiveSyncCoordinator], so a quota change is
 * judged identically whether it arrived from this worker or from a live connection.
 */
class BackgroundSyncWorker(
    context: Context,
    parameters: WorkerParameters,
    private val container: AppContainer,
) : CoroutineWorker(context, parameters) {

    override suspend fun doWork(): Result {
        val repository = container.repository

        return when (val result = repository.refreshCurrent()) {
            is RefreshResult.Updated -> {
                container.liveCoordinator.onTrustedSnapshot(result.snapshot)
                Result.success()
            }

            is RefreshResult.Unchanged -> {
                // The Bridge re-sent the same snapshot. The cache is already current, and the status
                // notification is refreshed so a long-idle phone still shows a fresh timestamp.
                val cached = repository.current()
                container.alerts.updateStatus(cached.snapshot, cached.receivedAt, stale = false)
                Result.success()
            }

            is RefreshResult.Offline -> {
                // No alert, and the status is relabelled rather than cleared. WorkManager's own
                // backoff handles the retry.
                container.liveCoordinator.onDisconnected()
                Result.retry()
            }

            is RefreshResult.SourceAuthRequired -> {
                // The Bridge is reachable; Codex is not logged in. That is not a quota alert, and
                // retrying will not fix it.
                container.liveCoordinator.onDisconnected()
                Result.success()
            }

            is RefreshResult.SourceUnavailable,
            is RefreshResult.ProtocolError,
            -> {
                // Nothing trustworthy arrived, so nothing is alerted on.
                container.liveCoordinator.onDisconnected()
                Result.success()
            }

            RefreshResult.RepairRequired,
            is RefreshResult.SecurityError,
            -> {
                // Both need the user. Retrying would either fail the same way or, in the security
                // case, mean offering the credential to a Bridge that is not the paired one.
                container.liveCoordinator.onDisconnected()
                Result.success()
            }
        }
    }

    /** The unique name this work is scheduled under. */
    companion object {
        /** Kept in step with [PERIODIC_WORK_NAME] so there is only ever one periodic job. */
        const val UNIQUE_NAME: String = PERIODIC_WORK_NAME
    }
}

/**
 * Supplies the worker's dependencies.
 *
 * A worker is constructed by WorkManager, so its repository has to arrive through a factory rather
 * than a constructor call. This is the only place that wiring happens.
 */
class CodexQuotaWorkerFactory(
    private val container: AppContainer,
) : WorkerFactory() {

    override fun createWorker(
        appContext: Context,
        workerClassName: String,
        workerParameters: WorkerParameters,
    ): ListenableWorker? = when (workerClassName) {
        BackgroundSyncWorker::class.java.name ->
            BackgroundSyncWorker(appContext, workerParameters, container)

        else -> null
    }
}
