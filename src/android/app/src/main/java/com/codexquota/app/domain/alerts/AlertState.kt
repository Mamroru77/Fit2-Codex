package com.codexquota.app.domain.alerts

import java.time.Instant

/**
 * Where one window is in the warning/critical cycle.
 *
 * [CriticalTriggered] implies the warning was crossed too, which is why a jump straight to critical
 * produces one notification and not two.
 */
enum class WindowTriggerState {
    Normal,
    WarningTriggered,
    CriticalTriggered,
}

/**
 * The persisted state of one window.
 *
 * [lastCriticalNotificationAt] is a UTC instant rather than an elapsed counter because it has to
 * survive the process being killed; the interval is measured from it.
 */
data class WindowAlertState(
    val state: WindowTriggerState = WindowTriggerState.Normal,
    val lastCriticalNotificationAt: Instant? = null,
)

/**
 * The persisted alert state of both windows.
 *
 * The reset times are kept so a window reset can be detected before thresholds are evaluated: a new
 * window starts at 100%, and without this the jump from 9% to 100% would look like a recovery rather
 * than a new window.
 */
data class AlertState(
    val shortWindow: WindowAlertState = WindowAlertState(),
    val weekly: WindowAlertState = WindowAlertState(),
    val shortWindowResetsAt: Instant? = null,
    val weeklyResetsAt: Instant? = null,
) {
    /** The state of one window. */
    fun forWindow(window: QuotaWindowKind): WindowAlertState = when (window) {
        QuotaWindowKind.ShortWindow -> shortWindow
        QuotaWindowKind.Weekly -> weekly
    }

    /** Replaces one window's state. */
    fun withWindow(window: QuotaWindowKind, value: WindowAlertState): AlertState = when (window) {
        QuotaWindowKind.ShortWindow -> copy(shortWindow = value)
        QuotaWindowKind.Weekly -> copy(weekly = value)
    }

    /** The reset time recorded for one window. */
    fun resetsAt(window: QuotaWindowKind): Instant? = when (window) {
        QuotaWindowKind.ShortWindow -> shortWindowResetsAt
        QuotaWindowKind.Weekly -> weeklyResetsAt
    }

    /** Records the reset time of one window. */
    fun withResetsAt(window: QuotaWindowKind, value: Instant): AlertState = when (window) {
        QuotaWindowKind.ShortWindow -> copy(shortWindowResetsAt = value)
        QuotaWindowKind.Weekly -> copy(weeklyResetsAt = value)
    }
}
