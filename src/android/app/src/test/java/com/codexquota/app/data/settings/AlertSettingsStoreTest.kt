package com.codexquota.app.data.settings

import com.codexquota.app.domain.alerts.WindowRules
import java.time.Duration
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertTrue
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.test.runTest

/** Threshold configuration and its validation. */
class AlertSettingsStoreTest {

    private fun store() = InMemoryAlertSettings()

    @Test
    fun theApprovedDefaultsAreUsed() = runTest {
        val preferences = store().preferences.first()

        assertEquals(false, preferences.statusNotificationEnabled)
        assertEquals(false, preferences.alertsEnabled)
        assertEquals(false, preferences.repeatCriticalEnabled)
        assertEquals(Duration.ofMinutes(60), preferences.repeatInterval)
        assertEquals(20.0, preferences.shortWindow.warningPercent)
        assertEquals(10.0, preferences.shortWindow.criticalPercent)
        assertEquals(20.0, preferences.weekly.warningPercent)
        assertEquals(10.0, preferences.weekly.criticalPercent)
        assertEquals(WatchFormat.Compact, preferences.watchFormat)
    }

    @Test
    fun aTwentyTenPairIsAccepted() = runTest {
        val store = store()

        store.update { it.copy(shortWindow = WindowRules(20.0, 10.0)) }

        assertEquals(20.0, store.preferences.first().shortWindow.warningPercent)
    }

    @Test
    fun aCriticalThresholdAtOrAboveWarningCannotEvenBeConstructed() {
        // The rule is enforced where the values live, which is stronger than validating on save: an
        // impossible pair cannot exist long enough to be stored.
        assertFailsWith<IllegalArgumentException> {
            WindowRules(warningPercent = 20.0, criticalPercent = 20.0)
        }

        assertFailsWith<IllegalArgumentException> {
            WindowRules(warningPercent = 20.0, criticalPercent = 25.0)
        }
    }

    @Test
    fun zeroAndOneHundredCannotEvenBeConstructed() {
        assertFailsWith<IllegalArgumentException> { WindowRules(warningPercent = 0.0, criticalPercent = 0.0) }
        assertFailsWith<IllegalArgumentException> { WindowRules(warningPercent = 100.0, criticalPercent = 10.0) }
    }

    @Test
    fun theStoreRefusesARawPairThatWasNotBuiltThroughWindowRules() {
        // The store also validates, because its input can come from a file rather than from a
        // constructor: a preference written by an older or corrupt build still has to be refused.
        assertFailsWith<InvalidThresholdsException> { requireValidThresholds(20.0, 20.0) }
        assertFailsWith<InvalidThresholdsException> { requireValidThresholds(0.0, 0.0) }
        assertFailsWith<InvalidThresholdsException> { requireValidThresholds(100.0, 10.0) }
    }

    @Test
    fun aRejectedUpdateLeavesTheStoredValuesAlone() = runTest {
        val store = store()

        runCatching {
            store.update { it.copy(weekly = WindowRules(warningPercent = 10.0, criticalPercent = 10.0)) }
        }

        val preferences = store.preferences.first()

        assertEquals(20.0, preferences.weekly.warningPercent)
        assertEquals(10.0, preferences.weekly.criticalPercent)
    }

    @Test
    fun theWeeklyThresholdsAreIndependentOfTheShortWindowOnes() = runTest {
        val store = store()

        store.update { it.copy(weekly = WindowRules(warningPercent = 40.0, criticalPercent = 30.0)) }

        val preferences = store.preferences.first()

        assertEquals(40.0, preferences.weekly.warningPercent)
        assertEquals(20.0, preferences.shortWindow.warningPercent, "the short window must be untouched")
    }

    @Test
    fun theRulesTheEvaluatorSeesComeFromThePreferences() = runTest {
        val store = InMemoryAlertSettings(
            NotificationPreferences(
                repeatCriticalEnabled = true,
                repeatInterval = Duration.ofMinutes(30),
                shortWindow = WindowRules(15.0, 5.0),
                weekly = WindowRules(25.0, 12.0),
            ),
        )

        val rules = store.preferences.first().rules

        assertTrue(rules.repeatCritical.enabled)
        assertEquals(Duration.ofMinutes(30), rules.repeatCritical.interval)
        assertEquals(15.0, rules.shortWindow.warningPercent)
        assertEquals(12.0, rules.weekly.criticalPercent)
    }

    @Test
    fun theRepeatIntervalsOfferedAreTheApprovedOnes() {
        assertEquals(
            listOf(Duration.ofMinutes(30), Duration.ofMinutes(60), Duration.ofMinutes(120)),
            NotificationPreferences.OFFERED_REPEAT_INTERVALS,
        )
    }

    @Test
    fun aWindowRulesPairCannotBeConstructedImpossible() {
        // The rule lives with the values, so no caller can build an unusable pair by accident.
        assertFailsWith<IllegalArgumentException> {
            WindowRules(warningPercent = 10.0, criticalPercent = 20.0)
        }

        assertFalse(WindowRules(20.0, 10.0).criticalPercent >= WindowRules(20.0, 10.0).warningPercent)
    }
}
