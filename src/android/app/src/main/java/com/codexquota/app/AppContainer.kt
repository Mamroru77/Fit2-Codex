package com.codexquota.app

import android.content.Context
import android.net.ConnectivityManager
import android.net.Network
import android.net.NetworkCapabilities
import com.codexquota.app.data.QuotaRepository
import com.codexquota.app.data.db.AppDatabase
import androidx.datastore.preferences.preferencesDataStoreFile
import com.codexquota.app.data.db.RoomAlertStateStore
import com.codexquota.app.data.db.RoomQuotaCache
import com.codexquota.app.data.discovery.BridgeDiscovery
import com.codexquota.app.data.discovery.BridgeIdentityProbe
import com.codexquota.app.data.discovery.NsdBridgeDiscovery
import com.codexquota.app.data.pairing.OkHttpPairingCoordinator
import com.codexquota.app.data.pairing.PairingCoordinator
import com.codexquota.app.data.security.BridgeEndpoint
import com.codexquota.app.data.security.KeystoreSecretBox
import com.codexquota.app.data.security.PairedBridgeStore
import com.codexquota.app.data.security.SecretStorage
import com.codexquota.app.data.ws.BridgeWebSocketFactory
import com.codexquota.app.data.ws.OkHttpBridgeWebSocket
import com.codexquota.app.data.api.OkHttpBridgeApi
import com.codexquota.app.sync.ConnectionManager
import com.codexquota.app.sync.EndpointProvider
import com.codexquota.app.sync.LocalNetworkPermissionState
import com.codexquota.app.sync.NetworkKind
import com.codexquota.app.sync.NetworkObserver
import java.io.File
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow

/**
 * The composition root.
 *
 * Everything is built here and nowhere else, so there is exactly one pairing store, one cache and one
 * connection manager per process. The parts that need the Android framework are wired to the pure
 * classes above them; none of the decision-making lives in this file.
 */
class AppContainer(private val context: Context) {

    /** The device's own name, as it will be shown on the Windows pairing window. */
    val displayName: String = "Android phone"

    /** The settings file name. */
    private val PREFERENCES_NAME = "codexquota_settings"

    /** The stored pairing, sealed with a Keystore-backed key. */
    val pairedBridgeStore: PairedBridgeStore = PairedBridgeStore(
        storage = FileSecretStorage(File(context.filesDir, "paired-bridge.json")),
        secretBox = KeystoreSecretBox(),
    )

    /** The local cache. */
    private val database: AppDatabase by lazy { AppDatabase.create(context) }

    /** The quota, history and events. */
    val repository: QuotaRepository by lazy {
        QuotaRepository(
            api = ApiFactory(this),
            cache = RoomQuotaCache(database.quotaDao()),
        )
    }

    /** DNS-SD discovery. */
    val discovery: BridgeDiscovery by lazy { NsdBridgeDiscovery(context) }

    /** Reads a discovered Bridge's identity before trust exists. */
    val identityProbe: BridgeIdentityProbe by lazy { BridgeIdentityProbe() }

    /** Runs pairing handshakes. */
    val pairing: PairingCoordinator by lazy { OkHttpPairingCoordinator(displayName) }

    /** Whether the platform's LAN permission has been granted. */
    val localNetworkPermission: StateFlow<LocalNetworkPermissionState> by lazy {
        com.codexquota.app.sync.AndroidLocalNetworkPermissionController(context).state
    }

    /** The network attachment, observed. */
    val network: NetworkObserver by lazy { AndroidNetworkObserver(context) }

    /** The notification settings. */
    val settings: com.codexquota.app.data.settings.AlertSettings by lazy {
        com.codexquota.app.data.settings.AlertSettingsStore(preferencesDataStore())
    }

    /** Delivers notifications. */
    val notifier: com.codexquota.app.notifications.QuotaNotificationManager by lazy {
        com.codexquota.app.notifications.QuotaNotificationManager(context)
    }

    /** The shared evaluate/persist/deliver path used by both run modes. */
    val alerts: com.codexquota.app.alerts.AlertProcessingRepository by lazy {
        com.codexquota.app.alerts.AlertProcessingRepository(
            repository = repository,
            alertState = RoomAlertStateStore(database.alertStateDao()),
            settings = settings,
            notifier = notifier,
        )
    }

    /** What happens to a trusted snapshot, whichever mode produced it. */
    val liveCoordinator: com.codexquota.app.sync.LiveSyncCoordinator by lazy {
        com.codexquota.app.sync.LiveSyncCoordinator(repository, alerts)
    }

    /** The three run modes, and the transitions between them. */
    val syncModeController: com.codexquota.app.sync.SyncModeController by lazy {
        com.codexquota.app.sync.SyncModeController(
            settings = settings,
            scheduler = scheduler(),
            scope = applicationScope,
        )
    }

    /** The scheduler, exposed so the settings screen can report what is actually running. */
    val syncScheduler: com.codexquota.app.sync.SyncScheduler by lazy { scheduler() }

    /** Whether notifications can actually be delivered. */
    val notificationHealth: com.codexquota.app.notifications.NotificationHealthChecker by lazy {
        com.codexquota.app.notifications.AndroidNotificationHealthChecker(context)
    }

    /** The single connection truth. */
    val connection: ConnectionManager by lazy {
        ConnectionManager(
            repository = repository,
            webSockets = liveWebSockets(),
            endpoints = EndpointProvider { pairedBridgeStore.load()?.endpoint },
            network = network,
        )
    }

    /** The process-wide scope for things that outlive any one screen. */
    private val applicationScope = kotlinx.coroutines.CoroutineScope(
        kotlinx.coroutines.SupervisorJob() + kotlinx.coroutines.Dispatchers.Default,
    )

    /** The DataStore-backed settings file. */
    private fun preferencesDataStore(): androidx.datastore.core.DataStore<androidx.datastore.preferences.core.Preferences> =
        context.preferencesDataStoreFile(PREFERENCES_NAME)
            .let { file ->
                androidx.datastore.preferences.core.PreferenceDataStoreFactory.create(
                    scope = applicationScope,
                    produceFile = { file },
                )
            }

    /** The WorkManager-backed scheduler. */
    private fun scheduler(): com.codexquota.app.sync.SyncScheduler =
        com.codexquota.app.sync.WorkManagerSyncScheduler(
            context = context,
            workManager = androidx.work.WorkManager.getInstance(context),
        )

    /**
     * A live-connection factory pinned to the stored pairing.
     *
     * It resolves the pairing and its credential at connect time, so a re-pair is picked up without
     * rebuilding the manager.
     */
    private fun liveWebSockets(): BridgeWebSocketFactory = BridgeWebSocketFactory { endpoint ->
        val bridge = pairedBridgeStore.load()
            ?: throw IllegalStateException("There is no paired Bridge to connect to.")

        val credential = pairedBridgeStore.openCredential(bridge)
            ?: throw IllegalStateException("The stored device credential could not be opened.")

        OkHttpBridgeWebSocket.create(bridge, credential).connect(endpoint)
    }
}

/** An [com.codexquota.app.data.api.BridgeApi] that follows the stored pairing. */
private class ApiFactory(private val container: AppContainer) :
    com.codexquota.app.data.api.BridgeApi {

    private fun api(): com.codexquota.app.data.api.BridgeApi {
        val bridge = container.pairedBridgeStore.load()
            ?: throw java.io.IOException("There is no paired Bridge.")

        val credential = container.pairedBridgeStore.openCredential(bridge)
            ?: throw java.io.IOException("The stored device credential could not be opened.")

        return OkHttpBridgeApi.create(bridge, credential)
    }

    override suspend fun fetchQuota() = api().fetchQuota()

    override suspend fun fetchHistory(hours: Int) = api().fetchHistory(hours)

    override suspend fun fetchEvents(hours: Int) = api().fetchEvents(hours)
}

/** Keeps the pairing in the app's own private storage. */
private class FileSecretStorage(private val file: File) : SecretStorage {
    override fun read(): String? = if (file.exists()) file.readText() else null

    override fun write(content: String) = file.writeText(content)

    override fun clear() {
        if (file.exists()) {
            file.delete()
        }
    }
}

/**
 * Reports whether the phone is on a network the Bridge can be reached over.
 *
 * V1 is LAN-only, so this distinguishes "on Wi-Fi or Ethernet" from "cellular only" rather than just
 * asking whether there is a network at all. Dialling a private address from cellular can only fail
 * slowly, and doing so repeatedly is what the offline-cached state exists to avoid.
 */
private class AndroidNetworkObserver(context: Context) : NetworkObserver {

    private val manager = context.getSystemService(Context.CONNECTIVITY_SERVICE) as ConnectivityManager

    private val state = MutableStateFlow(evaluate())

    override val kind: StateFlow<NetworkKind> = state.asStateFlow()

    override fun hasLocalNetworkPermission(): Boolean = true

    init {
        manager.registerDefaultNetworkCallback(
            object : ConnectivityManager.NetworkCallback() {
                override fun onAvailable(network: Network) {
                    state.value = evaluate()
                }

                override fun onLost(network: Network) {
                    state.value = evaluate()
                }

                override fun onCapabilitiesChanged(
                    network: Network,
                    capabilities: NetworkCapabilities,
                ) {
                    state.value = evaluate()
                }
            },
        )
    }

    private fun evaluate(): NetworkKind {
        val capabilities = manager.getNetworkCapabilities(manager.activeNetwork)
            ?: return NetworkKind.None

        return when {
            capabilities.hasTransport(NetworkCapabilities.TRANSPORT_WIFI) -> NetworkKind.Lan
            capabilities.hasTransport(NetworkCapabilities.TRANSPORT_ETHERNET) -> NetworkKind.Lan

            capabilities.hasTransport(NetworkCapabilities.TRANSPORT_CELLULAR) -> NetworkKind.CellularOnly

            else -> NetworkKind.None
        }
    }
}
