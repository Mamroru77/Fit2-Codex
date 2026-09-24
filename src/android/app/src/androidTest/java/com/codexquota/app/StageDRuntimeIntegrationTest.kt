package com.codexquota.app

import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.content.pm.PackageInfo
import android.content.pm.PackageManager
import android.content.pm.ServiceInfo
import android.os.Build
import androidx.lifecycle.Lifecycle
import androidx.room.Room
import androidx.test.core.app.ActivityScenario
import androidx.test.core.app.ApplicationProvider
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.work.WorkInfo
import androidx.work.WorkManager
import com.codexquota.app.alerts.AlertNotifier
import com.codexquota.app.alerts.AlertProcessingRepository
import com.codexquota.app.alerts.AlertStateStore
import com.codexquota.app.data.QuotaRepository
import com.codexquota.app.data.db.AppDatabase
import com.codexquota.app.data.db.RoomAlertStateStore
import com.codexquota.app.data.db.RoomQuotaCache
import com.codexquota.app.data.settings.AlertSettings
import com.codexquota.app.data.settings.InMemoryAlertSettings
import com.codexquota.app.data.settings.NotificationPreferences
import com.codexquota.app.data.settings.WatchFormat
import com.codexquota.app.domain.QuotaSnapshot
import com.codexquota.app.domain.QuotaSourceStatus
import com.codexquota.app.domain.QuotaWindow
import com.codexquota.app.domain.alerts.AlertAction
import com.codexquota.app.domain.alerts.AlertSeverity
import com.codexquota.app.domain.alerts.QuotaWindowKind
import com.codexquota.app.domain.alerts.WindowRules
import com.codexquota.app.notifications.QuotaNotificationManager
import com.codexquota.app.sync.LiveBridgeService
import com.codexquota.app.sync.LiveSyncCoordinator
import com.codexquota.app.sync.PERIODIC_WORK_NAME
import com.codexquota.app.sync.SyncMode
import com.codexquota.app.sync.CodexQuotaWorkerFactory
import com.codexquota.app.sync.SyncModeController
import com.codexquota.app.sync.WorkManagerSyncScheduler
import java.time.Duration
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
import kotlinx.coroutines.runBlocking
import org.junit.runner.RunWith

/**
 * The Stage D runtime gate.
 *
 * This is where the two things the JVM cannot show are settled: that the run modes really do and do
 * not start an Android foreground service and a WorkManager job, and that the alert state really
 * does survive a process recreation. The mode assertions use the real `WorkManager` and the real
 * service; the alert assertions use a real Room database, so "persisted" means persisted.
 *
 * Only delivery is recorded rather than observed, and it is recorded *around* the real notifier, so
 * the platform is still the thing that posts.
 */
@RunWith(AndroidJUnit4::class)
class StageDRuntimeIntegrationTest {

    private lateinit var context: Context
    private lateinit var scope: CoroutineScope
    private lateinit var workManager: WorkManager
    private var database: AppDatabase? = null

    @BeforeTest
    fun setUp() {
        context = ApplicationProvider.getApplicationContext()
        scope = CoroutineScope(SupervisorJob() + Dispatchers.Default)

        // The application's own WorkManager, with the application's own worker factory. Using the
        // real one rather than a test-initialised one is deliberate: it means these tests also prove
        // the wiring the app ships with, which is where the defect they were written for lived.
        workManager = WorkManager.getInstance(context)

        // Start from a known state: nothing scheduled, nothing running.
        workManager.cancelUniqueWork(PERIODIC_WORK_NAME)
        context.stopService(Intent(context, LiveBridgeService::class.java))
    }

    @AfterTest
    fun tearDown() {
        workManager.cancelUniqueWork(PERIODIC_WORK_NAME)
        context.stopService(Intent(context, LiveBridgeService::class.java))
        scope.cancel()
        database?.close()
    }

    // --- the mode matrix ------------------------------------------------------------------------

    @Test
    fun offWithAlertsOffLeavesNeitherServiceNorWorker() = runBlocking {
        controller(NotificationPreferences(statusNotificationEnabled = false, alertsEnabled = false)).applyMode(SyncMode.Off)

        assertFalse(LiveBridgeService.isRunning, "OFF must not leave a foreground service running")
        assertTrue(uniqueWork().isEmpty(), "OFF must not leave a periodic worker, but found ${uniqueWork()}")
    }

    @Test
    fun offWithAlertsOnSchedulesTheWorkerAndNoService() = runBlocking {
        controller(NotificationPreferences(statusNotificationEnabled = false, alertsEnabled = true)).applyMode(SyncMode.Background)

        assertFalse(LiveBridgeService.isRunning, "BACKGROUND must not start a foreground service")
        assertTrue(
            uniqueWork().any { it.state == WorkInfo.State.ENQUEUED },
            "BACKGROUND must leave exactly one enqueued periodic worker, but found ${uniqueWork()}",
        )
        assertEquals(1, uniqueWork().size, "there must never be more than one periodic job")
    }

    @Test
    fun liveWithAlertsOffRunsTheConnectedDeviceServiceAndNoWorker() = runBlocking {
        withForegroundApp {
            controller(NotificationPreferences(statusNotificationEnabled = true, alertsEnabled = false)).applyMode(SyncMode.Live)

            awaitServiceRunning(expected = true)
        }

        assertTrue(
            uniqueWork().none { it.state == WorkInfo.State.ENQUEUED },
            "LIVE must not leave a periodic worker, but found ${uniqueWork()}",
        )
    }

    @Test
    fun liveWithAlertsOnStillRunsOnlyTheService() = runBlocking {
        withForegroundApp {
            controller(NotificationPreferences(statusNotificationEnabled = true, alertsEnabled = true)).applyMode(SyncMode.Live)

            awaitServiceRunning(expected = true)
        }

        assertTrue(
            uniqueWork().none { it.state == WorkInfo.State.ENQUEUED },
            "alerts being on must not add a second synchronisation path, but found ${uniqueWork()}",
        )
    }

    @Test
    fun switchingFromLiveToBackgroundStopsTheServiceBeforeScheduling() = runBlocking {
        withForegroundApp {
            controller(NotificationPreferences(statusNotificationEnabled = true)).applyMode(SyncMode.Live)
            awaitServiceRunning(expected = true)
        }

        controller(NotificationPreferences(alertsEnabled = true)).applyMode(SyncMode.Background)

        awaitServiceRunning(expected = false)
        assertTrue(uniqueWork().any { it.state == WorkInfo.State.ENQUEUED })
    }

    @Test
    fun anExplicitStopLeavesTheServiceStoppedAndItDoesNotRestartItself() = runBlocking {
        withForegroundApp {
            controller(NotificationPreferences(statusNotificationEnabled = true)).applyMode(SyncMode.Live)
            awaitServiceRunning(expected = true)
        }

        context.stopService(Intent(context, LiveBridgeService::class.java))

        awaitServiceRunning(expected = false)

        // Nothing may bring it back on its own: not a boot receiver, not a restart loop, not a
        // sticky restart. The user resumes it.
        Thread.sleep(SETTLE_MILLIS)

        assertFalse(
            LiveBridgeService.isRunning,
            "the service must stay stopped until the user resumes it",
        )
    }

    @Test
    fun theApplicationInstallsTheWorkerFactory() {
        val application = context.applicationContext as CodexQuotaApplication

        // Without this, the periodic worker is enqueued and then fails every time it runs: WorkManager
        // cannot construct a worker whose dependencies are constructor parameters.
        assertTrue(
            application.workManagerConfiguration.workerFactory is CodexQuotaWorkerFactory,
            "the app must configure WorkManager with the factory that can build BackgroundSyncWorker, " +
                "but it has ${application.workManagerConfiguration.workerFactory}",
        )
    }

    @Test
    fun theDeclaredForegroundServiceTypeIsConnectedDevice() {
        val info = serviceInfo()

        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            assertEquals(
                ServiceInfo.FOREGROUND_SERVICE_TYPE_CONNECTED_DEVICE,
                info.foregroundServiceType and ServiceInfo.FOREGROUND_SERVICE_TYPE_CONNECTED_DEVICE,
                "the service must be declared connectedDevice",
            )
            assertEquals(
                0,
                info.foregroundServiceType and ServiceInfo.FOREGROUND_SERVICE_TYPE_DATA_SYNC,
                "the service must not be declared dataSync: all-day dataSync is subject to a " +
                    "six-hour background limit that live sync would hit",
            )
        }
    }

    @Test
    fun theApplicationDeclaresNoBootReceiver() {
        val packageInfo: PackageInfo = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            context.packageManager.getPackageInfo(
                context.packageName,
                PackageManager.PackageInfoFlags.of(PackageManager.GET_RECEIVERS.toLong()),
            )
        } else {
            @Suppress("DEPRECATION")
            context.packageManager.getPackageInfo(context.packageName, PackageManager.GET_RECEIVERS)
        }

        val receivers = packageInfo.receivers.orEmpty()

        // V1 does not start live sync from boot. The setting survives a reboot; the user resumes it.
        assertTrue(
            receivers.none { it.name?.contains("Boot") == true },
            "no boot receiver may exist, but found ${receivers.map { it.name }}",
        )
    }

    // --- the alert matrix, against a real database -----------------------------------------------

    @Test
    fun crossingFromAboveWarningStraightIntoCriticalDeliversExactlyOneCriticalAlert() = runBlocking {
        val state = alertStateStore()
        val notifier = RecordingNotifier(QuotaNotificationManager(context))
        val alerts = alertProcessing(state, notifier)

        // 25%: nothing.
        alerts.process(snapshot(shortRemaining = 25.0), fresh = true)
        assertTrue(notifier.delivered.isEmpty(), "25% is above both thresholds")

        // 9%: one critical, and no separate warning for the crossing it jumped over.
        alerts.process(snapshot(shortRemaining = 9.0), fresh = true)

        assertEquals(1, notifier.delivered.size, "expected exactly one alert, got ${notifier.delivered}")
        val action = notifier.delivered.single().single()

        assertEquals(AlertSeverity.Critical, action.severity)
        assertEquals(QuotaWindowKind.ShortWindow, action.window)
        assertFalse(action.isRepeat)
    }

    @Test
    fun aPersistedCriticalStateSurvivesRecreationWithoutRepeating() = runBlocking {
        val state = alertStateStore()

        val before = RecordingNotifier(QuotaNotificationManager(context))
        alertProcessing(state, before).process(snapshot(shortRemaining = 9.0), fresh = true)

        assertEquals(1, before.delivered.size)

        // A new process: a new repository over the same persisted alert state, exactly as a restart
        // produces. The stored state is what stops the alert being replayed.
        val after = RecordingNotifier(QuotaNotificationManager(context))
        alertProcessing(state, after).process(snapshot(shortRemaining = 9.0), fresh = true)

        assertTrue(
            after.delivered.isEmpty(),
            "a restart must not replay an alert the user has already seen, but delivered ${after.delivered}",
        )
    }

    @Test
    fun anOfflineSnapshotPausesTheRepeatAndAFreshOneAfterTheIntervalResumesIt() = runBlocking {
        val settings = InMemoryAlertSettings(
            NotificationPreferences(
                alertsEnabled = true,
                repeatCriticalEnabled = true,
                repeatInterval = Duration.ofMinutes(30),
            ),
        )

        val state = alertStateStore()
        var now = Instant.parse("2026-09-22T13:30:00Z")
        val notifier = RecordingNotifier(QuotaNotificationManager(context))

        val alerts = AlertProcessingRepository(
            repository = quotaRepository(),
            alertState = state,
            settings = settings,
            notifier = notifier,
            clock = { now },
        )

        // The first critical fires.
        alerts.process(snapshot(shortRemaining = 8.0), fresh = true)
        assertEquals(1, notifier.delivered.size)

        // Offline: the repeat must pause, even though the interval has elapsed.
        now = now.plus(Duration.ofHours(2))
        alerts.process(snapshot(shortRemaining = 8.0), fresh = false)

        assertEquals(1, notifier.delivered.size, "an offline snapshot must not repeat an alert")

        // Fresh again, and now past the interval: the repeat may fire.
        alerts.process(snapshot(shortRemaining = 8.0), fresh = true)

        assertEquals(2, notifier.delivered.size, "a fresh snapshot past the interval may repeat")
        assertTrue(notifier.delivered.last().single().isRepeat)
    }

    @Test
    fun aSourceThatIsNotOnlineNeverRaisesAnAlert() = runBlocking {
        val notifier = RecordingNotifier(QuotaNotificationManager(context))
        val alerts = alertProcessing(alertStateStore(), notifier)

        // The numbers say 8%, but the Bridge says its own source is stale.
        alerts.process(snapshot(shortRemaining = 8.0, status = QuotaSourceStatus.Stale), fresh = false)

        assertTrue(
            notifier.delivered.isEmpty(),
            "data that is not current cannot support a claim about the present",
        )
    }

    @Test
    fun aWindowResetRearmsTheAlertForTheNextWindow() = runBlocking {
        val state = alertStateStore()
        val notifier = RecordingNotifier(QuotaNotificationManager(context))
        val alerts = alertProcessing(state, notifier)

        alerts.process(snapshot(shortRemaining = 8.0), fresh = true)
        assertEquals(1, notifier.delivered.size)

        // A new window: the reset time has moved on and the quota is back at 100%.
        alerts.process(
            snapshot(shortRemaining = 100.0, shortResetsAt = Instant.parse("2026-09-22T20:42:00Z")),
            fresh = true,
        )
        assertEquals(1, notifier.delivered.size, "a reset is not an alert")

        // And the new window can alert in its turn.
        alerts.process(
            snapshot(shortRemaining = 9.0, shortResetsAt = Instant.parse("2026-09-22T20:42:00Z")),
            fresh = true,
        )

        assertEquals(2, notifier.delivered.size, "the new window must be able to alert")
    }

    @Test
    fun theCoordinatorCachesEvaluatesAndUpdatesTheStatusInOrder() = runBlocking {
        val state = alertStateStore()
        val notifier = RecordingNotifier(QuotaNotificationManager(context))
        val repository = quotaRepository()
        val alerts = alertProcessing(state, notifier, repository)
        val coordinator = LiveSyncCoordinator(repository, alerts)

        val evaluation = coordinator.onTrustedSnapshot(snapshot(shortRemaining = 8.0), sequence = 1L)

        assertNotNull(evaluation)
        assertEquals(1, notifier.delivered.size)
        assertEquals(8.0, repository.current().snapshot?.shortWindow?.remainingPercent)
        assertFalse(repository.current().stale, "a live frame is current by definition")

        // The same sequence again is a duplicate, not a second evaluation.
        assertEquals(null, coordinator.onTrustedSnapshot(snapshot(shortRemaining = 8.0), sequence = 1L))
        assertEquals(1, notifier.delivered.size)
    }

    // --- helpers ---------------------------------------------------------------------------------

    private fun controller(preferences: NotificationPreferences): SyncModeController =
        SyncModeController(
            settings = InMemoryAlertSettings(preferences),
            scheduler = WorkManagerSyncScheduler(context, workManager),
            scope = scope,
        )

    /** The real WorkManager's answer about the unique periodic job. */
    private fun uniqueWork(): List<WorkInfo> =
        workManager.getWorkInfosForUniqueWork(PERIODIC_WORK_NAME).get()

    private fun serviceInfo(): ServiceInfo =
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            context.packageManager.getServiceInfo(
                ComponentName(context, LiveBridgeService::class.java),
                PackageManager.ComponentInfoFlags.of(0),
            )
        } else {
            @Suppress("DEPRECATION")
            context.packageManager.getServiceInfo(ComponentName(context, LiveBridgeService::class.java), 0)
        }

    /**
     * Waits for the live service to be running, or to be stopped.
     *
     * `startForegroundService` and `stopService` are both asynchronous: the flag is set by the
     * service's own `onCreate`/`onDestroy`, which happen on the main thread after the call returns.
     * Asserting immediately was reading the flag before the service had been created, which is why
     * four tests reported "LIVE must run the foreground service" while logcat was recording
     * `Background started FGS: Allowed` for this very package.
     */
    private fun awaitServiceRunning(expected: Boolean, timeoutMillis: Long = SERVICE_TIMEOUT_MILLIS) {
        val deadline = System.currentTimeMillis() + timeoutMillis

        while (System.currentTimeMillis() < deadline) {
            if (LiveBridgeService.isRunning == expected) {
                return
            }

            Thread.sleep(SERVICE_POLL_MILLIS)
        }

        throw AssertionError(
            "the live service did not become ${if (expected) "running" else "stopped"} within " +
                "${timeoutMillis}ms",
        )
    }

    /**
     * Runs [body] with the app in the foreground.
     *
     * Android only permits an app to start a foreground service while it is visible, so the service
     * is genuinely started rather than its start merely being requested.
     */
    private suspend fun withForegroundApp(body: suspend () -> Unit) {
        val scenario: ActivityScenario<MainActivity> = ActivityScenario.launch(MainActivity::class.java)

        try {
            scenario.moveToState(Lifecycle.State.RESUMED)
            body()
        } finally {
            scenario.close()
        }
    }

    /**
     * An in-memory database, one per test.
     *
     * A file-backed one persisted between tests, so an alert test could find a `CriticalTriggered`
     * state an earlier test had stored, correctly decide there was nothing new to deliver, and fail
     * an assertion that was really about test isolation. The assertions about persistence are still
     * real: they build a second `AlertProcessingRepository` over the same store, which is exactly
     * what a process restart produces.
     */
    private fun database(): AppDatabase {
        database?.let { return it }

        val db = Room.inMemoryDatabaseBuilder(context, AppDatabase::class.java)
            .allowMainThreadQueries()
            .build()

        database = db

        return db
    }

    private fun alertStateStore(): AlertStateStore = RoomAlertStateStore(database().alertStateDao())

    private fun quotaRepository(): QuotaRepository =
        QuotaRepository(
            api = FailingApi,
            cache = RoomQuotaCache(database().quotaDao()),
        )

    private fun alertProcessing(
        state: AlertStateStore,
        notifier: AlertNotifier,
        repository: QuotaRepository = quotaRepository(),
    ): AlertProcessingRepository = AlertProcessingRepository(
        repository = repository,
        alertState = state,
        settings = InMemoryAlertSettings(
            NotificationPreferences(alertsEnabled = true, shortWindow = WindowRules(20.0, 10.0)),
        ),
        notifier = notifier,
        clock = { Instant.parse("2026-09-22T13:30:00Z") },
    )

    private fun snapshot(
        shortRemaining: Double,
        status: QuotaSourceStatus = QuotaSourceStatus.Online,
        shortResetsAt: Instant = Instant.parse("2026-09-22T15:42:00Z"),
    ): QuotaSnapshot = QuotaSnapshot(
        schemaVersion = 1,
        generatedAt = Instant.parse("2026-09-22T13:30:00Z"),
        source = "codex_app_server",
        status = status,
        lastSuccessfulSyncAt = Instant.parse("2026-09-22T13:30:00Z"),
        shortWindow = QuotaWindow(
            usedPercent = 100.0 - shortRemaining,
            remainingPercent = shortRemaining,
            windowMinutes = 300,
            resetsAt = shortResetsAt,
        ),
        weekly = QuotaWindow(
            usedPercent = 46.0,
            remainingPercent = 54.0,
            windowMinutes = 10080,
            resetsAt = Instant.parse("2026-09-28T00:00:00Z"),
        ),
    )

    /** Records what was delivered, then delivers it for real. */
    private class RecordingNotifier(private val delegate: AlertNotifier) : AlertNotifier {
        val delivered = mutableListOf<List<AlertAction>>()

        override suspend fun showStatus(
            snapshot: QuotaSnapshot?,
            receivedAt: Instant?,
            stale: Boolean,
            format: WatchFormat,
        ) = delegate.showStatus(snapshot, receivedAt, stale, format)

        override suspend fun showAlerts(actions: List<AlertAction>, format: WatchFormat) {
            delivered += actions
            delegate.showAlerts(actions, format)
        }

        override suspend fun cancelStatus() = delegate.cancelStatus()
    }

    /** The alert tests never go to the network; the snapshot is handed in directly. */
    private object FailingApi : com.codexquota.app.data.api.BridgeApi {
        override suspend fun fetchQuota(): QuotaSnapshot = throw java.io.IOException("not used")

        override suspend fun fetchHistory(hours: Int) = throw java.io.IOException("not used")

        override suspend fun fetchEvents(hours: Int) = throw java.io.IOException("not used")
    }

    private companion object {
        const val SETTLE_MILLIS = 1_000L
        const val SERVICE_TIMEOUT_MILLIS = 5_000L
        const val SERVICE_POLL_MILLIS = 50L
    }
}
