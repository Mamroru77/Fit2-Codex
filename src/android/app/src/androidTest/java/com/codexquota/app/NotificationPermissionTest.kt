package com.codexquota.app

import android.app.Instrumentation
import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.content.Context
import android.os.ParcelFileDescriptor
import androidx.core.app.NotificationManagerCompat
import androidx.test.core.app.ApplicationProvider
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
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
import com.codexquota.app.notifications.AlertNotificationRenderer
import com.codexquota.app.notifications.NotificationChannels
import com.codexquota.app.notifications.QuotaNotificationManager
import com.codexquota.app.sync.LocalNetworkPermissionState
import com.codexquota.app.sync.SyncScheduler
import com.codexquota.app.sync.SyncModeController
import com.codexquota.app.ui.settings.SettingsViewModel
import java.time.Instant
import kotlin.test.AfterTest
import kotlin.test.BeforeTest
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNotNull
import kotlin.test.assertTrue
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.runBlocking
import org.junit.runner.RunWith

/**
 * The notification permission and channel gate, on a real device.
 *
 * The JVM tests can only assert what the app *does* with a health reading they hand it. What they
 * cannot show is that the reading is real. This test changes the actual OS state — the app-op that
 * backs `POST_NOTIFICATIONS`, and the actual channel importance the platform stores — and then checks
 * that the app's own reading follows it. Nothing here mocks the value back to the app.
 */
@RunWith(AndroidJUnit4::class)
class NotificationPermissionTest {

    private lateinit var instrumentation: Instrumentation
    private lateinit var context: Context
    private lateinit var scope: CoroutineScope

    private val manager: NotificationManager
        get() = context.getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager

    @BeforeTest
    fun setUp() {
        instrumentation = InstrumentationRegistry.getInstrumentation()
        context = ApplicationProvider.getApplicationContext()
        scope = CoroutineScope(SupervisorJob() + Dispatchers.Default)

        NotificationChannels.ensureCreated(context)
    }

    @AfterTest
    fun tearDown() {
        // Restore the real state, so a later test never inherits this one's permission change.
        setNotificationPermission(allowed = true)

        manager.deleteNotificationChannel(NotificationChannels.ALERTS)
        manager.deleteNotificationChannel(NotificationChannels.STATUS)
        NotificationChannels.ensureCreated(context)

        cancelNotifications()
        scope.cancel()
    }

    // --- the OS permission, genuinely changed ---------------------------------------------------

    @Test
    fun aDeniedSystemPermissionIsNotReportedAsHealthyDelivery() {
        setNotificationPermission(allowed = false)

        val health = AndroidNotificationHealthChecker(context).read()

        assertFalse(health.permissionGranted, "the app must observe the denied system permission")
        assertFalse(health.canDeliver(NotificationChannels.STATUS))
        assertFalse(health.canDeliver(NotificationChannels.ALERTS))
        assertFalse(health.canDeliverAnything)
    }

    @Test
    fun theSettingsScreenDoesNotClaimHealthyDeliveryWhileTheSystemDeniesIt() {
        setNotificationPermission(allowed = false)

        // The switch is on, but the system will not deliver. Those are different facts, and the
        // screen has to say so rather than reporting the switch.
        val viewModel = settingsViewModel(NotificationPreferences(statusNotificationEnabled = true, alertsEnabled = true))

        viewModel.refresh()

        val state = viewModel.state.value

        assertTrue(state.preferences.statusNotificationEnabled, "the user's preference is still on")
        assertFalse(state.statusDeliveryHealthy, "but delivery is not healthy")
        assertFalse(state.alertsDeliveryHealthy, "and neither are alerts")
    }

    @Test
    fun anAllowedSystemPermissionIsReportedAsHealthy() {
        setNotificationPermission(allowed = true)

        val health = AndroidNotificationHealthChecker(context).read()

        assertTrue(health.permissionGranted)
        assertTrue(health.canDeliver(NotificationChannels.STATUS))
        assertTrue(health.canDeliver(NotificationChannels.ALERTS))
    }

    // --- the real channel importance ------------------------------------------------------------

    @Test
    fun aChannelTheSystemHasBlockedIsReportedAsNotDeliverable() {
        // Recreate the alerts channel at IMPORTANCE_NONE, which is what the platform stores when a
        // user blocks it. Recreating is the only way a test can reach that state.
        manager.deleteNotificationChannel(NotificationChannels.ALERTS)
        manager.createNotificationChannel(
            NotificationChannel(
                NotificationChannels.ALERTS,
                "Quota alerts",
                NotificationManager.IMPORTANCE_NONE,
            ),
        )

        val health = AndroidNotificationHealthChecker(context).read()

        assertFalse(health.alertsChannelEnabled, "a blocked channel must be observed as blocked")
        assertFalse(health.canDeliver(NotificationChannels.ALERTS))
        assertTrue(
            health.canDeliver(NotificationChannels.STATUS),
            "one blocked channel must not condemn the other",
        )
    }

    @Test
    fun aChannelThatDoesNotExistYetIsNotTreatedAsBlocked() {
        // The channels are created on first use, so a fresh install has none. Reporting that as
        // blocked would be a false alarm about a problem that does not exist.
        manager.deleteNotificationChannel(NotificationChannels.ALERTS)
        manager.deleteNotificationChannel(NotificationChannels.STATUS)

        val health = AndroidNotificationHealthChecker(context).read()

        assertTrue(health.statusChannelEnabled)
        assertTrue(health.alertsChannelEnabled)
    }

    // --- what actually lands in the shade --------------------------------------------------------

    @Test
    fun theStatusNotificationIsOngoingSilentAndOnTheStatusChannel() = runBlocking {
        setNotificationPermission(allowed = true)

        QuotaNotificationManager(context).showStatus(
            snapshot = snapshot(),
            receivedAt = Instant.parse("2026-09-22T13:30:00Z"),
            stale = false,
            format = WatchFormat.Compact,
        )

        val posted = awaitNotification(NotificationChannels.STATUS_NOTIFICATION_ID)

        assertEquals(
            Notification.FLAG_ONGOING_EVENT,
            posted.flags and Notification.FLAG_ONGOING_EVENT,
            "the status notification stays until it is dismissed",
        )
        assertEquals(NotificationChannels.STATUS, posted.channelId)

        val channel = assertNotNull(manager.getNotificationChannel(NotificationChannels.STATUS))

        // Silence is a property of the channel, not of the notification: an ordinary quota change
        // must never buzz the wrist.
        assertEquals(NotificationManager.IMPORTANCE_LOW, channel.importance)
        assertFalse(channel.shouldVibrate())
    }

    @Test
    fun anAlertIsNotOngoingAndGoesToTheAlertChannel() = runBlocking {
        setNotificationPermission(allowed = true)

        val action = AlertAction(
            window = QuotaWindowKind.ShortWindow,
            severity = AlertSeverity.Critical,
            remainingPercent = 8.0,
        )

        QuotaNotificationManager(context).showAlerts(listOf(action), WatchFormat.Compact)

        val posted = awaitNotification(AlertNotificationRenderer.notificationId(action))

        assertEquals(
            0,
            posted.flags and Notification.FLAG_ONGOING_EVENT,
            "an alert is meant to be dismissed",
        )
        assertEquals(NotificationChannels.ALERTS, posted.channelId)
        assertEquals(
            Notification.FLAG_AUTO_CANCEL,
            posted.flags and Notification.FLAG_AUTO_CANCEL,
            "an alert clears itself when tapped",
        )

        val channel = assertNotNull(manager.getNotificationChannel(NotificationChannels.ALERTS))

        assertTrue(
            channel.importance >= NotificationManager.IMPORTANCE_DEFAULT,
            "the alert channel is the one the user is meant to notice",
        )
    }

    @Test
    fun postingWhileTheSystemDeniesNotificationsIsSilentlyDroppedRatherThanCrashing() = runBlocking {
        setNotificationPermission(allowed = false)

        val notifications = QuotaNotificationManager(context)

        // Neither of these may throw: the app asks the platform to post and moves on.
        notifications.showStatus(snapshot(), Instant.parse("2026-09-22T13:30:00Z"), stale = false, WatchFormat.Compact)
        notifications.showAlerts(
            listOf(AlertAction(QuotaWindowKind.Weekly, AlertSeverity.Warning, 18.0)),
            WatchFormat.Compact,
        )

        assertEquals(
            0,
            manager.activeNotifications.size,
            "nothing may be delivered while the system denies notifications",
        )
    }

    @Test
    fun theTestNotificationUsesTheStatusChannelSoTheUserCanCheckTheWholePath() = runBlocking {
        setNotificationPermission(allowed = true)

        QuotaNotificationManager(context).sendTestNotification(WatchFormat.Chinese)

        // The point of the action is to prove the path to the watch, so it must use the same channel
        // the real status notification uses.
        val posted = manager.activeNotifications.firstOrNull { it.notification.channelId == NotificationChannels.STATUS }

        assertNotNull(posted, "the test notification must use the status channel")

        Unit
    }

    // --- helpers --------------------------------------------------------------------------------

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

    /**
     * Changes the app-op that backs the notification permission.
     *
     * This is the real OS state, not a mock: `NotificationManagerCompat.areNotificationsEnabled()`
     * reads the same app-op.
     */
    private fun setNotificationPermission(allowed: Boolean) {
        // `areNotificationsEnabled` follows the runtime permission on API 33+ *and* the app-op behind
        // it. Setting only the app-op leaves the permission revoked and the reading unchanged, which
        // is what the first attempt did: every test then failed on "the notification permission did
        // not become true".
        val permission = "android.permission.POST_NOTIFICATIONS"
        val packageName = context.packageName

        if (allowed) {
            shell("appops set $packageName POST_NOTIFICATION allow")
            shell("pm grant $packageName $permission")
        } else {
            shell("pm revoke $packageName $permission")
            shell("appops set $packageName POST_NOTIFICATION deny")
        }

        awaitPermission(allowed)
    }

    /** Waits for the platform to reflect the change, rather than sleeping a fixed amount. */
    private fun awaitPermission(expected: Boolean, timeoutMillis: Long = TIMEOUT_MILLIS) {
        val deadline = System.currentTimeMillis() + timeoutMillis
        val enabled = NotificationManagerCompat.from(context).areNotificationsEnabled()

        while (System.currentTimeMillis() < deadline) {
            if (NotificationManagerCompat.from(context).areNotificationsEnabled() == expected) {
                return
            }

            Thread.sleep(POLL_MILLIS)
        }

        throw AssertionError(
            "the notification permission did not become $expected within ${timeoutMillis}ms " +
                "(it started as $enabled)",
        )
    }

    private fun awaitNotification(id: Int, timeoutMillis: Long = TIMEOUT_MILLIS): Notification {
        val deadline = System.currentTimeMillis() + timeoutMillis

        while (System.currentTimeMillis() < deadline) {
            manager.activeNotifications.firstOrNull { it.id == id }?.let { return it.notification }

            Thread.sleep(POLL_MILLIS)
        }

        throw AssertionError(
            "notification $id was never posted; active ids are " +
                manager.activeNotifications.map { it.id },
        )
    }

    private fun cancelNotifications() {
        manager.cancelAll()
    }

    private fun shell(command: String): String =
        instrumentation.uiAutomation.executeShellCommand(command).use { descriptor ->
            ParcelFileDescriptor.AutoCloseInputStream(descriptor).readBytes().toString(Charsets.UTF_8)
        }

    private fun settingsViewModel(preferences: NotificationPreferences): SettingsViewModel =
        SettingsViewModel(
            settings = InMemoryAlertSettings(preferences),
            health = AndroidNotificationHealthChecker(context),
            scheduler = NoopScheduler,
            modeController = SyncModeController(InMemoryAlertSettings(preferences), NoopScheduler, scope),
            lanPermission = MutableStateFlow(LocalNetworkPermissionState.Granted),
            bridgeReachable = { false },
            sendTestNotification = { },
            scope = scope,
        )

    /** Nothing in this test starts a service or schedules work. */
    private object NoopScheduler : SyncScheduler {
        override fun startLive() = Unit

        override fun stopLive() = Unit

        override fun enqueuePeriodic() = Unit

        override fun cancelPeriodic() = Unit

        override fun isLiveRunning(): Boolean = false

        override fun isPeriodicEnqueued(): Boolean = false
    }

    private companion object {
        const val TIMEOUT_MILLIS = 10_000L
        const val POLL_MILLIS = 50L
    }
}
