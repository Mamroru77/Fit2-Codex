package com.codexquota.app.data.security

import java.util.Base64
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json

/**
 * The bytes a [PairedBridgeStore] persists.
 *
 * This indirection exists so the store is testable without a device: the Android implementation
 * writes a private file in the app's own storage, and a test supplies an in-memory one.
 */
interface SecretStorage {
    /** The stored document, or `null` when nothing has been stored yet. */
    fun read(): String?

    /** Replaces the stored document. */
    fun write(content: String)

    /** Removes the stored document. */
    fun clear()
}

/**
 * Remembers the paired Bridge and its device credential across process restarts.
 *
 * Two things are deliberately kept apart:
 *
 * - the **identity** (certificate bytes and SPKI fingerprint), which is public and is what the app
 *   pins; and
 * - the **credential**, which is a bearer secret and is only ever stored sealed by a [SecretBox].
 *
 * Nothing here writes a plaintext credential, because the type it holds has no plaintext field.
 */
class PairedBridgeStore(
    private val storage: SecretStorage,
    private val secretBox: SecretBox,
) {
    private val json = Json {
        ignoreUnknownKeys = true
        explicitNulls = true
        encodeDefaults = true
    }

    /**
     * The stored pairing, or `null` when there is none.
     *
     * A document that cannot be read is reported as `null` rather than thrown: an unreadable pairing
     * record means the app has no usable pairing, and the honest thing is to show "not paired" and
     * let the user pair again instead of crashing on launch.
     *
     * An unusable *endpoint* is different, and is dropped rather than taken as a reason to discard
     * the pairing. The address is only a location — it changes with DHCP and with the interface —
     * while the identity and the credential are what the pairing actually is. Losing the address
     * costs a rediscovery; losing the identity would cost a re-pair.
     */
    fun load(): PairedBridge? {
        val raw = storage.read() ?: return null
        val persisted = try {
            json.decodeFromString<PersistedPairedBridge>(raw)
        } catch (_: Exception) {
            return null
        }

        val credential = persisted.credentialCiphertext?.let { ciphertext ->
            persisted.credentialIv?.let { iv ->
                SealedSecret(decode(ciphertext), decode(iv))
            }
        }

        val endpoint = if (persisted.host != null && persisted.port != null) {
            runCatching { BridgeEndpoint(persisted.host, persisted.port) }.getOrNull()
        } else {
            null
        }

        return try {
            PairedBridge(
                bridgeId = persisted.bridgeId,
                displayName = persisted.displayName,
                identityCertificateDer = decode(persisted.identityCertificateDer),
                identityFingerprint = persisted.identityFingerprint,
                endpoint = endpoint,
                credential = credential,
            )
        } catch (_: IllegalArgumentException) {
            null
        }
    }

    /** Replaces the stored pairing. */
    fun save(bridge: PairedBridge) {
        storage.write(
            json.encodeToString(
                PersistedPairedBridge(
                    bridgeId = bridge.bridgeId,
                    displayName = bridge.displayName,
                    identityCertificateDer = encode(bridge.identityCertificateDer),
                    identityFingerprint = bridge.identityFingerprint,
                    host = bridge.endpoint?.host,
                    port = bridge.endpoint?.port,
                    credentialCiphertext = bridge.credential?.let { encode(it.ciphertext) },
                    credentialIv = bridge.credential?.let { encode(it.iv) },
                ),
            ),
        )
    }

    /** Forgets the pairing entirely, including the credential. */
    fun clear() {
        storage.clear()
    }

    /**
     * Opens the stored credential.
     *
     * Returns `null` when there is no credential, and also when the sealed bytes cannot be opened —
     * a key that has been invalidated (a device wipe, a restored backup) leaves a ciphertext nobody
     * can read, and that is a re-pair, not a crash.
     */
    fun openCredential(bridge: PairedBridge): ByteArray? {
        val sealed = bridge.credential ?: return null

        return try {
            secretBox.open(sealed)
        } catch (_: Exception) {
            null
        }
    }

    /** Seals a freshly issued credential so it can be stored with [save]. */
    fun sealCredential(credential: ByteArray): SealedSecret = secretBox.seal(credential)

    private fun encode(bytes: ByteArray): String = Base64.getEncoder().encodeToString(bytes)

    private fun decode(text: String): ByteArray = Base64.getDecoder().decode(text)
}

/**
 * The on-disk shape.
 *
 * Byte arrays are written as base64 text rather than as a JSON number array so the file is readable
 * by a person debugging a pairing problem, and so the format does not depend on a serialiser
 * default.
 */
@Serializable
private data class PersistedPairedBridge(
    val bridgeId: String,
    val displayName: String,
    val identityCertificateDer: String,
    val identityFingerprint: String,
    val host: String? = null,
    val port: Int? = null,
    val credentialCiphertext: String? = null,
    val credentialIv: String? = null,
)
