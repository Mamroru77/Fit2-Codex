package com.codexquota.app.data.db

import androidx.room.Dao
import androidx.room.Insert
import androidx.room.OnConflictStrategy
import androidx.room.Query
import androidx.room.Transaction
import kotlinx.coroutines.flow.Flow

/**
 * The stored quota, history and events.
 *
 * This is an abstract class rather than an interface on purpose: the transactional replace methods
 * have bodies, and Room only supports those reliably on an abstract class.
 */
@Dao
abstract class QuotaDao {

    /** The cached current snapshot, observed. */
    @Query("SELECT * FROM cached_quota WHERE id = ${CachedQuotaEntity.SINGLETON_ID}")
    abstract fun observeCurrent(): Flow<CachedQuotaEntity?>

    /** The cached current snapshot. */
    @Query("SELECT * FROM cached_quota WHERE id = ${CachedQuotaEntity.SINGLETON_ID}")
    abstract suspend fun readCurrent(): CachedQuotaEntity?

    /** Writes the current snapshot, replacing whatever was there. */
    @Insert(onConflict = OnConflictStrategy.REPLACE)
    abstract suspend fun writeCurrent(entity: CachedQuotaEntity)

    /** Marks the current snapshot as no longer proven current, leaving its values alone. */
    @Query("UPDATE cached_quota SET stale = 1 WHERE id = ${CachedQuotaEntity.SINGLETON_ID}")
    abstract suspend fun markStale()

    /** The stored history, oldest first. */
    @Query("SELECT * FROM quota_history ORDER BY timestamp ASC")
    abstract suspend fun readHistory(): List<HistoryPointEntity>

    /** The stored history, observed. */
    @Query("SELECT * FROM quota_history ORDER BY timestamp ASC")
    abstract fun observeHistory(): Flow<List<HistoryPointEntity>>

    @Insert(onConflict = OnConflictStrategy.REPLACE)
    abstract suspend fun insertHistory(points: List<HistoryPointEntity>)

    @Query("DELETE FROM quota_history")
    abstract suspend fun deleteHistory()

    /**
     * Replaces the whole series.
     *
     * The Bridge is authoritative for history, so a refresh replaces rather than merges: merging
     * would keep samples the Bridge has since dropped and slowly diverge from it.
     */
    @Transaction
    open suspend fun replaceHistory(points: List<HistoryPointEntity>) {
        deleteHistory()
        insertHistory(points)
    }

    /** The stored events, newest first. */
    @Query("SELECT * FROM quota_events ORDER BY occurredAt DESC, id DESC")
    abstract suspend fun readEvents(): List<QuotaEventEntity>

    @Query("SELECT * FROM quota_events ORDER BY occurredAt DESC, id DESC")
    abstract fun observeEvents(): Flow<List<QuotaEventEntity>>

    @Insert(onConflict = OnConflictStrategy.REPLACE)
    abstract suspend fun insertEvents(events: List<QuotaEventEntity>)

    @Query("DELETE FROM quota_events")
    abstract suspend fun deleteEvents()

    /** Replaces the whole event list, for the same reason history is replaced. */
    @Transaction
    open suspend fun replaceEvents(events: List<QuotaEventEntity>) {
        deleteEvents()
        insertEvents(events)
    }
}
