package com.codexquota.app.data.api

import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable

/**
 * The v1 wire DTOs.
 *
 * Every required field is declared without a default, so a payload that omits it fails to
 * deserialize instead of silently binding to `0` or `""`. Unknown fields are ignored by the parser
 * configuration, which is what lets the Bridge add optional fields within v1.
 */
@Serializable
internal data class QuotaWindowDto(
    val usedPercent: Double,
    val remainingPercent: Double,
    val windowMinutes: Int,
    val resetsAt: String,
)

@Serializable
internal data class QuotaWindowsDto(
    val shortWindow: QuotaWindowDto,
    val weekly: QuotaWindowDto,
)

/** `GET /api/v1/quota`, and the payload of a `quota.updated` WebSocket frame. */
@Serializable
internal data class QuotaResponseDto(
    val schemaVersion: Int,
    val generatedAt: String,
    val source: String,
    val status: String,
    // Nullable by contract: a Bridge that has never synchronised reports null, and the field may be
    // absent entirely. Both mean the same thing here.
    val lastSuccessfulSyncAt: String? = null,
    val windows: QuotaWindowsDto,
)

@Serializable
internal data class HistoryPointDto(
    val timestamp: String,
    val shortWindowRemainingPercent: Double,
    val weeklyRemainingPercent: Double,
)

/** `GET /api/v1/history?hours=24` */
@Serializable
internal data class HistoryResponseDto(
    val hours: Int,
    val points: List<HistoryPointDto>,
)

@Serializable
internal data class QuotaEventDto(
    val type: String,
    val occurredAt: String,
    val detail: String? = null,
)

/** `GET /api/v1/events?hours=24` */
@Serializable
internal data class EventsResponseDto(
    val hours: Int,
    val events: List<QuotaEventDto>,
)

/** The stable part of a v1 error. */
@Serializable
internal data class ApiErrorDto(
    val code: String,
    val message: String,
    val retryable: Boolean = false,
)

/** The single v1 error envelope, REST and pairing alike. */
@Serializable
internal data class ApiErrorResponseDto(
    val error: ApiErrorDto,
)

// ---------------------------------------------------------------------------------------------
// Pairing bootstrap
// ---------------------------------------------------------------------------------------------

/** The body of `POST /api/v1/pairing/request`, used by the discovery flow. */
@Serializable
internal data class PairingRequestDto(
    val displayName: String,
)

/** The body of `POST /api/v1/pairing/claim`, used by the QR flow. */
@Serializable
internal data class PairingClaimRequestDto(
    val pairingId: String,
    val displayName: String,
)

/** The body of `POST /api/v1/pairing/complete`. */
@Serializable
internal data class PairingCompleteRequestDto(
    val pairingId: String,
)

/** The public view of a pairing session, as returned by request, claim and status. */
@Serializable
internal data class PairingSessionDto(
    val pairingId: String,
    val status: String,
    val verificationCode: String,
    val displayName: String? = null,
    val expiresAt: String,
)

/** The credential a paired device receives exactly once. */
@Serializable
internal data class DeviceCredentialDto(
    val deviceId: String,
    @SerialName("token") val token: String,
)

/** The WebSocket `hello` frame. */
@Serializable
internal data class WsHelloDto(
    val apiVersion: String,
    val bridgeVersion: String,
    val type: String,
)

/** A WebSocket frame whose `type` decides how it is interpreted. */
@Serializable
internal data class WsEnvelopeDto(
    val type: String,
    val sequence: Long? = null,
    val payload: QuotaResponseDto? = null,
)
