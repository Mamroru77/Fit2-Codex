package com.codexquota.app

import android.app.Application
import androidx.work.Configuration
import com.codexquota.app.sync.CodexQuotaWorkerFactory

/**
 * The application object.
 *
 * It exists to hold the composition root for the whole process. A foreground service can be started
 * when no activity exists, and it still needs the same pairing store, cache and connection manager,
 * so they cannot live in the activity.
 *
 * It is also where WorkManager is configured. `BackgroundSyncWorker` takes its dependencies as
 * constructor parameters, so WorkManager cannot build it with its default factory — it has to be
 * given one. Without this the periodic worker is enqueued and then fails every time it runs, which
 * is the failure mode background mode would have shipped with.
 */
class CodexQuotaApplication : Application(), Configuration.Provider {

    /** The process-wide composition root. */
    val container: AppContainer by lazy { AppContainer(this) }

    override val workManagerConfiguration: Configuration
        get() = Configuration.Builder()
            .setWorkerFactory(CodexQuotaWorkerFactory(container))
            .build()
}
