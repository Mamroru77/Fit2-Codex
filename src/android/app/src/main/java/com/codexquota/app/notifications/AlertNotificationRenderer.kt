package com.codexquota.app.notifications

import com.codexquota.app.data.settings.WatchFormat
import com.codexquota.app.domain.alerts.AlertAction
import com.codexquota.app.domain.alerts.AlertSeverity
import com.codexquota.app.domain.alerts.QuotaWindowKind

/**
 * Renders warning and critical alerts.
 *
 * Each alert gets its own notification id, derived from the window and severity, so a warning and a
 * critical about the same window do not replace each other and a weekly alert never hides a
 * short-window one.
 */
object AlertNotificationRenderer {

    /** Renders one alert. */
    fun render(
        action: AlertAction,
        format: WatchFormat = WatchFormat.Compact,
    ): NotificationRenderModel {
        val shortLabel = when (format) {
            WatchFormat.Compact -> "5h"
            WatchFormat.Chinese -> "5小时"
        }
        val weeklyLabel = when (format) {
            WatchFormat.Compact -> "W"
            WatchFormat.Chinese -> "周"
        }

        val windowLabel = when (action.window) {
            QuotaWindowKind.ShortWindow -> shortLabel
            QuotaWindowKind.Weekly -> weeklyLabel
        }

        val percent = StatusNotificationRenderer.percent(action.remainingPercent)

        val body = when (action.severity) {
            AlertSeverity.Warning -> when (format) {
                WatchFormat.Compact -> "$windowLabel left $percent"
                WatchFormat.Chinese -> "$windowLabel 剩余 $percent"
            }

            AlertSeverity.Critical -> when (format) {
                WatchFormat.Compact -> "$windowLabel only $percent left"
                WatchFormat.Chinese -> "$windowLabel 仅剩 $percent"
            }
        }

        return NotificationRenderModel(
            channelId = NotificationChannels.ALERTS,
            notificationId = notificationId(action),
            title = "${WARNING_MARK} Codex",
            text = body,
            subText = if (action.isRepeat) repeatSubText(format) else null,
            // This is the channel the user is meant to notice.
            alerting = true,
            vibrate = true,
            ongoing = false,
        )
    }

    /**
     * A stable id per window and severity.
     *
     * Deterministic rather than generated, so a repeat replaces its own previous alert instead of
     * stacking a second copy in the shade.
     */
    fun notificationId(action: AlertAction): Int {
        val window = when (action.window) {
            QuotaWindowKind.ShortWindow -> 1
            QuotaWindowKind.Weekly -> 2
        }
        val severity = when (action.severity) {
            AlertSeverity.Warning -> 0
            AlertSeverity.Critical -> 1
        }

        return BASE_ID + window * 10 + severity
    }

    private fun repeatSubText(format: WatchFormat): String = when (format) {
        WatchFormat.Compact -> "Still low"
        WatchFormat.Chinese -> "仍然偏低"
    }

    private const val WARNING_MARK = "\u26A0"

    private const val BASE_ID = 2000
}
