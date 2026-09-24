package com.codexquota.app.ui.connection

import com.codexquota.app.data.discovery.BridgeDiscovery
import com.codexquota.app.data.discovery.BridgeIdentityProbe
import com.codexquota.app.data.discovery.DiscoveredService
import com.codexquota.app.data.pairing.ConfirmedBridgeIdentity
import com.codexquota.app.data.pairing.DiscoveredBridgeIdentity
import com.codexquota.app.data.pairing.IdentityMismatchException
import com.codexquota.app.data.pairing.PairingCoordinator
import com.codexquota.app.data.pairing.PairingOutcome
import com.codexquota.app.data.pairing.PairingQrException
import com.codexquota.app.data.pairing.PairingQrPayload
import com.codexquota.app.data.security.BridgeEndpoint
import com.codexquota.app.data.security.PairedBridge
import com.codexquota.app.data.security.PairedBridgeStore
import com.codexquota.app.sync.LocalNetworkPermissionState
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.catch
import kotlinx.coroutines.flow.launchIn
import kotlinx.coroutines.flow.onEach
import kotlinx.coroutines.launch

/** Where the pairing screen is in its own small flow. */
enum class ConnectionPhase {
    Idle,
    Searching,
    AwaitingConfirmation,
    Pairing,
    Paired,
}

/** A failure the screen has to explain, with the reason rather than a generic message. */
data class ConnectionProblem(
    val kind: Kind,
    val detail: String?,
) {
    /** What went wrong, as something the UI can branch on. */
    enum class Kind {
        Security,
        RepairRequired,
        Rejected,
        Expired,
        TimedOut,
        InvalidCode,
        Unreachable,
    }
}

/** Everything the connection screen draws. */
data class ConnectionUiState(
    val phase: ConnectionPhase,
    val pairedBridgeName: String?,
    val pairedIdentityCode: String?,
    val candidates: List<DiscoveredService>,
    val pendingIdentity: DiscoveredBridgeIdentity?,
    val lanPermission: LocalNetworkPermissionState,
    val problem: ConnectionProblem?,
    val message: String?,
)

/**
 * The connection screen's state.
 *
 * It owns the pairing flow and nothing else. It never talks to a socket: once a pairing succeeds the
 * [com.codexquota.app.sync.ConnectionManager] picks the Bridge up from the store on its own.
 */
class ConnectionViewModel(
    private val store: PairedBridgeStore,
    private val discovery: BridgeDiscovery,
    private val probe: BridgeIdentityProbe,
    private val pairing: PairingCoordinator,
    private val lanPermission: StateFlow<LocalNetworkPermissionState>,
    private val displayName: String,
    private val scope: CoroutineScope,
) {
    private val _state = MutableStateFlow(
        ConnectionUiState(
            phase = ConnectionPhase.Idle,
            pairedBridgeName = store.load()?.displayName,
            pairedIdentityCode = store.load()?.let { identityCodeOf(it) },
            candidates = emptyList(),
            pendingIdentity = null,
            lanPermission = lanPermission.value,
            problem = null,
            message = null,
        ),
    )

    /** The connection screen state. */
    val state: StateFlow<ConnectionUiState> = _state.asStateFlow()

    init {
        lanPermission
            .onEach { permission -> _state.value = _state.value.copy(lanPermission = permission) }
            .launchIn(scope)
    }

    /**
     * Looks for Bridges on the local network.
     *
     * Discovery only ever produces a candidate. It does not establish trust, and the state machine
     * below refuses to send anything until a person has confirmed the identity.
     */
    fun startDiscovery() {
        if (_state.value.lanPermission == LocalNetworkPermissionState.Required) {
            _state.value = _state.value.copy(
                problem = ConnectionProblem(ConnectionProblem.Kind.Unreachable, null),
                message = null,
            )
            return
        }

        _state.value = _state.value.copy(phase = ConnectionPhase.Searching, problem = null)

        discovery.services()
            .catch { _state.value = _state.value.copy(phase = ConnectionPhase.Idle) }
            .onEach { service ->
                _state.value = _state.value.copy(
                    candidates = (_state.value.candidates + service).distinctBy { it.host to it.port },
                )
            }
            .launchIn(scope)
    }

    /**
     * Reads the identity of a candidate and puts it in front of the user.
     *
     * The probe is deliberately a separate step from pairing: the verification code it produces is
     * what the user compares, and no pairing request exists until they have.
     */
    fun selectCandidate(service: DiscoveredService) {
        val endpoint = service.endpoint
        if (endpoint == null) {
            _state.value = _state.value.copy(
                problem = ConnectionProblem(ConnectionProblem.Kind.Unreachable, service.host),
            )
            return
        }

        _state.value = _state.value.copy(phase = ConnectionPhase.Pairing, problem = null)

        scope.launch {
            try {
                val identity = probe.probe(endpoint)

                _state.value = _state.value.copy(
                    phase = ConnectionPhase.AwaitingConfirmation,
                    pendingIdentity = identity,
                    problem = null,
                )
            } catch (e: IdentityMismatchException) {
                fail(ConnectionProblem.Kind.Security, e.message)
            } catch (e: Exception) {
                fail(ConnectionProblem.Kind.Unreachable, e.message)
            }
        }
    }

    /**
     * Pairs with the identity the user confirmed.
     *
     * The confirmation is carried in the type, so this method cannot be reached by accident: there is
     * no way to obtain a [ConfirmedBridgeIdentity] except by calling `confirmedByUser()`.
     */
    fun confirmPendingIdentity() {
        val pending = _state.value.pendingIdentity ?: return
        val bridgeId = _state.value.candidates
            .firstOrNull { it.host == pending.endpoint.host && it.port == pending.endpoint.port }
            ?.bridgeId

        pair { pairing.pairByDiscovery(pending.confirmedByUser(), bridgeId ?: pending.endpoint.host) }
    }

    /** Pairs using a scanned or pasted pairing code. */
    fun submitPairingCode(raw: String) {
        val payload = try {
            PairingQrPayload.parse(raw)
        } catch (e: PairingQrException) {
            fail(ConnectionProblem.Kind.InvalidCode, e.message)
            return
        }

        pair { pairing.pairByQr(payload) }
    }

    /** Forgets the Bridge and its credential. */
    fun unpair() {
        store.clear()

        _state.value = ConnectionUiState(
            phase = ConnectionPhase.Idle,
            pairedBridgeName = null,
            pairedIdentityCode = null,
            candidates = emptyList(),
            pendingIdentity = null,
            lanPermission = _state.value.lanPermission,
            problem = null,
            message = null,
        )
    }

    /** Clears a problem the user has read. */
    fun dismissProblem() {
        _state.value = _state.value.copy(problem = null)
    }

    private fun pair(attempt: suspend () -> PairingOutcome) {
        _state.value = _state.value.copy(phase = ConnectionPhase.Pairing, problem = null)

        scope.launch {
            try {
                when (val outcome = attempt()) {
                    is PairingOutcome.Paired -> storePaired(outcome)
                    is PairingOutcome.Rejected -> fail(ConnectionProblem.Kind.Rejected, outcome.verificationCode)
                    is PairingOutcome.Expired -> fail(ConnectionProblem.Kind.Expired, outcome.message)
                    is PairingOutcome.TimedOut -> fail(ConnectionProblem.Kind.TimedOut, outcome.verificationCode)
                }
            } catch (e: IdentityMismatchException) {
                // Nothing was sent: the TLS handshake failed before any request existed.
                fail(ConnectionProblem.Kind.Security, e.message)
            } catch (e: Exception) {
                fail(ConnectionProblem.Kind.Unreachable, e.message)
            }
        }
    }

    private fun storePaired(outcome: PairingOutcome.Paired) {
        val bridge = PairedBridge(
            bridgeId = outcome.bridgeId,
            displayName = displayName,
            identityCertificateDer = outcome.identityCertificateDer,
            identityFingerprint = outcome.identityFingerprint,
            endpoint = outcome.endpoint,
            credential = store.sealCredential(outcome.credential.token.toByteArray(Charsets.UTF_8)),
        )

        store.save(bridge)

        _state.value = _state.value.copy(
            phase = ConnectionPhase.Paired,
            pairedBridgeName = bridge.displayName,
            pairedIdentityCode = identityCodeOf(bridge),
            pendingIdentity = null,
            problem = null,
            message = null,
        )
    }

    private fun fail(kind: ConnectionProblem.Kind, detail: String?) {
        _state.value = _state.value.copy(
            phase = ConnectionPhase.Idle,
            problem = ConnectionProblem(kind, detail),
            message = null,
        )
    }

    private fun identityCodeOf(bridge: PairedBridge): String =
        com.codexquota.app.data.security.BridgeIdentity.humanVerificationCode(bridge.identityFingerprint)

    /** The endpoint a candidate advertises, or `null` when it is unusable. */
    private fun DiscoveredService.endpointOrNull(): BridgeEndpoint? = endpoint
}
