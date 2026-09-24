package com.codexquota.app.data

import com.codexquota.app.data.api.ApiErrorCodes
import com.codexquota.app.data.api.DataProtocolException
import com.codexquota.app.data.api.DeviceUnauthorizedException
import com.codexquota.app.data.api.ProtocolException
import com.codexquota.app.data.pairing.IdentityMismatchException
import com.codexquota.app.domain.HistoryPoint
import com.codexquota.app.domain.QuotaSourceStatus
import com.codexquota.app.testing.FakeBridgeApi
import com.codexquota.app.testing.InMemoryQuotaCache
import com.codexquota.app.testing.offline
import com.codexquota.app.testing.snapshot
import java.time.Instant
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertIs
import kotlin.test.assertTrue
import kotlinx.coroutines.test.runTest

/**
 * The repository is where "what the app shows" is decided, so these are the rules that keep the app
 * from inventing data or losing data it already trusts.
 */
class QuotaRepositoryTest {

    private val api = FakeBridgeApi()
    private val cache = InMemoryQuotaCache()
    private val now = Instant.parse("2026-09-22T13:31:00Z")
    private val repository = QuotaRepository(api, cache) { now }

    @Test
    fun aFreshFetchIsWrittenAndReportedAsUpdated() = runTest {
        val result = repository.refreshCurrent()

        assertIs<RefreshResult.Updated>(result)
        assertEquals(72.0, result.snapshot.shortWindow.remainingPercent)

        val cached = repository.current()
        assertEquals(72.0, cached.snapshot?.shortWindow?.remainingPercent)
        assertFalse(cached.stale, "a successful fetch must not be stored as stale")
    }

    @Test
    fun theSameSnapshotAgainIsUnchangedAndDoesNotReDownloadAnythingElse() = runTest {
        repository.refreshCurrent()

        val result = repository.refreshCurrent()

        assertIs<RefreshResult.Unchanged>(result)
        assertEquals(2, api.quotaCalls)
    }

    @Test
    fun aNetworkFailureKeepsTheCachedValuesAndMarksThemStale() = runTest {
        repository.refreshCurrent()
        api.quota = { throw offline() }

        val result = repository.refreshCurrent()

        assertIs<RefreshResult.Offline>(result)
        assertEquals(72.0, result.cached?.shortWindow?.remainingPercent)

        val cached = repository.current()
        assertEquals(72.0, cached.snapshot?.shortWindow?.remainingPercent)
        assertTrue(cached.stale, "unreachable data must be labelled stale")
    }

    @Test
    fun aProtocolErrorPreservesThePreviousTrustedCache() = runTest {
        repository.refreshCurrent()
        api.quota = { throw DataProtocolException("remainingPercent is missing") }

        val result = repository.refreshCurrent()

        assertIs<RefreshResult.ProtocolError>(result)
        assertTrue(result.reason.contains("remainingPercent"))

        // The point of the whole exercise: the numbers that were trusted stay, and nothing is
        // invented to fill the gap.
        val cached = repository.current()
        assertEquals(72.0, cached.snapshot?.shortWindow?.remainingPercent)
        assertEquals(54.0, cached.snapshot?.weekly?.remainingPercent)
        assertTrue(cached.stale)
    }

    @Test
    fun aRejectedCredentialAsksForRepair() = runTest {
        api.quota = { throw DeviceUnauthorizedException("not paired") }

        val result = repository.refreshCurrent()

        assertEquals(RefreshResult.RepairRequired, result)
    }

    @Test
    fun anAuthRequiredSourceKeepsTheCachedQuota() = runTest {
        repository.refreshCurrent()
        api.quota = { throw ProtocolException(ApiErrorCodes.CODEX_AUTH_REQUIRED, "login required") }

        val result = repository.refreshCurrent()

        assertIs<RefreshResult.SourceAuthRequired>(result)
        assertEquals(72.0, result.cached?.shortWindow?.remainingPercent)
        assertEquals(72.0, repository.current().snapshot?.shortWindow?.remainingPercent)
    }

    @Test
    fun anUnsupportedSourceSchemaIsReportedAsSourceUnavailable() = runTest {
        api.quota = { throw ProtocolException(ApiErrorCodes.SOURCE_SCHEMA_UNSUPPORTED, "cannot identify windows") }

        val result = repository.refreshCurrent()

        assertIs<RefreshResult.SourceUnavailable>(result)
        assertEquals(ApiErrorCodes.SOURCE_SCHEMA_UNSUPPORTED, result.code)
    }

    @Test
    fun anIdentityMismatchIsASecurityErrorAndNotAnOfflineState() = runTest {
        repository.refreshCurrent()
        api.quota = { throw IdentityMismatchException("identity changed") }

        val result = repository.refreshCurrent()

        assertIs<RefreshResult.SecurityError>(result)
        // The cached numbers stay readable, but they are no longer being refreshed.
        assertEquals(72.0, repository.current().snapshot?.shortWindow?.remainingPercent)
        assertTrue(repository.current().stale)
    }

    @Test
    fun aRestartedAppShowsRestoredDataAsStaleUntilAFreshFetchSucceeds() = runTest {
        repository.refreshCurrent()
        assertFalse(repository.current().stale)

        // A new process over the same storage: the values are there, but nothing has proved them
        // current for this process yet.
        val restarted = QuotaRepository(api, cache) { now }
        val restored = restarted.restoreFromCache()

        assertEquals(72.0, restored.snapshot?.shortWindow?.remainingPercent)
        assertTrue(restored.stale, "restored data must never be presented as real-time")

        // The Bridge re-sent the same snapshot, so it is reported as unchanged rather than as a new
        // value; what matters is that it is now proven current for this process.
        val refreshed = restarted.refreshCurrent()
        assertIs<RefreshResult.Unchanged>(refreshed)
        assertFalse(restarted.current().stale)
    }

    @Test
    fun aHistoryFailureNeverTouchesTheCurrentSnapshot() = runTest {
        repository.refreshCurrent()
        api.history = { throw offline() }

        val result = repository.refreshHistory()

        assertIs<HistoryRefreshResult.Offline>(result)
        assertEquals(72.0, repository.current().snapshot?.shortWindow?.remainingPercent)
        assertFalse(repository.current().stale, "a history failure is not a current-quota failure")
    }

    @Test
    fun aHistorySuccessReplacesTheStoredSeries() = runTest {
        val points = listOf(
            HistoryPoint(Instant.parse("2026-09-22T12:15:00Z"), 81.0, 60.0),
            HistoryPoint(Instant.parse("2026-09-22T13:30:00Z"), 72.0, 54.0),
        )
        api.history = { points }

        val result = repository.refreshHistory()

        assertIs<HistoryRefreshResult.Updated>(result)
        assertEquals(2, repository.history().size)
    }

    @Test
    fun historyUnavailableIsReportedWithoutClearingWhatIsStored() = runTest {
        api.history = { listOf(HistoryPoint(Instant.parse("2026-09-22T12:15:00Z"), 81.0, 60.0)) }
        repository.refreshHistory()
        api.history = { throw ProtocolException(ApiErrorCodes.HISTORY_UNAVAILABLE, "history is down") }

        val result = repository.refreshHistory()

        assertEquals(HistoryRefreshResult.Unavailable, result)
        assertEquals(1, repository.history().size)
    }

    @Test
    fun anOutOfRangeWindowIsRefusedRatherThanSent() = runTest {
        listOf(0, 25, -1).forEach { hours ->
            val failure = runCatching { repository.refreshHistory(hours) }.exceptionOrNull()

            assertIs<IllegalArgumentException>(failure, "hours=$hours should have been refused")
        }
    }

    @Test
    fun aRealtimeFrameIsWrittenAsFresh() = runTest {
        repository.applyRealtime(snapshot(shortRemaining = 8.0))

        val cached = repository.current()
        assertEquals(8.0, cached.snapshot?.shortWindow?.remainingPercent)
        assertFalse(cached.stale)
    }

    @Test
    fun aSnapshotThatIsNotOnlineIsNotFreshEnoughToAlert() {
        assertTrue(QuotaRepository.isFresh(snapshot(status = QuotaSourceStatus.Online)))
        assertFalse(QuotaRepository.isFresh(snapshot(status = QuotaSourceStatus.Stale)))
        assertFalse(QuotaRepository.isFresh(snapshot(status = QuotaSourceStatus.Unavailable)))
        assertFalse(QuotaRepository.isFresh(snapshot(status = QuotaSourceStatus.SourceError)))
    }
}
