package com.codexquota.app.data.settings

import androidx.datastore.core.DataStore
import androidx.datastore.preferences.core.Preferences
import androidx.datastore.preferences.core.booleanPreferencesKey
import androidx.datastore.preferences.core.doublePreferencesKey
import androidx.datastore.preferences.core.edit
import androidx.datastore.preferences.core.longPreferencesKey
import androidx.datastore.preferences.core.stringPreferencesKey
import com.codexquota.app.domain.alerts.WindowRules
import java.time.Duration
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.map

/**
 * The phone-local notification settings.
 *
 * It is an interface because the settings are ordinary values with rules attached, and the rules are
 * worth testing without a DataStore file behind them.
 */
interface AlertSettings {
    /** The current preferences, observed. */
    val preferences: Flow<NotificationPreferences>

    /**
     * Applies a change.
     *
     * @throws InvalidThresholdsException when the change would produce an impossible threshold pair.
     */
    suspend fun update(transform: (NotificationPreferences) -> NotificationPreferences): NotificationPreferences
}

/**
 * The DataStore-backed settings.
 *
 * Thresholds are validated on the way in, so an unusable pair can never reach the evaluator through
 * this path.
 */
class AlertSettingsStore(
    private val dataStore: DataStore<Preferences>,
) : AlertSettings {

    override val preferences: Flow<NotificationPreferences> = dataStore.data.map { it.toNotificationPreferences() }

    override suspend fun update(
        transform: (NotificationPreferences) -> NotificationPreferences,
    ): NotificationPreferences {
        var updated = NotificationPreferences.Default

        dataStore.edit { raw ->
            val next = transform(raw.toNotificationPreferences())

            // Rejected before anything is written, so a bad value cannot leave the store half-updated.
            requireValidThresholds(next.shortWindow.warningPercent, next.shortWindow.criticalPercent)
            requireValidThresholds(next.weekly.warningPercent, next.weekly.criticalPercent)

            raw[STATUS_ENABLED] = next.statusNotificationEnabled
            raw[ALERTS_ENABLED] = next.alertsEnabled
            raw[REPEAT_ENABLED] = next.repeatCriticalEnabled
            raw[REPEAT_INTERVAL_MINUTES] = next.repeatInterval.toMinutes()
            raw[SHORT_WARNING] = next.shortWindow.warningPercent
            raw[SHORT_CRITICAL] = next.shortWindow.criticalPercent
            raw[WEEKLY_WARNING] = next.weekly.warningPercent
            raw[WEEKLY_CRITICAL] = next.weekly.criticalPercent
            raw[WATCH_FORMAT] = next.watchFormat.name

            updated = next
        }

        return updated
    }

    private fun Preferences.toNotificationPreferences() = NotificationPreferences(
        statusNotificationEnabled = this[STATUS_ENABLED] ?: false,
        alertsEnabled = this[ALERTS_ENABLED] ?: false,
        repeatCriticalEnabled = this[REPEAT_ENABLED] ?: false,
        repeatInterval = Duration.ofMinutes(this[REPEAT_INTERVAL_MINUTES] ?: DEFAULT_REPEAT_MINUTES),
        shortWindow = WindowRules(
            warningPercent = this[SHORT_WARNING] ?: WindowRules.DEFAULT_WARNING,
            criticalPercent = this[SHORT_CRITICAL] ?: WindowRules.DEFAULT_CRITICAL,
        ),
        weekly = WindowRules(
            warningPercent = this[WEEKLY_WARNING] ?: WindowRules.DEFAULT_WARNING,
            criticalPercent = this[WEEKLY_CRITICAL] ?: WindowRules.DEFAULT_CRITICAL,
        ),
        watchFormat = this[WATCH_FORMAT]
            ?.let { name -> WatchFormat.entries.firstOrNull { it.name == name } }
            ?: WatchFormat.Compact,
    )

    private companion object {
        const val DEFAULT_REPEAT_MINUTES = 60L

        val STATUS_ENABLED = booleanPreferencesKey("status_notification_enabled")
        val ALERTS_ENABLED = booleanPreferencesKey("alerts_enabled")
        val REPEAT_ENABLED = booleanPreferencesKey("repeat_critical_enabled")
        val REPEAT_INTERVAL_MINUTES = longPreferencesKey("repeat_interval_minutes")
        val SHORT_WARNING = doublePreferencesKey("short_warning_percent")
        val SHORT_CRITICAL = doublePreferencesKey("short_critical_percent")
        val WEEKLY_WARNING = doublePreferencesKey("weekly_warning_percent")
        val WEEKLY_CRITICAL = doublePreferencesKey("weekly_critical_percent")
        val WATCH_FORMAT = stringPreferencesKey("watch_format")
    }
}

/** An in-memory [AlertSettings], for tests and for a process that has no DataStore yet. */
class InMemoryAlertSettings(
    initial: NotificationPreferences = NotificationPreferences.Default,
) : AlertSettings {
    private val state = MutableStateFlow(initial)

    override val preferences: Flow<NotificationPreferences> = state.asStateFlow()

    override suspend fun update(
        transform: (NotificationPreferences) -> NotificationPreferences,
    ): NotificationPreferences {
        val next = transform(state.value)

        requireValidThresholds(next.shortWindow.warningPercent, next.shortWindow.criticalPercent)
        requireValidThresholds(next.weekly.warningPercent, next.weekly.criticalPercent)

        state.value = next

        return next
    }
}

/** Reads the preferences once, for a caller that is not collecting them. */
suspend fun AlertSettings.current(): NotificationPreferences = preferences.first()
