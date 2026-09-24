package com.codexquota.app.sync

import android.app.Notification
import android.app.Service
import android.content.Intent
import android.content.pm.ServiceInfo
import android.os.Build
import android.os.IBinder
import androidx.core.app.ServiceCompat
import com.codexquota.app.CodexQuotaApplication
import com.codexquota.app.alerts.AlertProcessingRepository
import com.codexquota.app.data.QuotaRepository
import com.codexquota.app.notifications.NotificationChannels
import com.codexquota.app.notifications.StatusNotificationBuilder
import com.codexquota.app.notifications.StatusNotificationRenderer
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.launch

/**
 * The live-mode foreground service.
 *
 * It is a `connectedDevice` service because that is the type Android documents for maintaining a
 * connection to an external device over the network, and because the alternative — `dataSync` — is
 * subject to a six-hour background limit that all-day live sync would hit.
 *
 * Two things it does not do:
 *
 * - it never starts itself again. If the user or the system stops it, it stays stopped until the user
 *   asks for it, which is why there is no auto-restart loop and no `BOOT_COMPLETED` receiver.
 * - it does not own the connection state machine. That lives in the application container; this
 *   service only decides when it runs.
 */
class LiveBridgeService : Service() {

    private val scope = CoroutineScope(SupervisorJob())

    private var connectionJob: Job? = null

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onCreate() {
        super.onCreate()

        isRunning = true
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        if (intent?.action == ACTION_STOP) {
            stopSelf()
            return START_NOT_STICKY
        }

        // The notification goes up before any connection work, because a foreground service that
        // takes time to produce its notification is a foreground service that can be killed first.
        startForegroundCompat(initialNotification())

        if (connectionJob == null) {
            val container = (application as? CodexQuotaApplication)?.container

            if (container == null) {
                stopSelf()
                return START_NOT_STICKY
            }

            connectionJob = scope.launch {
                container.connection.run()
            }
        }

        // Not sticky: a service the system killed must not be resurrected behind the user's back.
        return START_NOT_STICKY
    }

    override fun onDestroy() {
        connectionJob?.cancel()
        scope.cancel()

        isRunning = false

        super.onDestroy()
    }

    /** Puts up the foreground notification, declaring the service type on the API levels that need it. */
    private fun startForegroundCompat(notification: Notification) {
        val type = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            ServiceInfo.FOREGROUND_SERVICE_TYPE_CONNECTED_DEVICE
        } else {
            0
        }

        ServiceCompat.startForeground(this, NotificationChannels.STATUS_NOTIFICATION_ID, notification, type)
    }

    /**
     * The notification shown the instant the service starts.
     *
     * It says "connecting" rather than inventing a quota: the cache has not been read yet, and a
     * foreground notification that shows a percentage would be claiming one.
     */
    private fun initialNotification(): Notification =
        StatusNotificationBuilder.build(
            this,
            StatusNotificationRenderer.render(
                snapshot = null,
                receivedAt = null,
                stale = true,
            ),
        )

    companion object {
        /** Starts live sync. */
        const val ACTION_START = "com.codexquota.app.action.START_LIVE"

        /** Stops live sync. */
        const val ACTION_STOP = "com.codexquota.app.action.STOP_LIVE"

        /**
         * Whether this process believes the service is running.
         *
         * It is what the settings screen reads to distinguish "live sync is on" from "live sync is on
         * and running", which is exactly the difference a reboot creates.
         */
        @Volatile
        var isRunning: Boolean = false
            private set
    }
}
