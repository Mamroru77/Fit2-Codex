package com.codexquota.app.data.pairing

import com.codexquota.app.data.security.BridgeEndpoint
import kotlinx.serialization.Serializable
import kotlinx.serialization.SerializationException
import kotlinx.serialization.json.Json

/**
 * The payload a pairing QR code carries.
 *
 * A QR code is displayed on a screen, photographed, and often left in a photo library, so the
 * payload is treated as public. It therefore carries no device credential and nothing that would
 * let whoever reads it authenticate as a paired device: only the address to connect to, the
 * one-time pairing session, and the identity fingerprint to compare against. The credential is
 * issued only after the person at the Windows machine approves the session.
 */
data class PairingQrPayload(
    val bridgeId: String,
    val endpoint: BridgeEndpoint,
    val pairingId: String,
    val identityFingerprint: String,
) {
    companion object {
        /** The API path version this client speaks. */
        const val SUPPORTED_VERSION = "v1"

        /**
         * The bare form of the same version.
         *
         * The Bridge writes the API path version `v1`, and the approved plan names the version as
         * `1`. They are the same version written with and without its prefix, so both are accepted
         * and nothing else is.
         */
        private const val SUPPORTED_VERSION_WITHOUT_PREFIX = "1"

        private val json = Json { ignoreUnknownKeys = true }

        /**
         * Parses a scanned QR payload.
         *
         * Every field is required and the version must be one this client speaks. A payload that is
         * missing the fingerprint is refused rather than defaulted, because a pairing that starts
         * without a fingerprint has nothing to verify the Bridge against.
         *
         * @throws PairingQrException when the payload is not a usable v1 pairing code.
         */
        fun parse(raw: String): PairingQrPayload {
            val dto = try {
                json.decodeFromString<PairingQrDto>(raw.trim())
            } catch (e: SerializationException) {
                throw PairingQrException("This is not a Codex Quota pairing code.", e)
            } catch (e: IllegalArgumentException) {
                throw PairingQrException("This is not a Codex Quota pairing code.", e)
            }

            if (dto.v != SUPPORTED_VERSION && dto.v != SUPPORTED_VERSION_WITHOUT_PREFIX) {
                throw PairingQrException(
                    "This pairing code is for API version ${dto.v}, which this app does not speak.",
                )
            }

            if (dto.bridgeId.isBlank() || dto.host.isBlank() || dto.pairingId.isBlank()) {
                throw PairingQrException("This pairing code is incomplete.")
            }

            if (dto.identityFingerprint.isBlank()) {
                throw PairingQrException(
                    "This pairing code carries no Bridge identity fingerprint, so the Bridge cannot be verified.",
                )
            }

            val endpoint = try {
                BridgeEndpoint(dto.host, dto.port)
            } catch (e: IllegalArgumentException) {
                throw PairingQrException("This pairing code has an unusable address.", e)
            }

            return PairingQrPayload(
                bridgeId = dto.bridgeId,
                endpoint = endpoint,
                pairingId = dto.pairingId,
                identityFingerprint = dto.identityFingerprint,
            )
        }
    }
}

/** A pairing code that cannot be used, with the reason a person needs to hear. */
class PairingQrException(message: String, cause: Throwable? = null) : Exception(message, cause)

/**
 * The wire shape of a pairing QR payload.
 *
 * `v` is declared without a default so a payload that omits the version is refused rather than
 * assumed to be current.
 */
@Serializable
internal data class PairingQrDto(
    val v: String,
    val bridgeId: String,
    val host: String,
    val port: Int,
    val pairingId: String,
    val identityFingerprint: String,
)
