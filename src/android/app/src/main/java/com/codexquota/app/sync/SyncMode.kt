package com.codexquota.app.sync

import com.codexquota.app.data.settings.NotificationPreferences

/**
 * The three background modes.
 *
 * There are exactly three, and which one is active is derived from the two notification switches
 * rather than being a third setting the user has to keep consistent with them.
 */
enum class SyncMode {
    /** No foreground service and no periodic work. */
    Off,

    /** Exactly one periodic worker, and no long-lived connection. */
    Background,

    /** A `connectedDevice` foreground service, and no periodic worker. */
    Live,
    ;

    companion object {
        /**
         * The approved decision table.
         *
         * The status notification is what makes a foreground service legitimate: without it there is
         * no visible notification, so live sync is not offered. Low-quota alerts alone are served by
         * the low-power worker instead.
         */
        fun forPreferences(preferences: NotificationPreferences): SyncMode = when {
            preferences.statusNotificationEnabled -> Live
            preferences.alertsEnabled -> Background
            else -> Off
        }
    }
}

/**
 * The things a mode change has to do.
 *
 * It is an interface so the transition rules — including their order — are testable without
 * WorkManager, a service, or an Android device.
 */
interface SyncScheduler {
    /** Starts the connected-device foreground service. */
    fun startLive()

    /** Stops the foreground service, explicitly. */
    fun stopLive()

    /** Enqueues the unique periodic worker. */
    fun enqueuePeriodic()

    /** Cancels the periodic worker. */
    fun cancelPeriodic()

    /** Whether the foreground service is currently running. */
    fun isLiveRunning(): Boolean

    /** Whether the periodic worker is currently enqueued. */
    fun isPeriodicEnqueued(): Boolean
}

/** The unique work name, so there can only ever be one periodic sync. */
const val PERIODIC_WORK_NAME = "codex_quota_periodic_sync"

/** The periodic interval. Fifteen minutes is the platform's minimum. */
const val PERIODIC_INTERVAL_MINUTES = 15L
