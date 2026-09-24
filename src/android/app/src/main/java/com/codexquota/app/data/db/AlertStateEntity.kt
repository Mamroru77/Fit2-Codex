package com.codexquota.app.data.db

import androidx.room.Dao
import androidx.room.Entity
import androidx.room.Insert
import androidx.room.OnConflictStrategy
import androidx.room.PrimaryKey
import androidx.room.Query
import com.codexquota.app.domain.alerts.AlertState
import com.codexquota.app.domain.alerts.WindowAlertState
import com.codexquota.app.domain.alerts.WindowTriggerState
import java.time.Instant

/**
 * The persisted alert state.
 *
 * It is stored in the same database as the cache rather than in DataStore because it changes with
 * every snapshot: it is state, not a preference. One row holds both windows, so an evaluation writes
 * them together and cannot leave the two windows describing different moments.
 */
@Entity(tableName = "alert_state")
data class AlertStateEntity(
    @PrimaryKey val id: Int = SINGLETON_ID,
    val shortState: String,
    val shortLastCriticalAt: Long?,
    val weeklyState: String,
    val weeklyLastCriticalAt: Long?,
    val shortResetsAt: Long?,
    val weeklyResetsAt: Long?,
) {
    companion object {
        /** The only key the alert state is stored under. */
        const val SINGLETON_ID = 0
    }
}

/** Reads and writes the alert state. */
@Dao
abstract class AlertStateDao {

    @Query("SELECT * FROM alert_state WHERE id = ${AlertStateEntity.SINGLETON_ID}")
    abstract suspend fun read(): AlertStateEntity?

    @Insert(onConflict = OnConflictStrategy.REPLACE)
    abstract suspend fun write(entity: AlertStateEntity)
}

/** The stored alert state, as the domain sees it. */
fun AlertStateEntity.toDomain(): AlertState = AlertState(
    shortWindow = WindowAlertState(shortState.toTriggerState(), shortLastCriticalAt?.let(Instant::ofEpochMilli)),
    weekly = WindowAlertState(weeklyState.toTriggerState(), weeklyLastCriticalAt?.let(Instant::ofEpochMilli)),
    shortWindowResetsAt = shortResetsAt?.let(Instant::ofEpochMilli),
    weeklyResetsAt = weeklyResetsAt?.let(Instant::ofEpochMilli),
)

/** The storable form of the alert state. */
fun AlertState.toEntity(): AlertStateEntity = AlertStateEntity(
    shortState = shortWindow.state.name,
    shortLastCriticalAt = shortWindow.lastCriticalNotificationAt?.toEpochMilli(),
    weeklyState = weekly.state.name,
    weeklyLastCriticalAt = weekly.lastCriticalNotificationAt?.toEpochMilli(),
    shortResetsAt = shortWindowResetsAt?.toEpochMilli(),
    weeklyResetsAt = weeklyResetsAt?.toEpochMilli(),
)

private fun String.toTriggerState(): WindowTriggerState =
    WindowTriggerState.entries.firstOrNull { it.name == this } ?: WindowTriggerState.Normal
