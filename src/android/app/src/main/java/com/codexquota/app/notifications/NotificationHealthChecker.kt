package com.codexquota.app.notifications

import android.app.NotificationChannel
import android.app.NotificationManager
import android.content.Context
import androidx.core.app.NotificationManagerCompat

/**
 * Whether notifications can actually reach the user.
 *
 * The distinction this type exists to preserve is between "the app's switch is on" and "the system
 * will deliver it". They are different facts, and reporting the first as the second is exactly the
 * mistake the spec forbids: a phone with notifications disabled must not be described as healthy.
 */
data class NotificationHealth(
    /** Whether the OS-level notification permission is granted. */
    val permissionGranted: Boolean,

    /** Whether the status channel is enabled and not blocked. */
    val statusChannelEnabled: Boolean,

    /** Whether the alerts channel is enabled and not blocked. */
    val alertsChannelEnabled: Boolean,
) {
    /**
     * Whether delivery may be claimed for a feature that is switched on.
     *
     * A channel that does not exist yet counts as available: the app creates them on first use, and
     * treating a missing channel as blocked would report a problem that does not exist.
     */
    fun canDeliver(channelId: String): Boolean = when (channelId) {
        NotificationChannels.STATUS -> permissionGranted && statusChannelEnabled
        NotificationChannels.ALERTS -> permissionGranted && alertsChannelEnabled
        else -> permissionGranted
    }

    /** Whether anything at all can be delivered. */
    val canDeliverAnything: Boolean get() = permissionGranted

    companion object {
        /** The state before anything has been checked. */
        val Unknown = NotificationHealth(
            permissionGranted = true,
            statusChannelEnabled = true,
            alertsChannelEnabled = true,
        )
    }
}

/** Reads the platform's notification state. */
interface NotificationHealthChecker {
    /** The current health. */
    fun read(): NotificationHealth
}

/**
 * The Android implementation.
 *
 * It uses only public APIs — `areNotificationsEnabled` and the channel's importance — because the
 * alternative is reaching into a vendor's private settings, which the spec forbids.
 */
class AndroidNotificationHealthChecker(private val context: Context) : NotificationHealthChecker {

    override fun read(): NotificationHealth {
        val compat = NotificationManagerCompat.from(context)
        val manager = context.getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager

        return NotificationHealth(
            permissionGranted = compat.areNotificationsEnabled(),
            statusChannelEnabled = manager.isChannelEnabled(NotificationChannels.STATUS),
            alertsChannelEnabled = manager.isChannelEnabled(NotificationChannels.ALERTS),
        )
    }

    /**
     * A channel that has not been created yet is not blocked.
     *
     * `NotificationManager.getNotificationChannel` returns `null` before the app has created it, and
     * the channels are created on first use.
     */
    private fun NotificationManager.isChannelEnabled(id: String): Boolean {
        val channel: NotificationChannel = getNotificationChannel(id) ?: return true

        return channel.importance != NotificationManager.IMPORTANCE_NONE
    }
}
