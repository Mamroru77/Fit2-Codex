package com.codexquota.app.data.api

/**
 * The stable machine-readable v1 error codes.
 *
 * Android branches on these strings and never on the human-readable message, which is why they are
 * constants rather than something derived from text. The list mirrors the Bridge's
 * `ApiErrorCodes`: adding a code within v1 is allowed, so an unknown code is carried through rather
 * than rejected.
 */
object ApiErrorCodes {
    const val PAIRING_INVALID = "PAIRING_INVALID"
    const val PAIRING_EXPIRED = "PAIRING_EXPIRED"
    const val DEVICE_UNAUTHORIZED = "DEVICE_UNAUTHORIZED"
    const val CODEX_AUTH_REQUIRED = "CODEX_AUTH_REQUIRED"
    const val CODEX_UNAVAILABLE = "CODEX_UNAVAILABLE"
    const val RATE_LIMIT_DATA_UNAVAILABLE = "RATE_LIMIT_DATA_UNAVAILABLE"
    const val SOURCE_SCHEMA_UNSUPPORTED = "SOURCE_SCHEMA_UNSUPPORTED"
    const val SECURITY_IDENTITY_MISMATCH = "SECURITY_IDENTITY_MISMATCH"
    const val API_VERSION_UNSUPPORTED = "API_VERSION_UNSUPPORTED"
    const val BRIDGE_INTERNAL_ERROR = "BRIDGE_INTERNAL_ERROR"
    const val DATA_PROTOCOL_ERROR = "DATA_PROTOCOL_ERROR"
    const val INVALID_REQUEST = "INVALID_REQUEST"
    const val HISTORY_UNAVAILABLE = "HISTORY_UNAVAILABLE"
}

/**
 * A failure that has a stable v1 code.
 *
 * It is thrown for anything the app can name, so the layers above can map a code to behaviour
 * instead of inspecting an exception type. A code the app does not know is preserved verbatim.
 */
open class ProtocolException(
    val code: String,
    message: String,
    cause: Throwable? = null,
) : Exception(message, cause) {
    override fun toString(): String = "ProtocolException($code): ${message}"
}

/** The Bridge answered, but refused this device's credential. Re-pairing is the only recovery. */
class DeviceUnauthorizedException(message: String, cause: Throwable? = null) :
    ProtocolException(ApiErrorCodes.DEVICE_UNAUTHORIZED, message, cause)

/**
 * Reads a response body as text.
 *
 * It lives beside the protocol errors because it is part of reading a v1 document, and both the
 * REST client and the pairing client use it.
 */
internal fun okhttp3.Response.bodyText(): String = body.string()

/**
 * Turns a stable error code into the exception that carries it.
 *
 * A `DATA_PROTOCOL_ERROR` becomes its own type so a caller cannot mistake a malformed document for a
 * request the Bridge refused.
 */
internal fun protocolFailure(code: String, message: String): ProtocolException =
    if (code == ApiErrorCodes.DATA_PROTOCOL_ERROR) {
        DataProtocolException(message)
    } else {
        ProtocolException(code, message)
    }

/**
 * A payload did not satisfy the v1 contract.
 *
 * This exists so a missing `remainingPercent` becomes a named failure rather than a plausible
 * `0%`. It is deliberately not a subtype of any "no data" signal: a caller that ignored it would
 * otherwise be able to render a quota the Bridge never reported.
 */
class DataProtocolException(message: String, cause: Throwable? = null) :
    ProtocolException(ApiErrorCodes.DATA_PROTOCOL_ERROR, message, cause)
