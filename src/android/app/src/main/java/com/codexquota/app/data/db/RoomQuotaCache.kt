package com.codexquota.app.data.db

import com.codexquota.app.data.CachedQuotaState
import com.codexquota.app.data.QuotaCache
import com.codexquota.app.domain.HistoryPoint
import com.codexquota.app.domain.QuotaEvent
import com.codexquota.app.domain.QuotaEventType
import com.codexquota.app.domain.QuotaSnapshot
import com.codexquota.app.domain.QuotaSourceStatus
import com.codexquota.app.domain.QuotaWindow
import java.time.Instant
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.map

/**
 * The Room-backed cache.
 *
 * It is a translation layer and nothing more: every decision about what the data means lives in
 * `QuotaRepository`, so this class has no rules to get wrong. Timestamps are stored as UTC epoch
 * milliseconds and converted at the boundary, which keeps comparisons and ordering unambiguous.
 */
class RoomQuotaCache(private val dao: QuotaDao) : QuotaCache {

    override fun observe(): Flow<CachedQuotaState> =
        dao.observeCurrent().map { it?.toCachedState() ?: CachedQuotaState.Empty }

    override suspend fun read(): CachedQuotaState = dao.readCurrent()?.toCachedState() ?: CachedQuotaState.Empty

    override suspend fun writeCurrent(snapshot: QuotaSnapshot, receivedAt: Instant) {
        dao.writeCurrent(snapshot.toEntity(receivedAt, stale = false))
    }

    override suspend fun markStale() = dao.markStale()

    override suspend fun replaceHistory(points: List<HistoryPoint>) {
        dao.replaceHistory(points.map { it.toEntity() })
    }

    override suspend fun readHistory(): List<HistoryPoint> = dao.readHistory().map { it.toDomain() }

    override suspend fun replaceEvents(events: List<QuotaEvent>) {
        dao.replaceEvents(events.map { it.toEntity() })
    }

    override suspend fun readEvents(): List<QuotaEvent> = dao.readEvents().mapNotNull { it.toDomainOrNull() }
}

// --- mapping -----------------------------------------------------------------------------------

private fun QuotaSnapshot.toEntity(receivedAt: Instant, stale: Boolean) = CachedQuotaEntity(
    schemaVersion = schemaVersion,
    generatedAt = generatedAt.toEpochMilli(),
    source = source,
    status = status.toWire(),
    lastSuccessfulSyncAt = lastSuccessfulSyncAt?.toEpochMilli(),
    shortUsedPercent = shortWindow.usedPercent,
    shortRemainingPercent = shortWindow.remainingPercent,
    shortWindowMinutes = shortWindow.windowMinutes,
    shortResetsAt = shortWindow.resetsAt.toEpochMilli(),
    weeklyUsedPercent = weekly.usedPercent,
    weeklyRemainingPercent = weekly.remainingPercent,
    weeklyWindowMinutes = weekly.windowMinutes,
    weeklyResetsAt = weekly.resetsAt.toEpochMilli(),
    receivedAt = receivedAt.toEpochMilli(),
    stale = stale,
)

private fun CachedQuotaEntity.toCachedState(): CachedQuotaState = CachedQuotaState(
    snapshot = runCatching {
        QuotaSnapshot(
            schemaVersion = schemaVersion,
            generatedAt = Instant.ofEpochMilli(generatedAt),
            source = source,
            status = status.toStatus(),
            lastSuccessfulSyncAt = lastSuccessfulSyncAt?.let(Instant::ofEpochMilli),
            shortWindow = QuotaWindow(
                usedPercent = shortUsedPercent,
                remainingPercent = shortRemainingPercent,
                windowMinutes = shortWindowMinutes,
                resetsAt = Instant.ofEpochMilli(shortResetsAt),
            ),
            weekly = QuotaWindow(
                usedPercent = weeklyUsedPercent,
                remainingPercent = weeklyRemainingPercent,
                windowMinutes = weeklyWindowMinutes,
                resetsAt = Instant.ofEpochMilli(weeklyResetsAt),
            ),
        )
    }.getOrNull(),
    receivedAt = Instant.ofEpochMilli(receivedAt),
    stale = stale,
)

private fun HistoryPoint.toEntity() = HistoryPointEntity(
    timestamp = timestamp.toEpochMilli(),
    shortWindowRemainingPercent = shortWindowRemainingPercent,
    weeklyRemainingPercent = weeklyRemainingPercent,
)

private fun HistoryPointEntity.toDomain() = HistoryPoint(
    timestamp = Instant.ofEpochMilli(timestamp),
    shortWindowRemainingPercent = shortWindowRemainingPercent,
    weeklyRemainingPercent = weeklyRemainingPercent,
)

private fun QuotaEvent.toEntity() = QuotaEventEntity(
    type = type.toWire(),
    occurredAt = occurredAt.toEpochMilli(),
    detail = detail,
)

private fun QuotaEventEntity.toDomainOrNull(): QuotaEvent? {
    val eventType = QuotaEventType.fromWire(type) ?: return null

    return QuotaEvent(
        type = eventType,
        occurredAt = Instant.ofEpochMilli(occurredAt),
        detail = detail,
    )
}

/** The wire name of a source status. */
internal fun QuotaSourceStatus.toWire(): String = when (this) {
    QuotaSourceStatus.Online -> "online"
    QuotaSourceStatus.Stale -> "stale"
    QuotaSourceStatus.Unavailable -> "unavailable"
    QuotaSourceStatus.AuthRequired -> "auth_required"
    QuotaSourceStatus.SourceError -> "source_error"
    QuotaSourceStatus.SourceSchemaUnsupported -> "source_schema_unsupported"
}

/** The source status named by a wire value. */
internal fun String.toStatus(): QuotaSourceStatus = when (this) {
    "online" -> QuotaSourceStatus.Online
    "stale" -> QuotaSourceStatus.Stale
    "unavailable" -> QuotaSourceStatus.Unavailable
    "auth_required" -> QuotaSourceStatus.AuthRequired
    "source_error" -> QuotaSourceStatus.SourceError
    "source_schema_unsupported" -> QuotaSourceStatus.SourceSchemaUnsupported
    // A status this build does not know is not "online": treating it as current would let an
    // unrecognised source state raise alerts.
    else -> QuotaSourceStatus.SourceError
}

/** The wire name of a user-meaningful event type. */
internal fun QuotaEventType.toWire(): String = when (this) {
    QuotaEventType.QuotaChanged -> "quota_changed"
    QuotaEventType.WindowReset -> "window_reset"
    QuotaEventType.BridgeStarted -> "bridge_started"
    QuotaEventType.BridgeStopped -> "bridge_stopped"
    QuotaEventType.CodexConnected -> "codex_connected"
    QuotaEventType.CodexDisconnected -> "codex_disconnected"
    QuotaEventType.AuthRequired -> "auth_required"
    QuotaEventType.SourceError -> "source_error"
}
