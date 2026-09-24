package com.codexquota.app.data.api

import com.codexquota.app.domain.HistoryPoint
import com.codexquota.app.domain.QuotaEvent
import com.codexquota.app.domain.QuotaSnapshot

/**
 * The Bridge's read-only v1 API.
 *
 * It is an interface so the repository's behaviour — what is cached, what is marked stale, what
 * survives a protocol error — can be tested without a server.
 */
interface BridgeApi {

    /** The current quota snapshot. */
    suspend fun fetchQuota(): QuotaSnapshot

    /** The stored history for the last [hours] hours. */
    suspend fun fetchHistory(hours: Int = DEFAULT_HOURS): List<HistoryPoint>

    /** The user-meaningful events of the last [hours] hours. */
    suspend fun fetchEvents(hours: Int = DEFAULT_HOURS): List<QuotaEvent>

    companion object {
        /** The v1 default and the maximum the Bridge serves. */
        const val DEFAULT_HOURS = 24

        /** The bounds the Bridge validates `hours` against. */
        val VALID_HOURS = 1..24
    }
}
