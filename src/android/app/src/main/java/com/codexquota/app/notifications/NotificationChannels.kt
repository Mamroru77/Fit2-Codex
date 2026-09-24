package com.codexquota.app.notifications

import android.app.NotificationChannel
import android.app.NotificationManager
import android.content.Context

/**
 * The two notification channels.
 *
 * They are created on first initialisation and never recreated: changing a channel's importance after
 * the fact is ignored by the platform, so the values here are the ones the user will live with.
 */
object NotificationChannels {

    /** The persistent status notification: quiet, updated in place, never an alert. */
    const val STATUS = "codex_status"

    /** Low-quota alerts: user-visible, and the only channel that ever vibrates. */
    const val ALERTS = "codex_alerts"

    /** The fixed identifier of the single status notification. */
    const val STATUS_NOTIFICATION_ID = 1001

    /** Creates both channels. Safe to call more than once. */
    fun ensureCreated(context: Context) {
        val manager = context.getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager

        // LOW importance with no vibration: an ordinary quota change must not buzz the user.
        manager.createNotificationChannel(
            NotificationChannel(
                STATUS,
                "Quota status",
                NotificationManager.IMPORTANCE_LOW,
            ).apply {
                description = "The current Codex quota, updated in place."
                enableVibration(false)
                setShowBadge(false)
            },
        )

        // DEFAULT importance: this is the channel the user is meant to notice.
        manager.createNotificationChannel(
            NotificationChannel(
                ALERTS,
                "Quota alerts",
                NotificationManager.IMPORTANCE_DEFAULT,
            ).apply {
                description = "Warnings and critical alerts when the Codex quota runs low."
            },
        )
    }
}
