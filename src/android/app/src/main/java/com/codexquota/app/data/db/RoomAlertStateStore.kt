package com.codexquota.app.data.db

import com.codexquota.app.alerts.AlertStateStore
import com.codexquota.app.domain.alerts.AlertState

/**
 * The Room-backed alert state.
 *
 * A missing row is the default state rather than an error: a phone that has never evaluated a
 * snapshot has nothing stored, and that is exactly "nothing has been triggered yet".
 */
class RoomAlertStateStore(private val dao: AlertStateDao) : AlertStateStore {

    override suspend fun read(): AlertState = dao.read()?.toDomain() ?: AlertState()

    override suspend fun write(state: AlertState) = dao.write(state.toEntity())
}
