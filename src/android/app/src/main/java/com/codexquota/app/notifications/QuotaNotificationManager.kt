package com.codexquota.app.notifications

import android.app.Notification
import android.content.Context
import androidx.core.app.NotificationCompat
import androidx.core.app.NotificationManagerCompat
import com.codexquota.app.alerts.AlertNotifier
import com.codexquota.app.data.settings.WatchFormat
import com.codexquota.app.domain.QuotaSnapshot
import com.codexquota.app.domain.alerts.AlertAction
import java.time.Instant

/**
 * Turns render models into Android notifications.
 *
 * It is the only class that touches `NotificationManagerCompat`, which is what keeps the decision
 * about *what* to say separate from the mechanism of saying it. Ordinary status updates reuse one
 * notification id and are silent; alerts get their own ids and are allowed to interrupt.
 */
class QuotaNotificationManager(
    private val context: Context,
) : AlertNotifier {

    init {
        NotificationChannels.ensureCreated(context)
    }

    override suspend fun showStatus(
        snapshot: QuotaSnapshot?,
        receivedAt: Instant?,
        stale: Boolean,
        format: WatchFormat,
    ) {
        val model = StatusNotificationRenderer.render(snapshot, receivedAt, stale, format)

        notify(model.notificationId, StatusNotificationBuilder.build(context, model))
    }

    override suspend fun showAlerts(actions: List<AlertAction>, format: WatchFormat) {
        actions.forEach { action ->
            val model = AlertNotificationRenderer.render(action, format)

            notify(model.notificationId, AlertNotificationBuilder.build(context, model))
        }
    }

    override suspend fun cancelStatus() {
        NotificationManagerCompat.from(context).cancel(NotificationChannels.STATUS_NOTIFICATION_ID)
    }

    /**
     * Posts a notification.
     *
     * The permission check is not defensive padding: on Android 13+ posting without it is silently
     * dropped, and a caller that assumed success would report healthy delivery that never happened.
     */
    private fun notify(id: Int, notification: Notification) {
        val manager = NotificationManagerCompat.from(context)

        if (!manager.areNotificationsEnabled()) {
            return
        }

        try {
            manager.notify(id, notification)
        } catch (_: SecurityException) {
            // The permission was revoked between the check and the post. Nothing to do about it here;
            // the settings screen is where the user learns about it.
        }
    }

    /** Sends a test notification, so the end-to-end path to the watch can be checked. */
    suspend fun sendTestNotification(format: WatchFormat) {
        val model = StatusNotificationRenderer.render(
            snapshot = null,
            receivedAt = Instant.now(),
            stale = true,
            format = format,
        )

        notify(
            TEST_NOTIFICATION_ID,
            StatusNotificationBuilder.build(context, model.copy(text = "Test notification")),
        )
    }

    private companion object {
        const val TEST_NOTIFICATION_ID = 9001
    }
}

/** Builds the persistent status notification. */
object StatusNotificationBuilder {

    /** Builds the Android notification for a status render model. */
    fun build(context: Context, model: NotificationRenderModel): Notification =
        NotificationCompat.Builder(context, model.channelId)
            .setSmallIcon(android.R.drawable.stat_notify_sync)
            .setContentTitle(model.title)
            .setContentText(model.text)
            .setSubText(model.subText)
            .setOngoing(model.ongoing)
            // The status notification is updated in place many times a day, so it must not buzz or
            // re-alert each time.
            .setOnlyAlertOnce(true)
            .setSilent(!model.vibrate)
            .setPriority(NotificationCompat.PRIORITY_LOW)
            .build()
}

/** Builds warning and critical alerts. */
object AlertNotificationBuilder {

    /** Builds the Android notification for an alert render model. */
    fun build(context: Context, model: NotificationRenderModel): Notification =
        NotificationCompat.Builder(context, model.channelId)
            .setSmallIcon(android.R.drawable.stat_sys_warning)
            .setContentTitle(model.title)
            .setContentText(model.text)
            .setSubText(model.subText)
            .setOngoing(false)
            .setAutoCancel(true)
            .setPriority(NotificationCompat.PRIORITY_DEFAULT)
            .build()
}
