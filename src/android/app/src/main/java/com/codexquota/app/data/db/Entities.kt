package com.codexquota.app.data.db

import androidx.room.Entity
import androidx.room.PrimaryKey

/**
 * The cached current snapshot.
 *
 * Exactly one row exists, under a fixed key, because there is exactly one current snapshot. Storing
 * it as a row rather than a preference keeps it in the same transaction as the history it belongs
 * with, and keeps every timestamp a single well-defined type.
 */
@Entity(tableName = "cached_quota")
data class CachedQuotaEntity(
    @PrimaryKey val id: Int = SINGLETON_ID,
    val schemaVersion: Int,
    /** UTC epoch milliseconds. */
    val generatedAt: Long,
    val source: String,
    val status: String,
    val lastSuccessfulSyncAt: Long?,
    val shortUsedPercent: Double,
    val shortRemainingPercent: Double,
    val shortWindowMinutes: Int,
    val shortResetsAt: Long,
    val weeklyUsedPercent: Double,
    val weeklyRemainingPercent: Double,
    val weeklyWindowMinutes: Int,
    val weeklyResetsAt: Long,
    /** When this phone received it. */
    val receivedAt: Long,
    /** Whether the last attempt to reach the Bridge failed. */
    val stale: Boolean,
) {
    companion object {
        /** The only key the current snapshot is ever stored under. */
        const val SINGLETON_ID = 0
    }
}

/** One stored history sample. The timestamp is the identity of the sample. */
@Entity(tableName = "quota_history")
data class HistoryPointEntity(
    @PrimaryKey val timestamp: Long,
    val shortWindowRemainingPercent: Double,
    val weeklyRemainingPercent: Double,
)

/** One stored user-meaningful event. */
@Entity(tableName = "quota_events")
data class QuotaEventEntity(
    @PrimaryKey(autoGenerate = true) val id: Long = 0,
    val type: String,
    val occurredAt: Long,
    val detail: String?,
)
