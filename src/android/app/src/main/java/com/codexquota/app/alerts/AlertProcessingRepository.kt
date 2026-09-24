package com.codexquota.app.alerts

import com.codexquota.app.data.QuotaRepository
import com.codexquota.app.data.settings.AlertSettings
import com.codexquota.app.data.settings.WatchFormat
import com.codexquota.app.data.settings.current
import com.codexquota.app.domain.QuotaSnapshot
import com.codexquota.app.domain.alerts.AlertAction
import com.codexquota.app.domain.alerts.AlertEvaluation
import com.codexquota.app.domain.alerts.AlertState
import com.codexquota.app.domain.alerts.ThresholdEvaluator
import java.time.Instant

/** Where the alert state lives between evaluations. */
interface AlertStateStore {
    /** The stored state, or the default when nothing has been stored. */
    suspend fun read(): AlertState

    /** Replaces the stored state. */
    suspend fun write(state: AlertState)
}

/** Delivers notifications. */
interface AlertNotifier {
    /** Shows or updates the persistent status notification. */
    suspend fun showStatus(
        snapshot: QuotaSnapshot?,
        receivedAt: Instant?,
        stale: Boolean,
        format: WatchFormat,
    )

    /** Delivers the alerts an evaluation produced. */
    suspend fun showAlerts(actions: List<AlertAction>, format: WatchFormat)

    /** Removes the status notification, when the user turns it off. */
    suspend fun cancelStatus()
}

/**
 * The one place a trusted snapshot becomes a notification.
 *
 * Both the live service and the periodic worker go through this, which is what makes their
 * behaviour identical: the same rules, the same persisted state, and therefore no duplicate alert
 * when the user switches modes.
 *
 * The order is fixed and matters:
 *
 * 1. **evaluate** — a pure decision, before anything is written or shown;
 * 2. **persist** — the state is stored before delivery, so a process death between the two cannot
 *    replay a notification the user already saw;
 * 3. **deliver** — and only then does anything become visible.
 */
class AlertProcessingRepository(
    private val repository: QuotaRepository,
    private val alertState: AlertStateStore,
    private val settings: AlertSettings,
    private val notifier: AlertNotifier,
    private val clock: () -> Instant = Instant::now,
) {
    /** The last snapshot this repository evaluated, so a repeat is judged against the right one. */
    private var lastEvaluated: QuotaSnapshot? = null

    /**
     * Evaluates one snapshot and delivers whatever it produced.
     *
     * @param snapshot the trusted snapshot to judge.
     * @param fresh whether it may be treated as describing the present.
     */
    suspend fun process(snapshot: QuotaSnapshot, fresh: Boolean): AlertEvaluation {
        val preferences = settings.current()
        val stored = alertState.read()

        val evaluation = ThresholdEvaluator.evaluate(
            previous = lastEvaluated,
            current = snapshot,
            rules = preferences.rules,
            state = stored,
            now = clock(),
            fresh = fresh,
        )

        lastEvaluated = snapshot

        // Persisted before delivery: a crash after this point loses a notification, which is the
        // safe direction. The other order would replay one the user has already seen.
        alertState.write(evaluation.state)

        if (preferences.alertsEnabled && evaluation.hasActions) {
            notifier.showAlerts(evaluation.actions, preferences.watchFormat)
        }

        return evaluation
    }

    /**
     * Updates the persistent status notification.
     *
     * It is separate from [process] because the two switches are independent: a phone can want the
     * status notification without low-quota alerts, and an ordinary quota change must update the
     * status without ever creating an alert.
     */
    suspend fun updateStatus(snapshot: QuotaSnapshot?, receivedAt: Instant?, stale: Boolean) {
        val preferences = settings.current()

        if (preferences.statusNotificationEnabled) {
            notifier.showStatus(snapshot, receivedAt, stale, preferences.watchFormat)
        } else {
            notifier.cancelStatus()
        }
    }

    /**
     * Records that the app is no longer receiving fresh data.
     *
     * The alert state is deliberately untouched: going offline pauses repeat reminders without
     * re-arming anything, so reconnecting does not replay an alert for a level the user already
     * knows about.
     */
    suspend fun markOffline() {
        repository.markOffline()
        updateStatus(repository.current().snapshot, repository.current().receivedAt, stale = true)
    }
}
