package com.codexquota.app.data.settings

import com.codexquota.app.domain.alerts.AlertRules
import com.codexquota.app.domain.alerts.RepeatRule
import com.codexquota.app.domain.alerts.WindowRules
import java.time.Duration

/** How much text the watch should be asked to show. */
enum class WatchFormat {
    /** `5h 72% | W 54%` — fits a watch line. */
    Compact,

    /** `5小时 72% | 周 54%` — the same information in Chinese. */
    Chinese,
}

/**
 * Everything the user can choose about notifications.
 *
 * The two switches are independent on purpose: the spec allows status-only, alerts-only, both or
 * neither, and the run mode is derived from them rather than being a third setting the user has to
 * keep in step.
 */
data class NotificationPreferences(
    /** The persistent status notification. Off until the user asks for it. */
    val statusNotificationEnabled: Boolean = false,

    /** Low-quota alerts. Off until the user asks for them. */
    val alertsEnabled: Boolean = false,

    /** Whether a critical state reminds the user again while it lasts. */
    val repeatCriticalEnabled: Boolean = false,

    /** How often that reminder repeats. */
    val repeatInterval: Duration = RepeatRule.DEFAULT_INTERVAL,

    /** The short-window thresholds. */
    val shortWindow: WindowRules = WindowRules(),

    /** The weekly thresholds, independent of the short-window ones. */
    val weekly: WindowRules = WindowRules(),

    /** How compact the watch-facing text should be. */
    val watchFormat: WatchFormat = WatchFormat.Compact,
) {
    /** The alert rules these preferences describe. */
    val rules: AlertRules
        get() = AlertRules(
            shortWindow = shortWindow,
            weekly = weekly,
            repeatCritical = RepeatRule(repeatCriticalEnabled, repeatInterval),
        )

    companion object {
        /** The defaults the spec approves: both switches off, both windows at 20/10, repeat off. */
        val Default = NotificationPreferences()

        /** The intervals the settings screen offers for the repeat reminder. */
        val OFFERED_REPEAT_INTERVALS: List<Duration> = RepeatRule.OFFERED_INTERVALS
    }
}

/**
 * A rejected threshold configuration.
 *
 * The rules are enforced where the values live, so an impossible pair cannot be stored and then
 * discovered later by the evaluator.
 */
class InvalidThresholdsException(message: String) : IllegalArgumentException(message)

/**
 * Checks a threshold pair before it is stored.
 *
 * @throws InvalidThresholdsException when the pair is not usable.
 */
fun requireValidThresholds(warning: Double, critical: Double) {
    if (warning !in WindowRules.VALID_RANGE) {
        throw InvalidThresholdsException("The warning threshold must be between 1% and 99%.")
    }

    if (critical !in WindowRules.VALID_RANGE) {
        throw InvalidThresholdsException("The critical threshold must be between 1% and 99%.")
    }

    if (critical >= warning) {
        throw InvalidThresholdsException("The critical threshold must be below the warning threshold.")
    }
}
