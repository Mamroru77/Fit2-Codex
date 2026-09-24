package com.codexquota.app.data.security

/**
 * The last endpoint the phone successfully used for a paired Bridge.
 *
 * An address is only a location: it may change with DHCP or a network interface change without the
 * pairing becoming invalid, which is why it is stored beside the identity rather than inside it.
 */
data class BridgeEndpoint(
    val host: String,
    val port: Int,
) {
    init {
        require(host.isNotBlank()) { "host must not be blank" }
        require(port in 1..65535) { "port must be a usable TCP port but was $port" }
    }

    /** The HTTPS base URL for this endpoint. Cleartext HTTP is never used. */
    val httpsBaseUrl: String get() = "https://$host:$port"

    /** The authenticated WebSocket URL for this endpoint. */
    val webSocketUrl: String get() = "wss://$host:$port/api/v1/ws"
}

/**
 * A secret in its at-rest form.
 *
 * The plaintext is never a field of anything persisted, so no serialisation mistake can write it
 * out: only [ciphertext] and [iv] exist outside the Keystore-backed box.
 */
data class SealedSecret(
    val ciphertext: ByteArray,
    val iv: ByteArray,
) {
    // ByteArray gives reference equality by default, which would make two equal secrets look
    // different and two different ones look the same. Both are wrong for a value type.
    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is SealedSecret) return false
        return ciphertext.contentEquals(other.ciphertext) && iv.contentEquals(other.iv)
    }

    override fun hashCode(): Int = 31 * ciphertext.contentHashCode() + iv.contentHashCode()

    /** Never renders the secret material. */
    override fun toString(): String = "SealedSecret(ciphertext=${ciphertext.size}B, iv=${iv.size}B)"
}

/**
 * A paired Bridge, as the phone remembers it.
 *
 * [identityCertificateDer] is the Bridge's stable identity certificate — the trust anchor. The
 * pinned value is [identityFingerprint], the SHA-256 of that certificate's SubjectPublicKeyInfo,
 * which stays the same across leaf renewals and changes if the identity is replaced.
 *
 * The device credential is held as a [SealedSecret] and nothing else: there is no plaintext field
 * for a mistake to write.
 */
data class PairedBridge(
    val bridgeId: String,
    val displayName: String,
    val identityCertificateDer: ByteArray,
    val identityFingerprint: String,
    val endpoint: BridgeEndpoint?,
    val credential: SealedSecret?,
) {
    init {
        require(bridgeId.isNotBlank()) { "bridgeId must not be blank" }
        require(displayName.isNotBlank()) { "displayName must not be blank" }
        require(identityCertificateDer.isNotEmpty()) { "the Bridge identity certificate is required" }
        require(identityFingerprint.isNotBlank()) { "the Bridge identity fingerprint is required" }
    }

    /** Whether a device credential has been issued and is available. */
    val hasCredential: Boolean get() = credential != null

    /** A copy pointing at a newly discovered address for the same Bridge identity. */
    fun withEndpoint(endpoint: BridgeEndpoint): PairedBridge = copy(endpoint = endpoint)

    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is PairedBridge) return false
        return bridgeId == other.bridgeId &&
            displayName == other.displayName &&
            identityCertificateDer.contentEquals(other.identityCertificateDer) &&
            identityFingerprint == other.identityFingerprint &&
            endpoint == other.endpoint &&
            credential == other.credential
    }

    override fun hashCode(): Int {
        var result = bridgeId.hashCode()
        result = 31 * result + displayName.hashCode()
        result = 31 * result + identityCertificateDer.contentHashCode()
        result = 31 * result + identityFingerprint.hashCode()
        result = 31 * result + (endpoint?.hashCode() ?: 0)
        result = 31 * result + (credential?.hashCode() ?: 0)
        return result
    }

    /** Names the identity, never the credential. */
    override fun toString(): String =
        "PairedBridge(bridgeId=$bridgeId, displayName=$displayName, fingerprint=$identityFingerprint, " +
            "endpoint=$endpoint, hasCredential=$hasCredential)"
}
