package com.codexquota.app.domain

import java.time.Instant

/**
 * One quota window.
 *
 * The percentages are validated here rather than at the UI, because a value outside 0..100 means the
 * Bridge and the app disagree about the protocol, and rendering it as a number would hide that. A
 * missing or invalid percentage must surface as a protocol error, never as a plausible-looking `0%`.
 */
data class QuotaWindow(
    val usedPercent: Double,
    val remainingPercent: Double,
    val windowMinutes: Int,
    val resetsAt: Instant,
) {
    init {
        require(usedPercent.isFinite() && usedPercent in 0.0..100.0) {
            "usedPercent must be within 0..100 but was $usedPercent"
        }
        require(remainingPercent.isFinite() && remainingPercent in 0.0..100.0) {
            "remainingPercent must be within 0..100 but was $remainingPercent"
        }
        require(windowMinutes > 0) {
            "windowMinutes must be positive but was $windowMinutes"
        }
    }
}

/** Availability of the quota source, mirroring the Bridge's v1 `status` values. */
enum class QuotaSourceStatus {
    Online,
    Stale,
    Unavailable,
    AuthRequired,
    SourceError,
    SourceSchemaUnsupported,
}

/**
 * The complete normalised quota state.
 *
 * [generatedAt] is when the Bridge produced it; it is what freshness is measured against, so a
 * snapshot is never described as real-time merely because the app received it recently.
 */
data class QuotaSnapshot(
    val schemaVersion: Int,
    val generatedAt: Instant,
    val source: String,
    val status: QuotaSourceStatus,
    val lastSuccessfulSyncAt: Instant?,
    val shortWindow: QuotaWindow,
    val weekly: QuotaWindow,
) {
    init {
        require(schemaVersion > 0) { "schemaVersion must be positive but was $schemaVersion" }
        require(source.isNotBlank()) { "source must not be blank" }
    }
}

/** One persisted sample of the 24-hour history. */
data class HistoryPoint(
    val timestamp: Instant,
    val shortWindowRemainingPercent: Double,
    val weeklyRemainingPercent: Double,
) {
    init {
        require(shortWindowRemainingPercent.isFinite() && shortWindowRemainingPercent in 0.0..100.0) {
            "shortWindowRemainingPercent must be within 0..100 but was $shortWindowRemainingPercent"
        }
        require(weeklyRemainingPercent.isFinite() && weeklyRemainingPercent in 0.0..100.0) {
            "weeklyRemainingPercent must be within 0..100 but was $weeklyRemainingPercent"
        }
    }
}

/**
 * Event types that are meaningful to a person.
 *
 * Technical ping, storage-cleanup and routine HTTP-success events are deliberately absent: they are
 * not part of this model, so they cannot leak into the user's timeline.
 */
enum class QuotaEventType {
    QuotaChanged,
    WindowReset,
    BridgeStarted,
    BridgeStopped,
    CodexConnected,
    CodexDisconnected,
    AuthRequired,
    SourceError,
    ;

    companion object {
        /** Maps a wire value, or returns `null` when the event is not user-meaningful. */
        fun fromWire(value: String): QuotaEventType? = when (value) {
            "quota_changed" -> QuotaChanged
            "window_reset" -> WindowReset
            "bridge_started" -> BridgeStarted
            "bridge_stopped" -> BridgeStopped
            "codex_connected" -> CodexConnected
            "codex_disconnected" -> CodexDisconnected
            "auth_required" -> AuthRequired
            "source_error" -> SourceError
            else -> null
        }
    }
}

/** One user-meaningful event. */
data class QuotaEvent(
    val type: QuotaEventType,
    val occurredAt: Instant,
    val detail: String?,
)
