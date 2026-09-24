package com.codexquota.app.domain.alerts

import com.codexquota.app.domain.QuotaSnapshot
import com.codexquota.app.testing.snapshot
import java.time.Duration
import java.time.Instant
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertTrue

/**
 * The approved alert matrix.
 *
 * Every case the design names is here as an ordinary assertion, because the evaluator is a pure
 * function: the value of writing the rules down this way is that a future change to them has to
 * break a test that says what the behaviour was.
 */
class ThresholdEvaluatorTest {

    private val now = Instant.parse("2026-09-22T13:30:00Z")
    private val shortReset = Instant.parse("2026-09-22T15:42:00Z")
    private val weeklyReset = Instant.parse("2026-09-28T00:00:00Z")

    private val rules = AlertRules()

    /** A snapshot with the two windows at the given remaining percentages. */
    private fun snap(
        short: Double,
        weekly: Double = 54.0,
        shortResetsAt: Instant = shortReset,
    ): QuotaSnapshot = snapshot(
        shortRemaining = short,
        weeklyRemaining = weekly,
        resetsAt = shortResetsAt,
        generatedAt = now,
    )

    /** Runs one evaluation and returns the actions. */
    private fun evaluate(
        previous: QuotaSnapshot?,
        current: QuotaSnapshot,
        state: AlertState = AlertState(),
        rules: AlertRules = this.rules,
        fresh: Boolean = true,
        at: Instant = now,
    ): AlertEvaluation = ThresholdEvaluator.evaluate(previous, current, rules, state, at, fresh)

    private fun AlertEvaluation.shortWindowActions() =
        actions.filter { it.window == QuotaWindowKind.ShortWindow }

    @Test
    fun crossingIntoWarningTriggersWarning() {
        val result = evaluate(snap(25.0), snap(19.0))

        assertEquals(1, result.shortWindowActions().size)
        assertEquals(AlertSeverity.Warning, result.shortWindowActions().single().severity)
        assertEquals(WindowTriggerState.WarningTriggered, result.state.shortWindow.state)
    }

    @Test
    fun stayingInWarningDoesNotRepeat() {
        val first = evaluate(snap(25.0), snap(19.0))
        val second = evaluate(snap(19.0), snap(18.0), state = first.state)

        assertTrue(second.actions.isEmpty(), "the warning must fire once, not once per snapshot")
    }

    @Test
    fun crossingFromWarningIntoCriticalTriggersCritical() {
        val warned = evaluate(snap(25.0), snap(19.0))
        val critical = evaluate(snap(19.0), snap(9.0), state = warned.state)

        assertEquals(1, critical.shortWindowActions().size)
        assertEquals(AlertSeverity.Critical, critical.shortWindowActions().single().severity)
        assertEquals(WindowTriggerState.CriticalTriggered, critical.state.shortWindow.state)
    }

    @Test
    fun aJumpPastBothThresholdsEmitsOnlyCritical() {
        // The case the design calls out explicitly: one update, one notification.
        val result = evaluate(snap(25.0), snap(9.0))

        assertEquals(1, result.actions.size)
        assertEquals(AlertSeverity.Critical, result.actions.single().severity)
        assertEquals(false, result.actions.single().isRepeat)

        // The internal state still records that critical was reached, so warning will not fire later.
        assertEquals(WindowTriggerState.CriticalTriggered, result.state.shortWindow.state)
    }

    @Test
    fun aWindowResetClearsTheStateAndRearms() {
        val critical = evaluate(snap(25.0), snap(9.0))
        assertEquals(WindowTriggerState.CriticalTriggered, critical.state.shortWindow.state)

        // A new window: the reset time has moved forward and the value is back at 100%.
        val nextWindow = evaluate(
            previous = snap(9.0),
            current = snap(100.0, shortResetsAt = shortReset.plus(Duration.ofHours(5))),
            state = critical.state,
        )

        assertTrue(nextWindow.actions.isEmpty(), "a reset is not an alert")
        assertEquals(WindowTriggerState.Normal, nextWindow.state.shortWindow.state)
    }

    @Test
    fun criticalRearmsBeforeWarningDoes() {
        val critical = evaluate(snap(25.0), snap(9.0))

        // 12% is above critical (10) but still at or below warning (20).
        val recovered = evaluate(snap(9.0), snap(12.0), state = critical.state)

        assertTrue(recovered.actions.isEmpty())
        assertEquals(
            WindowTriggerState.WarningTriggered,
            recovered.state.shortWindow.state,
            "critical must re-arm, but warning must remain triggered because 12% is still at or below 20%",
        )
    }

    @Test
    fun warningRearmsAboveTheWarningThreshold() {
        val warned = evaluate(snap(25.0), snap(19.0))

        val recovered = evaluate(snap(19.0), snap(21.0), state = warned.state)

        assertTrue(recovered.actions.isEmpty())
        assertEquals(WindowTriggerState.Normal, recovered.state.shortWindow.state)
    }

    @Test
    fun theFirstEverSnapshotBelowWarningTriggersWarning() {
        val result = evaluate(previous = null, current = snap(18.0))

        assertEquals(1, result.shortWindowActions().size)
        assertEquals(AlertSeverity.Warning, result.shortWindowActions().single().severity)
    }

    @Test
    fun theFirstEverSnapshotBelowCriticalTriggersOnlyCritical() {
        val result = evaluate(previous = null, current = snap(8.0))

        assertEquals(1, result.shortWindowActions().size)
        assertEquals(AlertSeverity.Critical, result.shortWindowActions().single().severity)
    }

    @Test
    fun aStaleSnapshotRaisesNothing() {
        val result = evaluate(snap(25.0), snap(8.0), fresh = false)

        assertTrue(result.actions.isEmpty(), "stale data cannot support a claim about the present")
    }

    @Test
    fun aStaleSnapshotDoesNotEvenRearm() {
        val critical = evaluate(snap(25.0), snap(9.0))

        val stale = evaluate(snap(9.0), snap(100.0), state = critical.state, fresh = false)

        assertEquals(
            WindowTriggerState.CriticalTriggered,
            stale.state.shortWindow.state,
            "a stale snapshot must not be read as a recovery either",
        )
    }

    @Test
    fun criticalRepeatsOnlyAfterTheInterval() {
        val repeatRules = AlertRules(repeatCritical = RepeatRule(enabled = true, interval = Duration.ofMinutes(60)))
        val first = evaluate(snap(25.0), snap(8.0), rules = repeatRules)
        assertEquals(false, first.actions.single().isRepeat)

        val tooSoon = evaluate(
            previous = snap(8.0),
            current = snap(8.0),
            state = first.state,
            rules = repeatRules,
            at = now.plus(Duration.ofMinutes(59)),
        )
        assertTrue(tooSoon.actions.isEmpty(), "the interval has not elapsed")

        val due = evaluate(
            previous = snap(8.0),
            current = snap(8.0),
            state = first.state,
            rules = repeatRules,
            at = now.plus(Duration.ofMinutes(60)),
        )

        assertEquals(1, due.actions.size)
        assertTrue(due.actions.single().isRepeat)
    }

    @Test
    fun criticalNeverRepeatsWhenTheRuleIsOff() {
        val first = evaluate(snap(25.0), snap(8.0))

        val later = evaluate(
            previous = snap(8.0),
            current = snap(8.0),
            state = first.state,
            at = now.plus(Duration.ofHours(5)),
        )

        assertTrue(later.actions.isEmpty(), "repeat is off by default and must stay off")
    }

    @Test
    fun aStaleSnapshotNeverRepeatsEvenWhenDue() {
        val repeatRules = AlertRules(repeatCritical = RepeatRule(enabled = true, interval = Duration.ofMinutes(60)))
        val first = evaluate(snap(25.0), snap(8.0), rules = repeatRules)

        val stale = evaluate(
            previous = snap(8.0),
            current = snap(8.0),
            state = first.state,
            rules = repeatRules,
            fresh = false,
            at = now.plus(Duration.ofHours(2)),
        )

        assertTrue(stale.actions.isEmpty())
    }

    @Test
    fun theWeeklyRulesAreIndependentOfTheShortWindowRules() {
        // The short window is comfortable, the weekly window is nearly exhausted. Only the weekly
        // alert may fire, and only at the weekly thresholds.
        val result = evaluate(
            previous = null,
            current = snap(short = 90.0, weekly = 8.0),
        )

        assertEquals(1, result.actions.size)
        assertEquals(QuotaWindowKind.Weekly, result.actions.single().window)
        assertEquals(AlertSeverity.Critical, result.actions.single().severity)
        assertEquals(WindowTriggerState.Normal, result.state.shortWindow.state)
    }

    @Test
    fun bothWindowsCanAlertFromOneSnapshot() {
        val result = evaluate(previous = null, current = snap(short = 8.0, weekly = 8.0))

        assertEquals(2, result.actions.size)
        assertEquals(
            setOf(QuotaWindowKind.ShortWindow, QuotaWindowKind.Weekly),
            result.actions.map { it.window }.toSet(),
        )
    }

    @Test
    fun aCustomThresholdIsRespected() {
        val strict = AlertRules(
            shortWindow = WindowRules(warningPercent = 50.0, criticalPercent = 25.0),
        )

        val result = evaluate(previous = snap(80.0), current = snap(40.0), rules = strict)

        assertEquals(AlertSeverity.Warning, result.shortWindowActions().single().severity)
    }

    @Test
    fun theEvaluatorIsPureAndCanBeReplayed() {
        // Evaluating the same inputs twice produces the same answer, which is what makes the matrix
        // above meaningful rather than incidental.
        val first = evaluate(snap(25.0), snap(9.0))
        val second = evaluate(snap(25.0), snap(9.0))

        assertEquals(first.actions, second.actions)
        assertEquals(first.state, second.state)
    }

    @Test
    fun aResetIsDetectedEvenWhenNoResetTimeWasStoredYet() {
        // The case where the app has never stored a reset time, so the previous snapshot is the only
        // evidence that a new window has begun.
        val critical = evaluate(snap(25.0), snap(9.0))
        val stateWithoutResets = AlertState(shortWindow = critical.state.shortWindow)

        val reset = evaluate(
            previous = snap(9.0),
            current = snap(100.0, shortResetsAt = shortReset.plus(Duration.ofHours(5))),
            state = stateWithoutResets,
        )

        assertTrue(reset.actions.isEmpty())
        assertEquals(WindowTriggerState.Normal, reset.state.shortWindow.state)
    }
}
