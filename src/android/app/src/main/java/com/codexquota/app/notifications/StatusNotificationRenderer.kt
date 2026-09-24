package com.codexquota.app.notifications

import com.codexquota.app.data.settings.WatchFormat
import com.codexquota.app.domain.QuotaSnapshot
import java.time.Instant
import java.time.ZoneId
import java.time.format.DateTimeFormatter

/**
 * What a notification should say, before Android is involved.
 *
 * Splitting the text from the `Notification` is what makes "the watch will show both percentages"
 * an assertion rather than something only a phone can confirm.
 */
data class NotificationRenderModel(
    val channelId: String,
    val notificationId: Int,
    val title: String,
    val text: String,
    val subText: String?,
    /** Whether this notification is meant to interrupt the user. */
    val alerting: Boolean,
    /** Whether it should vibrate. Ordinary status updates must not. */
    val vibrate: Boolean,
    /** Whether it stays until dismissed. Only the status notification does. */
    val ongoing: Boolean,
)

/**
 * The compact labels the watch will read.
 *
 * The order is the priority the spec gives: short-window percentage, weekly percentage, short-window
 * reset time.
 */
private data class WatchLabels(
    val short: String,
    val weekly: String,
    val offline: String,
    val reset: (String) -> String,
    val dataFrom: (String) -> String,
) {
    companion object {
        /** `5h 72% | W 54%` */
        val Compact = WatchLabels(
            short = "5h",
            weekly = "W",
            offline = "Offline",
            reset = { "5h reset $it" },
            dataFrom = { "data from $it" },
        )

        /** `5小时 72% | 周 54%` */
        val Chinese = WatchLabels(
            short = "5小时",
            weekly = "周",
            offline = "离线",
            reset = { "$it 重置" },
            dataFrom = { "$it 的数据" },
        )
    }
}

/**
 * Renders the persistent status notification.
 *
 * The requirement it exists to satisfy is that a HUAWEI WATCH FIT 2 mirroring the phone's
 * notification still shows both percentages, so the compact form puts them on one line and keeps the
 * reset time on the second.
 */
object StatusNotificationRenderer {

    /** Renders the status notification for a snapshot, or for the absence of one. */
    fun render(
        snapshot: QuotaSnapshot?,
        receivedAt: Instant?,
        stale: Boolean,
        format: WatchFormat = WatchFormat.Compact,
        zone: ZoneId = ZoneId.systemDefault(),
        clock: DateTimeFormatter = DateTimeFormatter.ofPattern("HH:mm"),
    ): NotificationRenderModel {
        val labels = labelsFor(format)

        if (snapshot == null) {
            return NotificationRenderModel(
                channelId = NotificationChannels.STATUS,
                notificationId = NotificationChannels.STATUS_NOTIFICATION_ID,
                title = TITLE,
                text = labels.offline,
                subText = null,
                alerting = false,
                vibrate = false,
                ongoing = true,
            )
        }

        val short = percent(snapshot.shortWindow.remainingPercent)
        val weekly = percent(snapshot.weekly.remainingPercent)

        val body = if (stale) {
            // The stale marker is mandatory: cached numbers must never look like live ones.
            "${labels.offline} | ${labels.short} $short | ${labels.weekly} $weekly"
        } else {
            "${labels.short} $short | ${labels.weekly} $weekly"
        }

        val subText = if (stale) {
            receivedAt?.let { labels.dataFrom(clock.format(it.atZone(zone))) }
        } else {
            labels.reset(clock.format(snapshot.shortWindow.resetsAt.atZone(zone)))
        }

        return NotificationRenderModel(
            channelId = NotificationChannels.STATUS,
            notificationId = NotificationChannels.STATUS_NOTIFICATION_ID,
            title = TITLE,
            text = body,
            subText = subText,
            // The status notification is never an alert, however low the quota is.
            alerting = false,
            vibrate = false,
            ongoing = true,
        )
    }

    /** The notification title, which is also what the watch shows first. */
    const val TITLE = "Codex"

    private fun labelsFor(format: WatchFormat): WatchLabels = when (format) {
        WatchFormat.Compact -> WatchLabels.Companion.Compact
        WatchFormat.Chinese -> WatchLabels.Companion.Chinese
    }

    /** Renders a percentage without a fractional part when there is none. */
    internal fun percent(value: Double): String {
        val rounded = kotlin.math.round(value)

        return if (kotlin.math.abs(value - rounded) < 0.05) {
            "${rounded.toInt()}%"
        } else {
            "${kotlin.math.round(value * 10) / 10}%"
        }
    }
}
