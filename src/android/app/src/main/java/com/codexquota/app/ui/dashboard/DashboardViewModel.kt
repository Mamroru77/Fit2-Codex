package com.codexquota.app.ui.dashboard

import com.codexquota.app.data.CachedQuotaState
import com.codexquota.app.data.QuotaRepository
import com.codexquota.app.domain.ConnectionState
import com.codexquota.app.domain.QuotaSourceStatus
import com.codexquota.app.sync.ConnectionManager
import java.time.Instant
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.flow.stateIn

/** How fresh the numbers on screen are, as a distinct state rather than a colour. */
enum class Freshness {
    /** A live connection produced this. */
    RealTime,

    /** A retry is scheduled. */
    Reconnecting,

    /** Not reachable; these are the last trusted values. */
    OfflineCached,

    /** The Bridge is reachable but Codex needs a login. */
    LoginRequired,

    /** The Bridge identity does not match. Nothing here is being refreshed. */
    SecurityError,
}

/**
 * The text the dashboard needs.
 *
 * It is an interface so the presentation rules can be tested as plain strings, and so the Android
 * layer can supply localised resources without the ViewModel knowing about resources at all.
 */
interface DashboardText {
    val realTime: String
    val reconnecting: String
    val offlineCached: String
    val loginRequired: String
    val securityError: String

    /** The label for a value that has never been received. */
    val noData: String

    fun lastUpdated(localTime: String): String
    fun resetsAt(localTime: String): String
    fun usedPercent(value: String): String
}

/** The English defaults, and what the tests assert against. */
open class EnglishDashboardText : DashboardText {
    override val realTime = "Real-time"
    override val reconnecting = "Reconnecting"
    override val offlineCached = "Offline / cached"
    override val loginRequired = "Login required"
    override val securityError = "Security error"
    override val noData = "No data yet"

    override fun lastUpdated(localTime: String) = "Last update $localTime"
    override fun resetsAt(localTime: String) = "Resets $localTime"
    override fun usedPercent(value: String) = "used $value"
}

/**
 * Everything the dashboard draws.
 *
 * Percentages are rendered here rather than in Compose so that "what the user reads" is a tested
 * value. The connection state is always textual as well as visual, because a colour alone cannot say
 * "security error" and must never be read as "offline".
 */
data class DashboardUiState(
    val shortRemaining: String,
    val weeklyRemaining: String,
    val shortUsed: String?,
    val weeklyUsed: String?,
    val shortResetsAt: String?,
    val weeklyResetsAt: String?,
    val freshness: Freshness,
    val connectionText: String,
    val lastUpdated: String?,
    val hasData: Boolean,
    val detail: String?,
)

/**
 * The dashboard's state.
 *
 * It observes exactly two things — the canonical connection state and the cached quota — and derives
 * everything from them. It owns no socket, no worker and no notification.
 */
class DashboardViewModel(
    private val repository: QuotaRepository,
    private val connection: ConnectionManager,
    scope: CoroutineScope,
    private val text: DashboardText = EnglishDashboardText(),
    private val zone: ZoneId = ZoneId.systemDefault(),
    private val clockFormat: DateTimeFormatter = DateTimeFormatter.ofPattern("HH:mm"),
) {
    /** The dashboard state. */
    val state: StateFlow<DashboardUiState> =
        combine(connection.state, repository.cachedState) { connectionState, cached ->
            present(connectionState, cached)
        }.stateIn(
            scope = scope,
            started = SharingStarted.Eagerly,
            initialValue = DashboardUiState(
                shortRemaining = "--",
                weeklyRemaining = "--",
                shortUsed = null,
                weeklyUsed = null,
                shortResetsAt = null,
                weeklyResetsAt = null,
                freshness = Freshness.OfflineCached,
                connectionText = text.noData,
                lastUpdated = null,
                hasData = false,
                detail = null,
            ),
        )

    private fun present(connectionState: ConnectionState, cached: CachedQuotaState): DashboardUiState {
        val snapshot = cached.snapshot

        return DashboardUiState(
            shortRemaining = snapshot?.let { percent(it.shortWindow.remainingPercent) } ?: "--",
            weeklyRemaining = snapshot?.let { percent(it.weekly.remainingPercent) } ?: "--",
            shortUsed = snapshot?.let { text.usedPercent(percent(it.shortWindow.usedPercent)) },
            weeklyUsed = snapshot?.let { text.usedPercent(percent(it.weekly.usedPercent)) },
            shortResetsAt = snapshot?.let { localTime(it.shortWindow.resetsAt) },
            weeklyResetsAt = snapshot?.let { localTime(it.weekly.resetsAt) },
            freshness = freshnessOf(connectionState, cached),
            connectionText = connectionText(connectionState, cached),
            lastUpdated = cached.receivedAt?.let { text.lastUpdated(localTime(it)) },
            hasData = snapshot != null,
            detail = (connectionState as? ConnectionState.SecurityError)?.reason,
        )
    }

    /**
     * The freshness label.
     *
     * The order matters: a security error is never "offline", and a login requirement is never
     * "reconnecting", because those need different things from the user.
     */
    private fun freshnessOf(connectionState: ConnectionState, cached: CachedQuotaState): Freshness =
        when (connectionState) {
            is ConnectionState.SecurityError -> Freshness.SecurityError
            ConnectionState.RepairRequired -> Freshness.SecurityError
            ConnectionState.AuthRequired -> Freshness.LoginRequired

            is ConnectionState.Connected ->
                // A live connection is only "real-time" if the numbers on screen are both current
                // for this process *and* the Bridge's own source says they describe the present.
                // A connected Bridge reporting `stale` is still stale.
                if (cached.stale || cached.snapshot?.status != QuotaSourceStatus.Online) {
                    Freshness.OfflineCached
                } else {
                    Freshness.RealTime
                }

            is ConnectionState.Reconnecting -> Freshness.Reconnecting

            ConnectionState.Discovering,
            ConnectionState.Connecting,
            ConnectionState.Unpaired,
            is ConnectionState.OfflineCached,
            -> Freshness.OfflineCached
        }

    private fun connectionText(connectionState: ConnectionState, cached: CachedQuotaState): String =
        when (connectionState) {
            is ConnectionState.SecurityError -> text.securityError
            ConnectionState.RepairRequired -> text.securityError
            ConnectionState.AuthRequired -> text.loginRequired

            is ConnectionState.Connected ->
                if (cached.stale || cached.snapshot?.status != QuotaSourceStatus.Online) {
                    text.offlineCached
                } else {
                    text.realTime
                }

            is ConnectionState.Reconnecting -> text.reconnecting

            // Not paired, but there is still something cached to show; saying "no data yet" would
            // be untrue.
            ConnectionState.Unpaired -> if (cached.hasData) text.offlineCached else text.noData

            ConnectionState.Discovering,
            ConnectionState.Connecting,
            is ConnectionState.OfflineCached,
            -> if (cached.hasData) text.offlineCached else text.noData
        }

    /** Renders a percentage without a fractional part when there is none. */
    private fun percent(value: Double): String {
        val rounded = kotlin.math.round(value)

        return if (kotlin.math.abs(value - rounded) < 0.05) {
            "${rounded.toInt()}%"
        } else {
            "${(kotlin.math.round(value * 10) / 10)}%"
        }
    }

    /** Renders a UTC instant in the phone's own time zone. */
    private fun localTime(instant: Instant): String = clockFormat.format(instant.atZone(zone))
}

