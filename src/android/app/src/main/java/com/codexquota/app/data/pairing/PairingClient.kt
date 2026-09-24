package com.codexquota.app.data.pairing

import com.codexquota.app.data.api.ApiErrorCodes
import com.codexquota.app.data.api.DeviceCredentialDto
import com.codexquota.app.data.api.PairingClaimRequestDto
import com.codexquota.app.data.api.PairingCompleteRequestDto
import com.codexquota.app.data.api.PairingRequestDto
import com.codexquota.app.data.api.PairingSession
import com.codexquota.app.data.api.PairingSessionDto
import com.codexquota.app.data.api.PairingWireStatus
import com.codexquota.app.data.api.ProtocolException
import com.codexquota.app.data.api.decodeApiError
import com.codexquota.app.data.api.bodyText
import com.codexquota.app.data.api.decodeV1
import com.codexquota.app.data.api.toDomain
import com.codexquota.app.data.security.BootstrapFingerprintTrustManager
import com.codexquota.app.data.security.BridgeEndpoint
import com.codexquota.app.data.security.BridgeIdentity
import java.io.IOException
import java.security.cert.CertificateException
import java.security.cert.X509Certificate
import java.time.Duration
import java.time.Instant
import javax.net.ssl.SSLContext
import javax.net.ssl.X509TrustManager
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.withContext
import kotlinx.serialization.json.Json
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import okhttp3.Response

/** The credential the Bridge issues exactly once, at the end of a successful pairing. */
data class DeviceCredential(
    val deviceId: String,
    val token: String,
) {
    /** Never renders the token. */
    override fun toString(): String = "DeviceCredential(deviceId=$deviceId, token=<redacted>)"
}

/** What happened when a pairing was attempted. */
sealed interface PairingOutcome {
    /** The Bridge approved this phone and issued a credential. */
    class Paired(
        val bridgeId: String,
        val endpoint: BridgeEndpoint,
        val identityCertificateDer: ByteArray,
        val identityFingerprint: String,
        val credential: DeviceCredential,
    ) : PairingOutcome {
        override fun equals(other: Any?): Boolean =
            other is Paired &&
                bridgeId == other.bridgeId &&
                endpoint == other.endpoint &&
                identityCertificateDer.contentEquals(other.identityCertificateDer) &&
                identityFingerprint == other.identityFingerprint &&
                credential == other.credential

        override fun hashCode(): Int =
            31 * bridgeId.hashCode() + identityFingerprint.hashCode() + credential.hashCode()
    }

    /** The person at the Windows machine said no. Terminal for this session. */
    data class Rejected(val verificationCode: String) : PairingOutcome

    /** The session ran out of its five minutes, or the Bridge no longer knows it. */
    data class Expired(val message: String) : PairingOutcome

    /** The Bridge did not approve in time. The session is abandoned; a new one is needed. */
    data class TimedOut(val verificationCode: String) : PairingOutcome
}

/**
 * A Bridge identity that discovery found but nobody has confirmed yet.
 *
 * The verification code is the point of this type: it is what the user compares against the code on
 * the Windows pairing window. Nothing may be sent to the Bridge until that comparison has been made,
 * which is why this type is not accepted anywhere a confirmed identity is required.
 */
class DiscoveredBridgeIdentity internal constructor(
    val endpoint: BridgeEndpoint,
    val identityCertificateDer: ByteArray,
    val identityFingerprint: String,
) {
    /** The four-group code a person reads off the Windows pairing window and compares. */
    val verificationCode: String = BridgeIdentity.humanVerificationCode(identityFingerprint)

    /**
     * Records that a person confirmed this identity.
     *
     * This is the only way to obtain a [ConfirmedBridgeIdentity], so "the user confirmed it" is a
     * fact the type carries rather than a step someone has to remember.
     */
    fun confirmedByUser(): ConfirmedBridgeIdentity = ConfirmedBridgeIdentity(this)

    override fun toString(): String =
        "DiscoveredBridgeIdentity(endpoint=$endpoint, verificationCode=$verificationCode)"
}

/** A discovered Bridge identity that a person has explicitly confirmed. */
class ConfirmedBridgeIdentity internal constructor(
    internal val discovered: DiscoveredBridgeIdentity,
) {
    internal val endpoint: BridgeEndpoint get() = discovered.endpoint
    internal val identityCertificateDer: ByteArray get() = discovered.identityCertificateDer
    internal val identityFingerprint: String get() = discovered.identityFingerprint

    /** The code the user compared. */
    val verificationCode: String get() = discovered.verificationCode
}

/**
 * Drives the pairing handshake against a Bridge.
 *
 * Two things this class is careful about, because they are what make pairing safe:
 *
 * 1. **The identity is verified before anything is sent.** The HTTP client it uses is pinned to the
 *    expected Bridge identity, so a Bridge presenting a different one fails the TLS handshake. No
 *    request body, and certainly no credential, is ever put on the wire to the wrong Bridge.
 * 2. **Only a locally approved session can complete.** The Bridge refuses to issue a credential
 *    until the person at the Windows machine presses Allow; this client merely waits and reports.
 */
class PairingClient(
    private val http: PairingHttpClient,
    private val baseUrl: String,
    private val displayName: String,
    private val pollInterval: Duration = Duration.ofSeconds(1),
    private val clock: () -> Instant = Instant::now,
    private val pause: suspend (Duration) -> Unit = { delay(it.toMillis()) },
) {
    private val json = Json { ignoreUnknownKeys = true }

    /**
     * Asks the Bridge to create a pairing session for a phone that found it by discovery.
     *
     * Discovery never establishes trust, so this can only ever create a session that waits for the
     * person at the Windows machine.
     */
    suspend fun requestDiscoveryPairing(): PairingSession {
        val payload = json.encodeToString(
            PairingRequestDto.serializer(),
            PairingRequestDto(displayName),
        )

        return decodeSession(post("/api/v1/pairing/request", payload), "pairing session")
    }

    /** Attaches this phone to a QR session the desktop created. */
    suspend fun claimQrPairing(pairingId: String): PairingSession {
        val payload = json.encodeToString(
            PairingClaimRequestDto.serializer(),
            PairingClaimRequestDto(pairingId, displayName),
        )

        return decodeSession(post("/api/v1/pairing/claim", payload), "pairing session")
    }

    /** Reads the current state of a pairing session. */
    suspend fun status(pairingId: String): PairingSession =
        decodeSession(get("/api/v1/pairing/status/$pairingId"), "pairing status")

    /**
     * Polls until the session is approved, rejected or expired.
     *
     * The polling is bounded by [timeout] so a session nobody answers is abandoned rather than
     * retried forever.
     */
    suspend fun awaitLocalApproval(
        pairingId: String,
        timeout: Duration = SESSION_LIFETIME,
    ): PairingSession {
        val deadline = clock().plus(timeout)

        while (true) {
            val session = status(pairingId)

            when (session.status) {
                PairingWireStatus.Approved,
                PairingWireStatus.Rejected,
                PairingWireStatus.Expired,
                PairingWireStatus.Consumed,
                -> return session

                PairingWireStatus.AwaitingClient,
                PairingWireStatus.AwaitingLocalApproval,
                -> Unit
            }

            if (!clock().isBefore(deadline)) {
                return session
            }

            pause(pollInterval)
        }
    }

    /**
     * Completes an approved session and receives the device credential.
     *
     * The credential exists only in this response: the Bridge stores a hash of it, and the phone
     * seals it immediately.
     */
    suspend fun complete(pairingId: String): DeviceCredential {
        val payload = json.encodeToString(
            PairingCompleteRequestDto.serializer(),
            PairingCompleteRequestDto(pairingId),
        )

        val response = post("/api/v1/pairing/complete", payload)
        val dto = decodeV1<DeviceCredentialDto>(response.bodyText(), "device credential")

        return DeviceCredential(dto.deviceId, dto.token)
    }

    /**
     * The whole QR flow: claim the session, wait for local approval, then complete.
     *
     * The client this instance holds must already be pinned to the fingerprint from the QR code,
     * which is what makes the claim safe to send.
     */
    suspend fun pairByQr(
        payload: PairingQrPayload,
        timeout: Duration = SESSION_LIFETIME,
    ): PairingOutcome {
        val claimed = try {
            claimQrPairing(payload.pairingId)
        } catch (e: ProtocolException) {
            return PairingOutcome.Expired(e.message ?: "The pairing session is no longer valid.")
        }

        return finish(claimed, payload.bridgeId, payload.endpoint, payload.identityFingerprint, timeout)
    }

    /**
     * The whole discovery flow, from a confirmed identity.
     *
     * The confirmation is a parameter of type [ConfirmedBridgeIdentity] rather than a boolean, so
     * this cannot be reached without the user having compared the verification code.
     */
    suspend fun pairByDiscovery(
        confirmed: ConfirmedBridgeIdentity,
        bridgeId: String,
        timeout: Duration = SESSION_LIFETIME,
    ): PairingOutcome {
        val requested = try {
            requestDiscoveryPairing()
        } catch (e: ProtocolException) {
            return PairingOutcome.Expired(e.message ?: "The Bridge refused to start pairing.")
        }

        return finish(
            requested,
            bridgeId = bridgeId,
            endpoint = confirmed.endpoint,
            identityFingerprint = confirmed.identityFingerprint,
            timeout = timeout,
        )
    }

    private suspend fun finish(
        session: PairingSession,
        bridgeId: String,
        endpoint: BridgeEndpoint,
        identityFingerprint: String,
        timeout: Duration,
    ): PairingOutcome {
        val settled = awaitLocalApproval(session.pairingId, timeout)

        return when (settled.status) {
            PairingWireStatus.Rejected -> PairingOutcome.Rejected(settled.verificationCode)
            PairingWireStatus.Expired -> PairingOutcome.Expired("The pairing session expired.")

            PairingWireStatus.Approved ->
                completeApproved(settled, bridgeId, endpoint, identityFingerprint)

            else -> PairingOutcome.TimedOut(settled.verificationCode)
        }
    }

    private suspend fun completeApproved(
        session: PairingSession,
        bridgeId: String,
        endpoint: BridgeEndpoint,
        identityFingerprint: String,
    ): PairingOutcome {
        val credential = try {
            complete(session.pairingId)
        } catch (e: ProtocolException) {
            return PairingOutcome.Expired(e.message ?: "The pairing session could not be completed.")
        }

        // The identity that was actually verified during this handshake, not a value the caller
        // supplied. Storing anything else would let a caller record a pin it never checked.
        val identity = http.observedIdentity
            ?: return PairingOutcome.Expired("The Bridge identity was not observed during pairing.")

        return PairingOutcome.Paired(
            bridgeId = bridgeId,
            endpoint = endpoint,
            identityCertificateDer = identity.encoded,
            identityFingerprint = BridgeIdentity.spkiSha256(identity),
            credential = credential,
        )
    }

    private fun decodeSession(response: Response, what: String): PairingSession {
        val text = response.bodyText()

        if (!response.isSuccessful) {
            val error = decodeApiError(text)

            throw ProtocolException(error.code, error.message)
        }

        return decodeV1<PairingSessionDto>(text, what).toDomain()
    }

    private suspend fun get(path: String): Response =
        execute(Request.Builder().url("$baseUrl$path").get().build())

    private suspend fun post(path: String, body: String): Response =
        execute(
            Request.Builder()
                .url("$baseUrl$path")
                .post(body.toRequestBody(JSON))
                .build(),
        )

    /**
     * Runs one request, naming a TLS failure for what it is.
     *
     * A verification failure is deliberately not retried and deliberately not softened: it is the
     * signal that the Bridge is not who it claimed to be, and the caller turns it into a security
     * error rather than a connection problem.
     */
    private suspend fun execute(request: Request): Response = withContext(Dispatchers.IO) {
        try {
            http.client.newCall(request).execute()
        } catch (e: CertificateException) {
            throw IdentityMismatchException(
                e.message ?: "The Bridge identity could not be verified.",
                e,
            )
        } catch (e: javax.net.ssl.SSLException) {
            throw IdentityMismatchException(
                e.message ?: "The Bridge identity could not be verified.",
                e,
            )
        } catch (e: IOException) {
            throw e
        }
    }

    companion object {
        private val JSON = "application/json; charset=utf-8".toMediaType()

        /** A pairing session lives for five minutes, so waiting longer than that is pointless. */
        val SESSION_LIFETIME: Duration = Duration.ofMinutes(5)

        /**
         * Builds the HTTP client used for one pairing handshake.
         *
         * The only trust anchor is the expected Bridge identity, so a Bridge presenting a different
         * one fails the handshake and nothing is sent. Hostname verification is left exactly as
         * OkHttp does it: the leaf's SAN is still checked against the address that was dialled.
         */
        fun pinnedTo(
            expectedIdentityFingerprint: String,
            base: OkHttpClient = OkHttpClient(),
        ): PairingHttpClient {
            val trust = BootstrapFingerprintTrustManager(expectedIdentityFingerprint)

            val context = SSLContext.getInstance("TLS").apply {
                init(null, arrayOf<X509TrustManager>(trust), null)
            }

            return PairingHttpClient(
                client = base.newBuilder()
                    .sslSocketFactory(context.socketFactory, trust)
                    .build(),
                trustManager = trust,
            )
        }
    }
}

/**
 * A pairing HTTP client together with the trust manager that observed its handshakes.
 *
 * Keeping them together is what lets [PairingClient] report the identity that was *verified* rather
 * than one a caller asserted.
 */
class PairingHttpClient internal constructor(
    val client: OkHttpClient,
    private val trustManager: BootstrapFingerprintTrustManager,
) {
    /** The identity of the last handshake that passed verification, or `null` if none has. */
    val observedIdentity: X509Certificate? get() = trustManager.lastVerifiedIdentity
}

/** The Bridge's identity is not the one that was expected. Terminal until the user acts. */
class IdentityMismatchException(message: String, cause: Throwable? = null) : Exception(message, cause)
