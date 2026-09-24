package com.codexquota.app.ui.settings

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.FilterChip
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.codexquota.app.R
import com.codexquota.app.data.settings.WatchFormat
import com.codexquota.app.sync.LocalNetworkPermissionState
import com.codexquota.app.ui.connection.ConnectionScreen
import com.codexquota.app.ui.connection.ConnectionViewModel

/**
 * Settings.
 *
 * The spec's sections are Alerts, Bridge connection, Watch display, Data/cache, Logs and About. The
 * connection section is the existing pairing screen, because it is the same thing described in the
 * same terms.
 *
 * Every switch here states what will actually happen, and the delivery rows state whether the system
 * will deliver it — which is a different fact from whether the user asked for it.
 */
@Composable
fun SettingsScreen(
    settings: SettingsViewModel,
    connection: ConnectionViewModel,
) {
    val state by settings.state.collectAsStateWithLifecycle()

    Column(
        modifier = Modifier
            .fillMaxWidth()
            .verticalScroll(rememberScrollState())
            .padding(16.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        Text(text = stringResource(R.string.settings_title), style = MaterialTheme.typography.titleLarge)

        SwitchRow(
            title = stringResource(R.string.settings_status_notification),
            checked = state.preferences.statusNotificationEnabled,
            onCheckedChange = settings::setStatusNotificationEnabled,
        )

        SwitchRow(
            title = stringResource(R.string.settings_alerts),
            checked = state.preferences.alertsEnabled,
            onCheckedChange = settings::setAlertsEnabled,
        )

        // The repeat reminder only applies to a critical state that is still critical.
        SwitchRow(
            title = stringResource(R.string.settings_repeat_critical),
            checked = state.preferences.repeatCriticalEnabled,
            enabled = state.preferences.alertsEnabled,
            onCheckedChange = settings::setRepeatCriticalEnabled,
        )

        Text(
            text = stringResource(R.string.settings_mode, modeDescription(state)),
            style = MaterialTheme.typography.bodyMedium,
        )

        if (state.preferences.statusNotificationEnabled && !state.statusDeliveryHealthy) {
            Text(
                text = stringResource(R.string.settings_delivery_blocked),
                style = MaterialTheme.typography.bodySmall,
            )
        }

        if (state.preferences.alertsEnabled && !state.alertsDeliveryHealthy) {
            Text(
                text = stringResource(R.string.settings_delivery_blocked),
                style = MaterialTheme.typography.bodySmall,
            )
        }

        // The state a reboot leaves the phone in: the setting survived, the service did not.
        if (state.liveResumeRequired) {
            Card(modifier = Modifier.fillMaxWidth()) {
                Column(
                    modifier = Modifier
                        .fillMaxWidth()
                        .padding(12.dp),
                    verticalArrangement = Arrangement.spacedBy(8.dp),
                ) {
                    Text(text = stringResource(R.string.settings_live_resume_required))
                    Button(onClick = settings::resumeLive) {
                        Text(stringResource(R.string.settings_live_resume))
                    }
                }
            }
        }

        if (state.lanPermission == LocalNetworkPermissionState.Required) {
            Text(
                text = stringResource(R.string.connection_lan_permission_required),
                style = MaterialTheme.typography.bodySmall,
            )
        }

        Text(text = stringResource(R.string.settings_watch_display), style = MaterialTheme.typography.titleMedium)

        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            FilterChip(
                selected = state.preferences.watchFormat == WatchFormat.Compact,
                onClick = { settings.setWatchFormat(WatchFormat.Compact) },
                label = { Text(stringResource(R.string.settings_watch_compact)) },
            )

            FilterChip(
                selected = state.preferences.watchFormat == WatchFormat.Chinese,
                onClick = { settings.setWatchFormat(WatchFormat.Chinese) },
                label = { Text(stringResource(R.string.settings_watch_chinese)) },
            )
        }

        Button(onClick = settings::sendTest) {
            Text(stringResource(R.string.settings_send_test_notification))
        }

        if (state.testNotificationSent) {
            Text(
                text = stringResource(R.string.settings_test_notification_sent),
                style = MaterialTheme.typography.bodySmall,
            )
        }

        Text(
            text = stringResource(R.string.settings_connection_section),
            style = MaterialTheme.typography.titleMedium,
        )

        ConnectionScreen(connection)
    }
}

@Composable
private fun SwitchRow(
    title: String,
    checked: Boolean,
    enabled: Boolean = true,
    onCheckedChange: (Boolean) -> Unit,
) {
    Row(
        modifier = Modifier.fillMaxWidth(),
        horizontalArrangement = Arrangement.SpaceBetween,
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Text(text = title, modifier = Modifier.weight(1f))
        Switch(checked = checked, onCheckedChange = onCheckedChange, enabled = enabled)
    }
}

/** A sentence describing what the current switches will actually do. */
@Composable
private fun modeDescription(state: SettingsUiState): String = when (state.mode) {
    com.codexquota.app.sync.SyncMode.Off -> stringResource(R.string.settings_mode_off)
    com.codexquota.app.sync.SyncMode.Background -> stringResource(R.string.settings_mode_background)
    com.codexquota.app.sync.SyncMode.Live -> stringResource(R.string.settings_mode_live)
}
