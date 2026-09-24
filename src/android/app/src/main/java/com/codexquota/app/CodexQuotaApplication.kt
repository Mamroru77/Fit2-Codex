package com.codexquota.app

import android.app.Application

/**
 * The application object.
 *
 * It exists to hold the composition root for the whole process. A foreground service can be started
 * when no activity exists, and it still needs the same pairing store, cache and connection manager,
 * so they cannot live in the activity.
 */
class CodexQuotaApplication : Application() {
    /** The process-wide composition root. */
    val container: AppContainer by lazy { AppContainer(this) }
}
