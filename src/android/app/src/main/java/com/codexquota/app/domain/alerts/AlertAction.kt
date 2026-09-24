package com.codexquota.app.domain.alerts

/** How serious an alert is. */
enum class AlertSeverity {
    Warning,
    Critical,
}

/**
 * One notification the evaluator decided should be delivered.
 *
 * The evaluator produces these and nothing else: it does not touch a `NotificationManager`, a
 * channel or a service. Delivery is a separate concern, which is what makes the rules testable as
 * plain values.
 */
data class AlertAction(
    val window: QuotaWindowKind,
    val severity: AlertSeverity,
    /** The remaining percentage that caused it. */
    val remainingPercent: Double,
    /** Whether this is a repeat of a critical state rather than a fresh crossing. */
    val isRepeat: Boolean = false,
)

/** The evaluator's answer: the state to persist, and what to deliver. */
data class AlertEvaluation(
    val state: AlertState,
    val actions: List<AlertAction>,
) {
    /** Whether anything should be delivered. */
    val hasActions: Boolean get() = actions.isNotEmpty()
}
