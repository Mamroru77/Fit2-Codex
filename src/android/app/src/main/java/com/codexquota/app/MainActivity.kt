package com.codexquota.app

import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.remember
import androidx.lifecycle.lifecycleScope
import com.codexquota.app.ui.AppNav
import com.codexquota.app.ui.connection.ConnectionViewModel
import com.codexquota.app.domain.ConnectionState
import com.codexquota.app.ui.dashboard.DashboardViewModel
import com.codexquota.app.ui.settings.SettingsViewModel
import kotlinx.coroutines.launch

/**
 * The single activity.
 *
 * It hosts Compose and owns the process-wide [AppContainer]. The connection state machine and every
 * repository live outside it, so the activity being recreated never changes what the app is doing.
 */
class MainActivity : ComponentActivity() {

    /**
     * The process-wide composition root, taken from the application.
     *
     * It must not be built here. An `AppContainer` of its own would be a second pairing store, a
     * second Room database, a second connection manager and — as the instrumentation gate found — a
     * second DataStore for the same file, which throws
     * "There are multiple DataStores active for the same file" the moment both exist. The whole point
     * of the container is that there is exactly one per process.
     */
    private val container: AppContainer
        get() = (application as CodexQuotaApplication).container

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        // The connection loop runs for as long as the activity does, not for as long as one screen is
        // visible: rotating the phone must not drop the live connection.
        lifecycleScope.launch {
            container.connection.run()
        }

        setContent {
            MaterialTheme {
                Surface {
                    val dashboard = remember {
                        DashboardViewModel(
                            repository = container.repository,
                            connection = container.connection,
                            scope = lifecycleScope,
                        )
                    }

                    val connection = remember {
                        ConnectionViewModel(
                            store = container.pairedBridgeStore,
                            discovery = container.discovery,
                            probe = container.identityProbe,
                            pairing = container.pairing,
                            lanPermission = container.localNetworkPermission,
                            displayName = container.displayName,
                            scope = lifecycleScope,
                        )
                    }

                    val settings = remember {
                        SettingsViewModel(
                            settings = container.settings,
                            health = container.notificationHealth,
                            scheduler = container.syncScheduler,
                            modeController = container.syncModeController,
                            lanPermission = container.localNetworkPermission,
                            bridgeReachable = {
                                container.connection.state.value is ConnectionState.Connected
                            },
                            sendTestNotification = { format -> container.notifier.sendTestNotification(format) },
                            scope = lifecycleScope,
                        )
                    }

                    // The mode controller is what turns the two switches into something running.
                    LaunchedEffect(Unit) {
                        container.syncModeController.start()
                    }

                    AppNav(dashboard = dashboard, connection = connection, settings = settings)
                }
            }
        }
    }
}
