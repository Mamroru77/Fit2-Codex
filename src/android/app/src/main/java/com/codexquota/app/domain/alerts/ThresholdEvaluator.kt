package com.codexquota.app.domain.alerts

import com.codexquota.app.domain.QuotaSnapshot
import java.time.Duration
import java.time.Instant

/**
 * Decides whether a quota change deserves a notification.
 *
 * It is a pure function of the previous snapshot, the current one, the rules, the stored state and
 * the time. It has no side effects and no dependencies, which is what makes the approved matrix —
 * including the awkward cases like "a jump from 25% to 9% is one critical, not a warning then a
 * critical" — a set of ordinary assertions rather than an integration test.
 *
 * Three rules it enforces throughout:
 *
 * 1. **Stale data never raises anything.** A threshold crossing is a claim about the present, and
 *    data that is not current cannot support it.
 * 2. **A crossing fires once.** The state records that it fired, so the next snapshot at the same
 *    level is not a second alert.
 * 3. **A window reset clears the state before the new window is judged.** A new window starting at
 *    100% is not a recovery, and the old window's triggers do not carry into it.
 */
object ThresholdEvaluator {

    /**
     * Evaluates one trusted snapshot.
     *
     * @param previous the snapshot evaluated before this one, or `null` for the first ever trusted one.
     * @param current the snapshot just received.
     * @param rules this phone's thresholds.
     * @param state the persisted alert state.
     * @param now the current UTC instant, used for the repeat interval.
     * @param fresh whether [current] may be treated as describing the present.
     */
    fun evaluate(
        previous: QuotaSnapshot?,
        current: QuotaSnapshot,
        rules: AlertRules,
        state: AlertState,
        now: Instant,
        fresh: Boolean,
    ): AlertEvaluation {
        if (!fresh) {
            // Stale or offline data is not evidence of anything happening now, so it neither raises a
            // new alert nor advances the repeat timer.
            return AlertEvaluation(state, emptyList())
        }

        var working = clearResetWindows(previous, state, current)
        val actions = mutableListOf<AlertAction>()

        for (window in QuotaWindowKind.entries) {
            val remaining = current.remainingPercent(window)

            val result = evaluateWindow(
                window = window,
                remaining = remaining,
                rules = rules.forWindow(window),
                state = working.forWindow(window),
                repeat = rules.repeatCritical,
                now = now,
            )

            working = working.withWindow(window, result.state)

            // A window that produced no action is the common case: only a crossing notifies.
            result.action?.let { actions += it }
        }

        return AlertEvaluation(working, actions)
    }

    /** One window's transition. */
    private fun evaluateWindow(
        window: QuotaWindowKind,
        remaining: Double,
        rules: WindowRules,
        state: WindowAlertState,
        repeat: RepeatRule,
        now: Instant,
    ): WindowResult {
        val atCritical = remaining <= rules.criticalPercent
        val atWarning = remaining <= rules.warningPercent

        return when {
            // At or below critical: the only case that can notify more than once.
            atCritical -> criticalWindow(window, remaining, state, repeat, now)

            // Between the two thresholds. Arriving here from critical is what re-arms critical,
            // and arriving from normal is the crossing that notifies.
            atWarning -> if (state.state == WindowTriggerState.Normal) {
                WindowResult(
                    state = WindowAlertState(WindowTriggerState.WarningTriggered),
                    action = AlertAction(window, AlertSeverity.Warning, remaining, isRepeat = false),
                )
            } else {
                // Already at or past warning: the notification has been delivered, so the state is
                // carried forward without repeating it.
                WindowResult(state.copy(state = WindowTriggerState.WarningTriggered), null)
            }

            // Above warning: both thresholds are re-armed.
            else -> WindowResult(WindowAlertState(state = WindowTriggerState.Normal), null)
        }
    }

    private fun criticalWindow(
        window: QuotaWindowKind,
        remaining: Double,
        state: WindowAlertState,
        repeat: RepeatRule,
        now: Instant,
    ): WindowResult {
        val alreadyCritical = state.state == WindowTriggerState.CriticalTriggered

        if (!alreadyCritical) {
            // The first time this window reaches critical, whether it arrived from above warning or
            // jumped straight past it. One notification, not two.
            return WindowResult(
                state = WindowAlertState(WindowTriggerState.CriticalTriggered, now),
                action = AlertAction(window, AlertSeverity.Critical, remaining, isRepeat = false),
            )
        }

        val dueAt = state.lastCriticalNotificationAt?.plus(repeat.interval)
        val due = repeat.enabled && (dueAt == null || !now.isBefore(dueAt))

        return if (due) {
            WindowResult(
                state = state.copy(lastCriticalNotificationAt = now),
                action = AlertAction(window, AlertSeverity.Critical, remaining, isRepeat = true),
            )
        } else {
            WindowResult(state, null)
        }
    }

    /**
     * Clears the state of any window that has been replaced since the last evaluation.
     *
     * The reset time moving forward is the signal: the Bridge reports when the window ends, so a new
     * value means a new window and the previous triggers no longer apply to it.
     */
    private fun clearResetWindows(
        previous: QuotaSnapshot?,
        state: AlertState,
        current: QuotaSnapshot,
    ): AlertState {
        var working = state

        for (window in QuotaWindowKind.entries) {
            val resetsAt = current.resetsAt(window)

            // Two independent signals, because either can be the only one available: the stored
            // reset time survives a restart, and the previous snapshot covers the case where the app
            // has never stored one yet.
            val stored = state.resetsAt(window)
            val fromPrevious = previous?.resetsAt(window)

            val movedOn = (stored != null && resetsAt.isAfter(stored)) ||
                (fromPrevious != null && resetsAt.isAfter(fromPrevious))

            if (movedOn) {
                working = working.withWindow(window, WindowAlertState())
            }

            working = working.withResetsAt(window, resetsAt)
        }

        return working
    }

    /** Whether a repeat is due, for a caller that wants to show it before evaluating. */
    fun repeatDue(state: WindowAlertState, repeat: RepeatRule, now: Instant): Boolean {
        if (!repeat.enabled || state.state != WindowTriggerState.CriticalTriggered) {
            return false
        }

        val last = state.lastCriticalNotificationAt ?: return true

        return Duration.between(last, now) >= repeat.interval
    }

    private data class WindowResult(
        val state: WindowAlertState,
        val action: AlertAction?,
    )

    /** The remaining percentage of one window. */
    private fun QuotaSnapshot.remainingPercent(window: QuotaWindowKind): Double = when (window) {
        QuotaWindowKind.ShortWindow -> shortWindow.remainingPercent
        QuotaWindowKind.Weekly -> weekly.remainingPercent
    }

    /** The reset time of one window. */
    private fun QuotaSnapshot.resetsAt(window: QuotaWindowKind): Instant = when (window) {
        QuotaWindowKind.ShortWindow -> shortWindow.resetsAt
        QuotaWindowKind.Weekly -> weekly.resetsAt
    }
}
