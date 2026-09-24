package com.codexquota.app.ui

import androidx.compose.foundation.layout.padding
import androidx.compose.material3.NavigationBar
import androidx.compose.material3.NavigationBarItem
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.navigation.NavDestination.Companion.hierarchy
import androidx.navigation.NavGraph.Companion.findStartDestination
import androidx.navigation.compose.NavHost
import androidx.navigation.compose.composable
import androidx.navigation.compose.currentBackStackEntryAsState
import androidx.navigation.compose.rememberNavController
import com.codexquota.app.R
import com.codexquota.app.ui.connection.ConnectionScreen
import com.codexquota.app.ui.connection.ConnectionViewModel
import com.codexquota.app.ui.dashboard.DashboardScreen
import com.codexquota.app.ui.dashboard.DashboardViewModel
import com.codexquota.app.ui.settings.SettingsScreen
import com.codexquota.app.ui.settings.SettingsViewModel

/** The three bottom destinations. */
enum class Destination(val route: String, val labelRes: Int) {
    Dashboard("dashboard", R.string.nav_dashboard),
    History("history", R.string.nav_history),
    Settings("settings", R.string.nav_settings),
}

/**
 * The navigation host.
 *
 * It holds no state of its own: each screen observes a ViewModel, and the ViewModels own the
 * connection and the cache. Recreating the activity therefore changes nothing about what the app is
 * doing.
 */
@Composable
fun AppNav(
    dashboard: DashboardViewModel,
    connection: ConnectionViewModel,
    settings: SettingsViewModel,
) {
    val navController = rememberNavController()
    val backStackEntry by navController.currentBackStackEntryAsState()
    val currentDestination = backStackEntry?.destination

    Scaffold(
        bottomBar = {
            NavigationBar {
                Destination.entries.forEach { destination ->
                    NavigationBarItem(
                        selected = currentDestination?.hierarchy?.any { it.route == destination.route } == true,
                        onClick = {
                            navController.navigate(destination.route) {
                                popUpTo(navController.graph.findStartDestination().id) { saveState = true }
                                launchSingleTop = true
                                restoreState = true
                            }
                        },
                        icon = {},
                        label = { Text(stringResource(destination.labelRes)) },
                    )
                }
            }
        },
    ) { padding ->
        NavHost(
            navController = navController,
            startDestination = Destination.Dashboard.route,
            modifier = Modifier.padding(padding),
        ) {
            composable(Destination.Dashboard.route) {
                DashboardScreen(dashboard)
            }

            composable(Destination.History.route) {
                // Replaced by the 24-hour chart and event list in the History task.
                PlaceholderScreen(R.string.history_available_after_sync)
            }

            composable(Destination.Settings.route) {
                SettingsScreen(settings = settings, connection = connection)
            }
        }
    }
}

/** A screen whose content arrives in a later task. */
@Composable
private fun PlaceholderScreen(textRes: Int) {
    Text(text = stringResource(textRes))
}
