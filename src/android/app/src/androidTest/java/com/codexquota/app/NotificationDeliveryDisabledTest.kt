package com.codexquota.app

import android.app.NotificationManager
import android.content.Context
import androidx.core.app.NotificationManagerCompat
import androidx.test.core.app.ApplicationProvider
import androidx.test.ext.junit.runners.AndroidJUnit4
import com.codexquota.app.data.settings.InMemoryAlertSettings
import com.codexquota.app.data.settings.NotificationPreferences
import com.codexquota.app.data.settings.WatchFormat
import com.codexquota.app.domain.QuotaSnapshot
import com.codexquota.app.domain.QuotaSourceStatus
import com.codexquota.app.domain.QuotaWindow
import com.codexquota.app.domain.alerts.AlertAction
import com.codexquota.app.domain.alerts.AlertSeverity
import com.codexquota.app.domain.alerts.QuotaWindowKind
import com.codexquota.app.notifications.AndroidNotificationHealthChecker
import com.codexquota.app.notifications.NotificationChannels
import com.codexquota.app.notifications.QuotaNotificationManager
import com.codexquota.app.sync.LocalNetworkPermissionState
import com.codexquota.app.sync.SyncModeController
import com.codexquota.app.sync.SyncScheduler
import com.codexquota.app.ui.settings.SettingsUiState
import com.codexquota.app.ui.settings.SettingsViewModel
import java.time.Instant
import kotlin.test.AfterTest
import kotlin.test.BeforeTest
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.runBlocking
import org.junit.runner.RunWith

/**
 * What the app must say and do when the system will not deliver its notifications.
 *
 * ## Why this is a separate class, run separately
 *
 * The state these tests need — delivery disabled — can only be produced by revoking the runtime
 * permission, and **Android kills a process whose runtime permission is revoked**. Instrumentation
 * runs inside the app's own process, so doing that here would kill the runner: the CI log shows
 * `ActivityManager: Killing com.codexquota.app/u0a216 (adj 0): permissions revoked`, after which
 * only three results were ever written and the Stage C and Stage D classes never ran at all.
 *
 * Changing only the `POST_NOTIFICATION` app-op was tried first, on the theory that it is what
 * `areNotificationsEnabled()` reads and that it does not restart anything. On the API 36 emulator it
 * did not take effect — `areNotificationsEnabled()` stayed true — so the app-op route is not a
 * reliable way to reach this state either.
 *
 * So the state is produced **outside**, between instrumentation processes, exactly as the plan's
 * fallback describes: the workflow revokes the permission with `adb`, runs only this class with
 * `am instrument`, and grants it back afterwards. `connectedDebugAndroidTest` excludes this class by
 * name so the two invocations never overlap.
 *
 * The assertions are not weakened by any of that. Each test first proves the platform really is
 * refusing delivery, and only then asks the app what it thinks.
 */
@RunWith(AndroidJUnit4::class)
class NotificationDeliveryDisabledTest {

    private lateinit var context: Context
    private lateinit var scope: CoroutineScope

    private val manager: NotificationManager
        get() = context.getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager

    @BeforeTest
    fun setUp() {
        context = ApplicationProvider.getApplicationContext()
        scope = CoroutineScope(SupervisorJob() + Dispatchers.Default)

        NotificationChannels.ensureCreated(context)

        // The whole class is meaningless if the platform is still willing to deliver, and a test
        // that silently passed in that state would be worse than one that fails. This is the guard
        // that makes the external setup part of the assertion rather than a hidden precondition.
        assertFalse(
            NotificationManagerCompat.from(context).areNotificationsEnabled(),
            "this class must be run with POST_NOTIFICATIONS revoked; the runner sets that up " +
                "outside the instrumentation process and `connectedDebugAndroidTest` excludes it",
        )
    }

    @AfterTest
    fun tearDown() {
        manager.cancelAll()
        scope.cancel()
    }

    @Test
    fun theAppObservesThatTheSystemWillNotDeliver() {
        val health = AndroidNotificationHealthChecker(context).read()

        assertFalse(health.permissionGranted, "the app must observe that delivery is unavailable")
        assertFalse(health.canDeliver(NotificationChannels.STATUS))
        assertFalse(health.canDeliver(NotificationChannels.ALERTS))
        assertFalse(health.canDeliverAnything)
    }

    @Test
    fun theSettingsScreenDoesNotClaimHealthyDelivery() {
        // The switch is on, but the system will not deliver. Those are different facts, and the
        // screen has to report the second one.
        val viewModel = SettingsViewModel(
            settings = InMemoryAlertSettings(
                NotificationPreferences(statusNotificationEnabled = true, alertsEnabled = true),
            ),
            health = AndroidNotificationHealthChecker(context),
            scheduler = NoopScheduler,
            modeController = SyncModeController(
                InMemoryAlertSettings(NotificationPreferences()),
                NoopScheduler,
                scope,
            ),
            lanPermission = MutableStateFlow(LocalNetworkPermissionState.Granted),
            bridgeReachable = { false },
            sendTestNotification = { },
            scope = scope,
        )

        viewModel.refresh()

        // The stored preferences arrive on a flow, so the first state is the default one. Waiting
        // for them is what a user sees after a frame, and asserting before they arrive tests the
        // placeholder rather than the screen.
        val state = awaitPreferences(viewModel)

        assertTrue(state.preferences.statusNotificationEnabled, "the user's choice is still recorded")
        assertFalse(state.statusDeliveryHealthy, "but delivery is not healthy")
        assertFalse(state.alertsDeliveryHealthy)
    }

    /** Waits until the screen has observed the stored preferences. */
    private fun awaitPreferences(
        viewModel: SettingsViewModel,
        timeoutMillis: Long = 5_000L,
    ): SettingsUiState {
        val deadline = System.currentTimeMillis() + timeoutMillis

        while (System.currentTimeMillis() < deadline) {
            val state = viewModel.state.value

            if (state.preferences.statusNotificationEnabled) {
                return state
            }

            Thread.sleep(50L)
        }

        throw AssertionError("the settings screen never observed the stored preferences")
    }

    @Test
    fun postingWhileTheSystemRefusesDeliveryIsDroppedRatherThanCrashing() = runBlocking {
        val notifications = QuotaNotificationManager(context)

        // Neither of these may throw: the app asks the platform to post and moves on, and a user
        // whose notifications are off must not get a crash instead.
        notifications.showStatus(
            snapshot = snapshot(),
            receivedAt = Instant.parse("2026-09-22T13:30:00Z"),
            stale = false,
            format = WatchFormat.Compact,
        )
        notifications.showAlerts(
            listOf(AlertAction(QuotaWindowKind.Weekly, AlertSeverity.Warning, 18.0)),
            WatchFormat.Compact,
        )
        notifications.sendTestNotification(WatchFormat.Compact)

        // And nothing may actually be delivered, which is the half that a swallowed exception would
        // not prove.
        assertEquals(
            0,
            manager.activeNotifications.size,
            "no notification may be delivered while the system refuses them",
        )
    }

    private fun snapshot(): QuotaSnapshot = QuotaSnapshot(
        schemaVersion = 1,
        generatedAt = Instant.parse("2026-09-22T13:30:00Z"),
        source = "codex_app_server",
        status = QuotaSourceStatus.Online,
        lastSuccessfulSyncAt = Instant.parse("2026-09-22T13:30:00Z"),
        shortWindow = QuotaWindow(
            usedPercent = 28.0,
            remainingPercent = 72.0,
            windowMinutes = 300,
            resetsAt = Instant.parse("2026-09-22T15:42:00Z"),
        ),
        weekly = QuotaWindow(
            usedPercent = 46.0,
            remainingPercent = 54.0,
            windowMinutes = 10080,
            resetsAt = Instant.parse("2026-09-28T00:00:00Z"),
        ),
    )

    /** Nothing in this class starts a service or schedules work. */
    private object NoopScheduler : SyncScheduler {
        override fun startLive() = Unit

        override fun stopLive() = Unit

        override fun enqueuePeriodic() = Unit

        override fun cancelPeriodic() = Unit

        override fun isLiveRunning(): Boolean = false

        override fun isPeriodicEnqueued(): Boolean = false
    }
}
