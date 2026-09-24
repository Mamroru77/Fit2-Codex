package com.codexquota.app.data.db

import android.content.Context
import androidx.room.Database
import androidx.room.Room
import androidx.room.RoomDatabase

/**
 * The app's local cache.
 *
 * The Bridge is authoritative for everything stored here, so this database is a cache and never a
 * source of truth. That is why losing it is survivable: the app re-fetches, and in the meantime it
 * shows nothing rather than something invented.
 *
 * The alert state is the one exception to "the Bridge is authoritative": thresholds and trigger
 * state are the phone's own, and they are kept here so a restart does not replay a notification the
 * user has already seen.
 */
@Database(
    entities = [
        CachedQuotaEntity::class,
        HistoryPointEntity::class,
        QuotaEventEntity::class,
        AlertStateEntity::class,
    ],
    version = 1,
    exportSchema = false,
)
abstract class AppDatabase : RoomDatabase() {

    /** The quota, history and event queries. */
    abstract fun quotaDao(): QuotaDao

    /** The alert-state queries. */
    abstract fun alertStateDao(): AlertStateDao

    companion object {
        private const val NAME = "codexquota.db"

        /** Opens the database in the app's own storage. */
        fun create(context: Context): AppDatabase =
            Room.databaseBuilder(context.applicationContext, AppDatabase::class.java, NAME)
                // V1 has no released schema, so there is nothing to migrate from yet. A destructive
                // migration is honest for a cache: the data can always be re-fetched, and pretending
                // to migrate a schema nobody has shipped would be worse than dropping it.
                .fallbackToDestructiveMigration(dropAllTables = true)
                .build()
    }
}
