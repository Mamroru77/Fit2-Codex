package com.codexquota.app.ui.settings

import com.codexquota.app.data.settings.AlertSettings
import com.codexquota.app.data.settings.NotificationPreferences
import com.codexquota.app.data.settings.WatchFormat
import com.codexquota.app.notifications.NotificationChannels
import com.codexquota.app.notifications.NotificationHealth
import com.codexquota.app.notifications.NotificationHealthChecker
import com.codexquota.app.sync.LocalNetworkPermissionState
import com.codexquota.app.sync.SyncMode
import com.codexquota.app.sync.SyncModeController
import com.codexquota.app.sync.SyncScheduler
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.launchIn
import kotlinx.coroutines.flow.onEach
import kotlinx.coroutines.launch

/**
 * Everything the settings screen draws.
 *
 * The two delivery flags are separate from the two switches on purpose: "the user asked for alerts"
 * and "the system will deliver them" are different facts, and conflating them is exactly the mistake
 * the spec forbids.
 */
data class SettingsUiState(
    val preferences: NotificationPreferences,
    val notificationHealth: NotificationHealth,
    /** Whether an enabled status notification can actually be delivered. */
    val statusDeliveryHealthy: Boolean,
    /** Whether enabled alerts can actually be delivered. */
    val alertsDeliveryHealthy: Boolean,
    val mode: SyncMode,
    /** Whether the foreground service is running right now. */
    val liveRunning: Boolean,
    /**
     * Whether the user has live sync enabled but it is not running.
     *
     * This is the state a reboot leaves the phone in, and the reason the screen offers an explicit
     * resume rather than starting the service behind the user's back.
     */
    val liveResumeRequired: Boolean,
    val lanPermission: LocalNetworkPermissionState,
    /** Whether the Bridge answered the last attempt. */
    val bridgeReachable: Boolean,
    val testNotificationSent: Boolean,
)

/**
 * The settings screen's state.
 *
 * It owns no service and no worker: it changes preferences, and the mode controller reacts. That is
 * what keeps the switch and the running mode from being two things that can disagree.
 */
class SettingsViewModel(
    private val settings: AlertSettings,
    private val health: NotificationHealthChecker,
    private val scheduler: SyncScheduler,
    private val modeController: SyncModeController,
    private val lanPermission: StateFlow<LocalNetworkPermissionState>,
    private val bridgeReachable: () -> Boolean,
    private val sendTestNotification: suspend (WatchFormat) -> Unit,
    private val scope: CoroutineScope,
) {
    private val _state = MutableStateFlow(initialState())

    /** The settings screen state. */
    val state: StateFlow<SettingsUiState> = _state.asStateFlow()

    init {
        settings.preferences
            .onEach { preferences ->
                _state.value = _state.value.copy(
                    preferences = preferences,
                    mode = SyncMode.forPreferences(preferences),
                    liveResumeRequired = preferences.statusNotificationEnabled && !scheduler.isLiveRunning(),
                    liveRunning = scheduler.isLiveRunning(),
                    bridgeReachable = bridgeReachable(),
                )
            }
            .launchIn(scope)

        lanPermission
            .onEach { permission -> _state.value = _state.value.copy(lanPermission = permission) }
            .launchIn(scope)
    }

    /** Re-reads the platform's notification state and the service's running state. */
    fun refresh() {
        val health = health.read()

        _state.value = _state.value.copy(
            notificationHealth = health,
            statusDeliveryHealthy = health.canDeliver(NotificationChannels.STATUS),
            alertsDeliveryHealthy = health.canDeliver(NotificationChannels.ALERTS),
            liveRunning = scheduler.isLiveRunning(),
            liveResumeRequired =
                _state.value.preferences.statusNotificationEnabled && !scheduler.isLiveRunning(),
            bridgeReachable = bridgeReachable(),
        )
    }

    /** Turns the persistent status notification on or off. */
    fun setStatusNotificationEnabled(enabled: Boolean) {
        scope.launch {
            settings.update { it.copy(statusNotificationEnabled = enabled) }
            refresh()
        }
    }

    /** Turns low-quota alerts on or off. */
    fun setAlertsEnabled(enabled: Boolean) {
        scope.launch {
            settings.update { it.copy(alertsEnabled = enabled) }
            refresh()
        }
    }

    /** Turns the repeated critical reminder on or off. */
    fun setRepeatCriticalEnabled(enabled: Boolean) {
        scope.launch {
            settings.update { it.copy(repeatCriticalEnabled = enabled) }
        }
    }

    /** Changes the watch-facing text format. */
    fun setWatchFormat(format: WatchFormat) {
        scope.launch {
            settings.update { it.copy(watchFormat = format) }
        }
    }

    /**
     * Starts live sync because the user asked for it.
     *
     * The permission is requested first when the platform needs it, so the service does not start
     * into a state where it cannot reach the Bridge.
     */
    fun resumeLive() {
        scope.launch {
            modeController.resumeLive()
            refresh()
        }
    }

    /** Sends a test notification, so the end-to-end path to the watch can be checked. */
    fun sendTest() {
        scope.launch {
            sendTestNotification(_state.value.preferences.watchFormat)

            _state.value = _state.value.copy(testNotificationSent = true)
            refresh()
        }
    }

    /** Clears the "test sent" acknowledgement. */
    fun acknowledgeTest() {
        _state.value = _state.value.copy(testNotificationSent = false)
    }

    private fun initialState(): SettingsUiState {
        val health = this.health.read()
        val preferences = NotificationPreferences.Default

        return SettingsUiState(
            preferences = preferences,
            notificationHealth = health,
            statusDeliveryHealthy = health.canDeliver(NotificationChannels.STATUS),
            alertsDeliveryHealthy = health.canDeliver(NotificationChannels.ALERTS),
            mode = SyncMode.Off,
            liveRunning = scheduler.isLiveRunning(),
            liveResumeRequired = false,
            lanPermission = lanPermission.value,
            bridgeReachable = bridgeReachable(),
            testNotificationSent = false,
        )
    }
}
