package com.codexquota.app.data.api

import com.codexquota.app.domain.HistoryPoint
import com.codexquota.app.domain.QuotaEvent
import com.codexquota.app.domain.QuotaEventType
import com.codexquota.app.domain.QuotaSnapshot
import com.codexquota.app.domain.QuotaSourceStatus
import com.codexquota.app.domain.QuotaWindow
import java.time.Instant
import java.time.OffsetDateTime
import kotlinx.serialization.SerializationException
import kotlinx.serialization.json.Json

/**
 * The v1 JSON configuration.
 *
 * `ignoreUnknownKeys = true` is what makes an added optional field harmless, and it is the only
 * leniency configured: missing required fields still fail, because every required DTO property is
 * declared without a default.
 */
internal val V1Json: Json = Json {
    ignoreUnknownKeys = true
    isLenient = false
    explicitNulls = true
}

/** The only schema version this client understands. */
const val SUPPORTED_SCHEMA_VERSION = 1

/** The only API path version this client understands. */
const val SUPPORTED_API_VERSION = "v1"

/**
 * Reads a timestamp that the Bridge writes as a UTC instant.
 *
 * `Instant.parse` covers the contract's `Z` form. `OffsetDateTime` is accepted as a fallback
 * because it is the same instant written differently, and refusing it would make the client fail
 * on a document it fully understands.
 */
internal fun parseInstant(value: String, field: String): Instant =
    try {
        Instant.parse(value)
    } catch (_: java.time.format.DateTimeParseException) {
        try {
            OffsetDateTime.parse(value).toInstant()
        } catch (e: java.time.format.DateTimeParseException) {
            throw DataProtocolException("$field is not an ISO-8601 timestamp: $value", e)
        }
    }

/** Maps the wire status onto the domain enum, refusing a value the contract does not define. */
internal fun quotaSourceStatus(value: String): QuotaSourceStatus = when (value) {
    "online" -> QuotaSourceStatus.Online
    "stale" -> QuotaSourceStatus.Stale
    "unavailable" -> QuotaSourceStatus.Unavailable
    "auth_required" -> QuotaSourceStatus.AuthRequired
    "source_error" -> QuotaSourceStatus.SourceError
    "source_schema_unsupported" -> QuotaSourceStatus.SourceSchemaUnsupported
    else -> throw DataProtocolException("Unknown quota status: $value")
}

/**
 * Builds the domain snapshot, validating rather than defaulting.
 *
 * A percentage outside 0..100, a non-positive window length or an unknown schema version is a
 * protocol error. It must never become a number the user could read as real quota.
 */
internal fun QuotaResponseDto.toDomain(): QuotaSnapshot {
    if (schemaVersion != SUPPORTED_SCHEMA_VERSION) {
        throw DataProtocolException(
            "Unsupported schema version $schemaVersion, expected $SUPPORTED_SCHEMA_VERSION",
        )
    }

    return try {
        QuotaSnapshot(
            schemaVersion = schemaVersion,
            generatedAt = parseInstant(generatedAt, "generatedAt"),
            source = source,
            status = quotaSourceStatus(status),
            lastSuccessfulSyncAt = lastSuccessfulSyncAt?.let { parseInstant(it, "lastSuccessfulSyncAt") },
            shortWindow = windows.shortWindow.toDomain("windows.shortWindow"),
            weekly = windows.weekly.toDomain("windows.weekly"),
        )
    } catch (e: IllegalArgumentException) {
        // The domain types refuse out-of-range values; that refusal is the protocol error.
        throw DataProtocolException(e.message ?: "Quota payload is out of range", e)
    }
}

private fun QuotaWindowDto.toDomain(path: String): QuotaWindow =
    try {
        QuotaWindow(
            usedPercent = usedPercent,
            remainingPercent = remainingPercent,
            windowMinutes = windowMinutes,
            resetsAt = parseInstant(resetsAt, "$path.resetsAt"),
        )
    } catch (e: IllegalArgumentException) {
        throw DataProtocolException(e.message ?: "$path is out of range", e)
    }

/** Maps one history point, refusing a percentage outside 0..100. */
internal fun HistoryPointDto.toDomain(): HistoryPoint =
    try {
        HistoryPoint(
            timestamp = parseInstant(timestamp, "timestamp"),
            shortWindowRemainingPercent = shortWindowRemainingPercent,
            weeklyRemainingPercent = weeklyRemainingPercent,
        )
    } catch (e: IllegalArgumentException) {
        throw DataProtocolException(e.message ?: "History point is out of range", e)
    }

/**
 * Maps one event, dropping the ones a person does not need to see.
 *
 * Technical ping, cleanup and routine HTTP-success events are not in [QuotaEventType], so they
 * cannot reach the user's timeline. A `null` here is a deliberate filter, not an error.
 */
internal fun QuotaEventDto.toDomainOrNull(): QuotaEvent? {
    val type = QuotaEventType.fromWire(type) ?: return null

    return QuotaEvent(
        type = type,
        occurredAt = parseInstant(occurredAt, "occurredAt"),
        detail = detail,
    )
}

/** Decodes a JSON document, turning any parse failure into a named protocol error. */
internal inline fun <reified T> decodeV1(body: String, what: String): T =
    try {
        V1Json.decodeFromString<T>(body)
    } catch (e: SerializationException) {
        throw DataProtocolException("$what is not a valid v1 document: ${e.message}", e)
    } catch (e: IllegalArgumentException) {
        throw DataProtocolException("$what is not a valid v1 document: ${e.message}", e)
    }

/** Decodes a v1 error envelope. */
internal fun decodeApiError(body: String): ApiErrorDto =
    decodeV1<ApiErrorResponseDto>(body, "error envelope").error

/** The `status` values a pairing session can report, exactly as the Bridge names them. */
enum class PairingWireStatus {
    AwaitingClient,
    AwaitingLocalApproval,
    Approved,
    Rejected,
    Consumed,
    Expired,
    ;

    companion object {
        /** Maps the wire value, or throws when the Bridge reports a state this client cannot reason about. */
        fun fromWire(value: String): PairingWireStatus = when (value) {
            "awaiting_client" -> AwaitingClient
            "awaiting_local_approval" -> AwaitingLocalApproval
            "approved" -> Approved
            "rejected" -> Rejected
            "consumed" -> Consumed
            "expired" -> Expired
            else -> throw DataProtocolException("Unknown pairing status: $value")
        }
    }
}

/** Maps a pairing session response into the domain view the UI and pairing client use. */
internal fun PairingSessionDto.toDomain(): PairingSession = PairingSession(
    pairingId = pairingId,
    status = PairingWireStatus.fromWire(status),
    verificationCode = verificationCode,
    displayName = displayName,
    expiresAt = parseInstant(expiresAt, "expiresAt"),
)

/** The domain view of a pairing session. */
data class PairingSession(
    val pairingId: String,
    val status: PairingWireStatus,
    val verificationCode: String,
    val displayName: String?,
    val expiresAt: Instant,
)
