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

        ensureAppChannels()

        // Every test starts from "the system will deliver", so an allowed-state test is not at the
        // mercy of the emulator's default and a denied-state test is a change rather than a
        // coincidence.
        setDeliveryEnabled(enabled = true)
    }

    /**
     * Makes sure the app's two channels exist at their normal importance.
     *
     * This class deliberately never lowers them. An earlier version recreated the alerts channel at
     * `IMPORTANCE_NONE` to prove the app notices a blocked channel, and then could not put it back:
     * Android does not let an app raise a channel's importance again, and deleting and recreating it
     * did not escape that either — the recreate came back at `IMPORTANCE_NONE`, which left three
     * tests failing on "the alerts channel could not be reset to its normal importance".
     *
     * The blocked-channel case is now checked on a channel this class owns. The property under test
     * is unchanged — real platform channel state, read through the app's own checker — and the app's
     * channels are never touched.
     */
    private fun ensureAppChannels() {
        NotificationChannels.ensureCreated(context)

        assertEquals(
            NotificationManager.IMPORTANCE_LOW,
            manager.getNotificationChannel(NotificationChannels.STATUS)?.importance,
            "the status channel is not at its normal importance",
        )
        assertEquals(
            NotificationManager.IMPORTANCE_DEFAULT,
            manager.getNotificationChannel(NotificationChannels.ALERTS)?.importance,
            "the alerts channel is not at its normal importance",
        )
    }

    @AfterTest
    fun tearDown() {
        // Restore the real state, so a later test never inherits this one's permission change.
        setDeliveryEnabled(enabled = true)

        cancelNotifications()
        scope.cancel()
    }

    // --- OS delivery state, genuinely changed ---------------------------------------------------



    @Test
    fun whenTheSystemWillDeliverTheAppReportsHealthyDelivery() {
        setDeliveryEnabled(enabled = true)

        val health = AndroidNotificationHealthChecker(context).read()

        assertTrue(health.permissionGranted, "delivery permission was not reported as granted")
        assertTrue(
            health.statusChannelEnabled,
            "the status channel was reported as blocked; importance is " +
                manager.getNotificationChannel(NotificationChannels.STATUS)?.importance,
        )
        assertTrue(
            health.alertsChannelEnabled,
            "the alerts channel was reported as blocked; importance is " +
                manager.getNotificationChannel(NotificationChannels.ALERTS)?.importance,
        )
    }

    // --- the real channel importance ------------------------------------------------------------

    @Test
    fun aChannelTheSystemHasBlockedIsReportedAsNotDeliverable() {
        // A channel of this class's own, at IMPORTANCE_NONE, which is what the platform stores when
        // a channel is blocked. It is deliberately not one of the app's: an app cannot raise a
        // channel's importance again, so using the app's would leave it blocked for every later test
        // in this process — and for the app.
        manager.createNotificationChannel(
            NotificationChannel(BLOCKED_TEST_CHANNEL, "Blocked test channel", NotificationManager.IMPORTANCE_NONE),
        )

        val checker = AndroidNotificationHealthChecker(context)

        assertFalse(
            checker.isChannelEnabled(BLOCKED_TEST_CHANNEL),
            "a blocked channel must be observed as blocked",
        )

        // And the app's own channels are unaffected by it.
        assertTrue(
            checker.isChannelEnabled(NotificationChannels.STATUS),
            "one blocked channel must not condemn another",
        )
        assertTrue(checker.isChannelEnabled(NotificationChannels.ALERTS))
    }

    @Test
    fun aChannelThatDoesNotExistYetIsNotTreatedAsBlocked() {
        // The channels are created on first use, so a fresh install has none. Reporting that as
        // blocked would be a false alarm about a problem that does not exist.
        val checker = AndroidNotificationHealthChecker(context)

        assertTrue(checker.isChannelEnabled("com.codexquota.app.test.channel.that.does.not.exist"))
        assertTrue(checker.isChannelEnabled(NotificationChannels.STATUS))
        assertTrue(checker.isChannelEnabled(NotificationChannels.ALERTS))
    }

    // --- what actually lands in the shade --------------------------------------------------------

    @Test
    fun theStatusNotificationIsOngoingSilentAndOnTheStatusChannel() = runBlocking {
        setDeliveryEnabled(enabled = true)

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
        setDeliveryEnabled(enabled = true)

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
    fun theTestNotificationUsesTheStatusChannelSoTheUserCanCheckTheWholePath() = runBlocking {
        setDeliveryEnabled(enabled = true)

        QuotaNotificationManager(context).sendTestNotification(WatchFormat.Chinese)

        // The point of the action is to prove the path to the watch, so it must use the same channel
        // the real status notification uses.
        val posted = awaitNotificationOn(NotificationChannels.STATUS)

        assertEquals(NotificationChannels.STATUS, posted.channelId)
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
     * Makes sure the system will deliver notifications, without ever revoking the runtime
     * permission.
     *
     * **`pm revoke` must never be called from here.** This instrumentation runs inside the target
     * package's process, and Android kills that process when its runtime permission is revoked — the
     * CI log shows `ActivityManager: Killing com.codexquota.app/u0a216 (adj 0): permissions revoked`,
     * after which only three results were ever written and neither `StageCIntegrationTest` nor
     * `StageDRuntimeIntegrationTest` ran at all. Granting is safe; revoking is not.
     *
     * The denied state is therefore produced outside the process, by
     * [NotificationDeliveryDisabledTest]'s own workflow step. This class only ever needs the
     * opposite, so that is all it does.
     */
    private fun setDeliveryEnabled(enabled: Boolean) {
        val packageName = context.packageName
        val permission = "android.permission.POST_NOTIFICATIONS"

        // A grant does not restart the process, and it is what makes the allowed-state tests real
        // rather than dependent on the emulator's default.
        shell("pm grant $packageName $permission")

        // `ignore` is the app-op mode that means "this app does not get the operation", which is what
        // "notifications are off" is at the platform level.
        shell("appops set $packageName POST_NOTIFICATION ${if (enabled) "allow" else "ignore"}")

        awaitDelivery(enabled)
    }

    /**
     * Waits for the platform to reflect the change, rather than sleeping a fixed amount.
     *
     * This is the same call the app makes, so the wait and the assertion are about one thing.
     */
    private fun awaitDelivery(expected: Boolean, timeoutMillis: Long = TIMEOUT_MILLIS) {
        val deadline = System.currentTimeMillis() + timeoutMillis
        val startedAs = NotificationManagerCompat.from(context).areNotificationsEnabled()

        while (System.currentTimeMillis() < deadline) {
            if (NotificationManagerCompat.from(context).areNotificationsEnabled() == expected) {
                return
            }

            Thread.sleep(POLL_MILLIS)
        }

        throw AssertionError(
            "the OS did not become ${if (expected) "willing" else "unwilling"} to deliver " +
                "notifications within ${timeoutMillis}ms (areNotificationsEnabled started as " +
                "$startedAs); the app-op change may not be taking effect on this device",
        )
    }

    /**
     * Waits for a notification on a channel.
     *
     * Posting is asynchronous, so reading the shade on the next line is a race — which is what made
     * this test fail while its two siblings, which already waited, passed.
     */
    private fun awaitNotificationOn(channelId: String, timeoutMillis: Long = TIMEOUT_MILLIS): Notification {
        val deadline = System.currentTimeMillis() + timeoutMillis

        while (System.currentTimeMillis() < deadline) {
            manager.activeNotifications
                .firstOrNull { it.notification.channelId == channelId }
                ?.let { return it.notification }

            Thread.sleep(POLL_MILLIS)
        }

        throw AssertionError(
            "no notification appeared on channel $channelId; active channels are " +
                manager.activeNotifications.map { it.notification.channelId },
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

        /** A channel of this test class's own, used to prove blocked channels are noticed. */
        const val BLOCKED_TEST_CHANNEL = "com.codexquota.app.test.blocked_channel"
    }
}
