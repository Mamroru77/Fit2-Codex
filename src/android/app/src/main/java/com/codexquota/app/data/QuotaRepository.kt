package com.codexquota.app.data

import com.codexquota.app.data.api.ApiErrorCodes
import com.codexquota.app.data.api.BridgeApi
import com.codexquota.app.data.api.DataProtocolException
import com.codexquota.app.data.api.DeviceUnauthorizedException
import com.codexquota.app.data.api.ProtocolException
import com.codexquota.app.data.api.protocolFailure
import com.codexquota.app.data.pairing.IdentityMismatchException
import com.codexquota.app.domain.HistoryPoint
import com.codexquota.app.domain.QuotaEvent
import com.codexquota.app.domain.QuotaSnapshot
import com.codexquota.app.domain.QuotaSourceStatus
import java.io.IOException
import java.time.Instant
import kotlinx.coroutines.flow.Flow

/**
 * The quota the app has stored, with the freshness metadata needed to describe it honestly.
 *
 * [stale] is not a guess about the numbers: it says whether the last attempt to reach the Bridge
 * succeeded. Cached values are shown, and are never presented as real-time data.
 */
data class CachedQuotaState(
    val snapshot: QuotaSnapshot?,
    val receivedAt: Instant?,
    val stale: Boolean,
) {
    /** Whether there is anything to show at all. */
    val hasData: Boolean get() = snapshot != null

    companion object {
        /** The state before anything has ever been cached. */
        val Empty = CachedQuotaState(snapshot = null, receivedAt = null, stale = true)
    }
}

/** What a refresh attempt achieved. */
sealed interface RefreshResult {
    /** A new trusted snapshot replaced the cached one. */
    data class Updated(val snapshot: QuotaSnapshot) : RefreshResult

    /** The Bridge reported the same snapshot again. The cache is unchanged but still current. */
    data class Unchanged(val snapshot: QuotaSnapshot) : RefreshResult

    /** The Bridge could not be reached. Cached values stay, and are marked stale. */
    data class Offline(val cached: QuotaSnapshot?) : RefreshResult

    /** The Bridge is reachable but Codex needs a login. Cached quota is kept. */
    data class SourceAuthRequired(val cached: QuotaSnapshot?) : RefreshResult

    /** The Bridge is reachable but cannot serve rate-limit data right now. */
    data class SourceUnavailable(val code: String, val cached: QuotaSnapshot?) : RefreshResult

    /** The device credential was rejected: the phone has to pair again. */
    data object RepairRequired : RefreshResult

    /** The Bridge's identity is not the pinned one. Terminal until the user acts. */
    data class SecurityError(val reason: String) : RefreshResult

    /** The payload was not a valid v1 document. The previous trusted cache is preserved. */
    data class ProtocolError(val reason: String, val cached: QuotaSnapshot?) : RefreshResult
}

/** What a history or events refresh achieved. */
sealed interface HistoryRefreshResult {
    /** The stored series was replaced. */
    data class Updated(val points: List<HistoryPoint>) : HistoryRefreshResult

    /** The Bridge has no history to serve. Cached history stays visible. */
    data object Unavailable : HistoryRefreshResult

    /** The Bridge could not be reached. */
    data object Offline : HistoryRefreshResult

    /** The payload was not a valid v1 document. */
    data class ProtocolError(val reason: String) : HistoryRefreshResult
}

/**
 * The stored quota, history and events.
 *
 * An interface so the repository's rules can be tested against an in-memory implementation, and so
 * the Room-backed one stays a thin translation of these operations into SQL.
 */
interface QuotaCache {
    /** The stored current snapshot with its freshness, observed as it changes. */
    fun observe(): Flow<CachedQuotaState>

    /** The stored current snapshot with its freshness. */
    suspend fun read(): CachedQuotaState

    /** Replaces the current snapshot and marks it current. */
    suspend fun writeCurrent(snapshot: QuotaSnapshot, receivedAt: Instant)

    /** Marks the stored snapshot as no longer proven current. */
    suspend fun markStale()

    /** Replaces the stored history. */
    suspend fun replaceHistory(points: List<HistoryPoint>)

    /** The stored history, oldest first. */
    suspend fun readHistory(): List<HistoryPoint>

    /** Replaces the stored events. */
    suspend fun replaceEvents(events: List<QuotaEvent>)

    /** The stored events, newest first. */
    suspend fun readEvents(): List<QuotaEvent>
}

/**
 * The single place that decides what the app's quota data means.
 *
 * Two rules it exists to enforce:
 *
 * 1. **The current snapshot is authoritative in memory and in the cache; history is secondary.**
 *    A history failure never clears or invalidates the current snapshot.
 * 2. **A failure never invents data.** A protocol error preserves the last trusted snapshot and is
 *    reported as a protocol error, rather than blanking the screen or rendering a zero.
 */
class QuotaRepository(
    private val api: BridgeApi,
    private val cache: QuotaCache,
    private val clock: () -> Instant = Instant::now,
) {
    /** The cached state, observed. */
    val cachedState: Flow<CachedQuotaState> get() = cache.observe()

    /** The cached state, read once. */
    suspend fun current(): CachedQuotaState = cache.read()

    /**
     * Prepares the cache for a newly started process.
     *
     * Data restored from disk was current for the *previous* process, not for this one: nothing has
     * yet proved it still describes the Bridge. Marking it stale here is what stops a phone that was
     * killed overnight from opening on yesterday's numbers labelled as real-time.
     *
     * It is a separate call rather than something done in a constructor because it has to happen
     * before the first read is shown, and only the composition root knows when that is.
     */
    suspend fun restoreFromCache(): CachedQuotaState {
        cache.markStale()
        return cache.read()
    }

    /**
     * Fetches the current snapshot and updates the cache.
     *
     * Nothing is written when the fetch fails: a stale snapshot that is still the last thing the
     * Bridge said is more useful, and more honest, than an empty screen.
     */
    suspend fun refreshCurrent(): RefreshResult {
        val cached = cache.read().snapshot

        return try {
            val snapshot = api.fetchQuota()

            if (cached != null && cached.generatedAt == snapshot.generatedAt) {
                // The Bridge re-sent the same snapshot. It is still current, but rewriting the cache
                // would produce a pointless history sample.
                cache.writeCurrent(snapshot, clock())
                RefreshResult.Unchanged(snapshot)
            } else {
                cache.writeCurrent(snapshot, clock())
                RefreshResult.Updated(snapshot)
            }
        } catch (e: IdentityMismatchException) {
            cache.markStale()
            RefreshResult.SecurityError(e.message ?: "The Bridge identity could not be verified.")
        } catch (e: DeviceUnauthorizedException) {
            cache.markStale()
            RefreshResult.RepairRequired
        } catch (e: ProtocolException) {
            cache.markStale()
            classifyProtocolFailure(e, cached)
        } catch (e: IOException) {
            cache.markStale()
            RefreshResult.Offline(cached)
        }
    }

    /** Fetches the history. A failure here never touches the current snapshot. */
    suspend fun refreshHistory(hours: Int = BridgeApi.DEFAULT_HOURS): HistoryRefreshResult {
        require(hours in BridgeApi.VALID_HOURS) { "hours must be within ${BridgeApi.VALID_HOURS} but was $hours" }

        return try {
            val points = api.fetchHistory(hours)
            cache.replaceHistory(points)
            HistoryRefreshResult.Updated(points)
        } catch (e: ProtocolException) {
            if (e.code == ApiErrorCodes.HISTORY_UNAVAILABLE) {
                HistoryRefreshResult.Unavailable
            } else {
                HistoryRefreshResult.ProtocolError(e.message ?: "History could not be read.")
            }
        } catch (e: IOException) {
            HistoryRefreshResult.Offline
        }
    }

    /** Fetches the events. A failure here never touches the current snapshot. */
    suspend fun refreshEvents(hours: Int = BridgeApi.DEFAULT_HOURS): HistoryRefreshResult {
        require(hours in BridgeApi.VALID_HOURS) { "hours must be within ${BridgeApi.VALID_HOURS} but was $hours" }

        return try {
            val events = api.fetchEvents(hours)
            cache.replaceEvents(events)
            HistoryRefreshResult.Updated(emptyList())
        } catch (e: ProtocolException) {
            if (e.code == ApiErrorCodes.HISTORY_UNAVAILABLE) {
                HistoryRefreshResult.Unavailable
            } else {
                HistoryRefreshResult.ProtocolError(e.message ?: "Events could not be read.")
            }
        } catch (e: IOException) {
            HistoryRefreshResult.Offline
        }
    }

    /** The stored history, oldest first. */
    suspend fun history(): List<HistoryPoint> = cache.readHistory()

    /** The stored events, newest first. */
    suspend fun events(): List<QuotaEvent> = cache.readEvents()

    /** Records that the app knows it is not connected. */
    suspend fun markOffline() = cache.markStale()

    /**
     * Applies a snapshot that arrived over the live connection.
     *
     * A live frame is by definition current, so it is written as fresh. This is the one path that
     * does not go through [refreshCurrent], because the snapshot is already in hand and re-fetching
     * it would be a pointless round trip.
     */
    suspend fun applyRealtime(snapshot: QuotaSnapshot) {
        cache.writeCurrent(snapshot, clock())
    }

    /**
     * Turns a stable error code into the behaviour it implies.
     *
     * Branching on the code, never on the message, is what keeps this correct when the Bridge's
     * wording changes.
     */
    private fun classifyProtocolFailure(e: ProtocolException, cached: QuotaSnapshot?): RefreshResult =
        when (e.code) {
            ApiErrorCodes.DEVICE_UNAUTHORIZED -> RefreshResult.RepairRequired

            ApiErrorCodes.CODEX_AUTH_REQUIRED -> RefreshResult.SourceAuthRequired(cached)

            ApiErrorCodes.CODEX_UNAVAILABLE,
            ApiErrorCodes.RATE_LIMIT_DATA_UNAVAILABLE,
            ApiErrorCodes.SOURCE_SCHEMA_UNSUPPORTED,
            ApiErrorCodes.HISTORY_UNAVAILABLE,
            -> RefreshResult.SourceUnavailable(e.code, cached)

            else -> RefreshResult.ProtocolError(e.message ?: e.code, cached)
        }

    companion object {
        /** The statuses that mean the Bridge's own source cannot currently be trusted as current. */
        val NOT_FRESH: Set<QuotaSourceStatus> =
            setOf(QuotaSourceStatus.Stale, QuotaSourceStatus.Unavailable, QuotaSourceStatus.SourceError)

        /** Whether a snapshot may be used to raise a new alert. */
        fun isFresh(snapshot: QuotaSnapshot): Boolean = snapshot.status == QuotaSourceStatus.Online
    }
}

