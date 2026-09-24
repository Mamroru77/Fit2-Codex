package com.codexquota.app.domain

import java.time.Duration
import java.time.Instant
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertTrue

class QuotaModelsTest {
    private val resetsAt: Instant = Instant.parse("2026-09-22T15:42:00Z")

    @Test
    fun quotaWindowRejectsOutOfRangeRemaining() {
        assertFailsWith<IllegalArgumentException> {
            QuotaWindow(
                usedPercent = 10.0,
                remainingPercent = 101.0,
                windowMinutes = 300,
                resetsAt = Instant.MAX,
            )
        }
    }

    @Test
    fun quotaWindowRejectsNegativeRemaining() {
        assertFailsWith<IllegalArgumentException> {
            QuotaWindow(
                usedPercent = 10.0,
                remainingPercent = -0.5,
                windowMinutes = 300,
                resetsAt = resetsAt,
            )
        }
    }

    @Test
    fun quotaWindowRejectsOutOfRangeUsed() {
        assertFailsWith<IllegalArgumentException> {
            QuotaWindow(
                usedPercent = 100.5,
                remainingPercent = 0.0,
                windowMinutes = 300,
                resetsAt = resetsAt,
            )
        }
    }

    @Test
    fun quotaWindowRejectsNonFinitePercentages() {
        assertFailsWith<IllegalArgumentException> {
            QuotaWindow(
                usedPercent = Double.NaN,
                remainingPercent = 50.0,
                windowMinutes = 300,
                resetsAt = resetsAt,
            )
        }
    }

    @Test
    fun quotaWindowRejectsNonPositiveWindowLength() {
        assertFailsWith<IllegalArgumentException> {
            QuotaWindow(usedPercent = 0.0, remainingPercent = 100.0, windowMinutes = 0, resetsAt = resetsAt)
        }
    }

    @Test
    fun quotaWindowAcceptsTheBoundaries() {
        val exhausted = QuotaWindow(usedPercent = 100.0, remainingPercent = 0.0, windowMinutes = 300, resetsAt = resetsAt)
        val untouched = QuotaWindow(usedPercent = 0.0, remainingPercent = 100.0, windowMinutes = 10080, resetsAt = resetsAt)

        assertEquals(0.0, exhausted.remainingPercent)
        assertEquals(100.0, untouched.remainingPercent)
    }

    @Test
    fun quotaSnapshotRejectsAnUnknownSchemaVersion() {
        assertFailsWith<IllegalArgumentException> {
            snapshot(schemaVersion = 0)
        }
    }

    @Test
    fun historyPointRejectsOutOfRangePercentages() {
        assertFailsWith<IllegalArgumentException> {
            HistoryPoint(timestamp = resetsAt, shortWindowRemainingPercent = 140.0, weeklyRemainingPercent = 50.0)
        }

        assertFailsWith<IllegalArgumentException> {
            HistoryPoint(timestamp = resetsAt, shortWindowRemainingPercent = 50.0, weeklyRemainingPercent = -1.0)
        }
    }

    @Test
    fun onlyUserMeaningfulEventTypesMapFromTheWire() {
        assertEquals(QuotaEventType.QuotaChanged, QuotaEventType.fromWire("quota_changed"))
        assertEquals(QuotaEventType.WindowReset, QuotaEventType.fromWire("window_reset"))
        assertEquals(QuotaEventType.SourceError, QuotaEventType.fromWire("source_error"))

        // Technical noise must not become a user-visible event.
        assertEquals(null, QuotaEventType.fromWire("ping"))
        assertEquals(null, QuotaEventType.fromWire("http_success"))
        assertEquals(null, QuotaEventType.fromWire("sqlite_cleanup"))
    }

    private fun snapshot(schemaVersion: Int) = QuotaSnapshot(
        schemaVersion = schemaVersion,
        generatedAt = resetsAt,
        source = "codex_app_server",
        status = QuotaSourceStatus.Online,
        lastSuccessfulSyncAt = resetsAt,
        shortWindow = QuotaWindow(28.0, 72.0, 300, resetsAt),
        weekly = QuotaWindow(46.0, 54.0, 10080, resetsAt),
    )
}

class ConnectionStateTest {
    @Test
    fun transientStatesAreNeverPersistedAsThemselves() {
        // Restoring a process into "Connecting" would be a claim the app cannot support.
        assertEquals(PersistedConnectionState.OfflineCached, ConnectionState.Connecting.toPersisted())
        assertEquals(PersistedConnectionState.OfflineCached, ConnectionState.Discovering.toPersisted())
        assertEquals(
            PersistedConnectionState.OfflineCached,
            ConnectionState.Reconnecting(attempt = 3, nextRetryIn = Duration.ofSeconds(5)).toPersisted(),
        )
    }

    @Test
    fun aLiveConnectionComesBackAsCachedUntilProvenCurrent() {
        assertEquals(
            PersistedConnectionState.OfflineCached,
            ConnectionState.Connected(since = Instant.parse("2026-09-22T13:30:00Z")).toPersisted(),
        )
    }

    @Test
    fun terminalStatesSurviveRecreation() {
        assertEquals(PersistedConnectionState.Unpaired, ConnectionState.Unpaired.toPersisted())
        assertEquals(PersistedConnectionState.AuthRequired, ConnectionState.AuthRequired.toPersisted())
        assertEquals(PersistedConnectionState.RepairRequired, ConnectionState.RepairRequired.toPersisted())
        assertEquals(
            PersistedConnectionState.SecurityError,
            ConnectionState.SecurityError("identity mismatch").toPersisted(),
        )
    }

    @Test
    fun aSecurityErrorIsNotInterchangeableWithBeingOffline() {
        // These must stay distinguishable: one needs the user, the other needs the network.
        val securityError = ConnectionState.SecurityError("identity mismatch")
        val offline = ConnectionState.OfflineCached(lastSuccessfulSyncAt = null)

        assertTrue(securityError != offline)
        assertTrue(securityError.toPersisted() != offline.toPersisted())
    }
}
