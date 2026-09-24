package com.codexquota.app.data.api

import com.codexquota.app.data.security.PairedBridge
import com.codexquota.app.data.security.PairedBridgeTrustManagerFactory
import com.codexquota.app.domain.HistoryPoint
import com.codexquota.app.domain.QuotaEvent
import com.codexquota.app.domain.QuotaSnapshot
import java.io.IOException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import okhttp3.HttpUrl.Companion.toHttpUrlOrNull
import okhttp3.Interceptor
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.Response

/**
 * The authenticated v1 client.
 *
 * Two properties hold by construction rather than by remembering them:
 *
 * - the client it is given is pinned to the paired Bridge identity, so a request can only ever reach
 *   the Bridge that was paired with; and
 * - the `Authorization` header is attached by an interceptor that belongs to that pinned client, so
 *   there is no code path that sends the credential to anything else.
 *
 * Nothing here logs. The credential is a long-lived bearer token, so it is written into a header and
 * nowhere else.
 */
class OkHttpBridgeApi(
    private val client: OkHttpClient,
    private val baseUrl: String,
) : BridgeApi {

    override suspend fun fetchQuota(): QuotaSnapshot =
        decodeV1<QuotaResponseDto>(get("/api/v1/quota", "quota"), "quota").toDomain()

    override suspend fun fetchHistory(hours: Int): List<HistoryPoint> {
        require(hours in BridgeApi.VALID_HOURS) { "hours must be within ${BridgeApi.VALID_HOURS} but was $hours" }

        val body = get("/api/v1/history?hours=$hours", "history")

        return decodeV1<HistoryResponseDto>(body, "history").points.map { it.toDomain() }
    }

    override suspend fun fetchEvents(hours: Int): List<QuotaEvent> {
        require(hours in BridgeApi.VALID_HOURS) { "hours must be within ${BridgeApi.VALID_HOURS} but was $hours" }

        val body = get("/api/v1/events?hours=$hours", "events")

        return decodeV1<EventsResponseDto>(body, "events").events.mapNotNull { it.toDomainOrNull() }
    }

    private suspend fun get(path: String, what: String): String = withContext(Dispatchers.IO) {
        val url = "$baseUrl$path".toHttpUrlOrNull()
            ?: throw DataProtocolException("$what: the Bridge address is not a valid URL.")

        val response = try {
            client.newCall(Request.Builder().url(url).get().build()).execute()
        } catch (e: IOException) {
            throw e
        }

        response.use { read(it, what) }
    }

    /** Turns one response into a body, or into the typed failure its status and code imply. */
    private fun read(response: Response, what: String): String {
        val body = response.bodyText()

        if (response.isSuccessful) {
            return body
        }

        // A refusal that carries no parseable envelope is still a refusal; it is reported with the
        // status rather than being mistaken for success.
        val error = runCatching { decodeApiError(body) }.getOrNull()
            ?: throw ProtocolException(
                ApiErrorCodes.BRIDGE_INTERNAL_ERROR,
                "The Bridge answered $what with HTTP ${response.code}.",
            )

        if (error.code == ApiErrorCodes.DEVICE_UNAUTHORIZED || response.code == 401) {
            throw DeviceUnauthorizedException(error.message)
        }

        throw protocolFailure(error.code, error.message)
    }

    companion object {
        /** The header the credential travels in. */
        const val AUTHORIZATION_HEADER = "Authorization"

        /**
         * Builds a client pinned to the paired Bridge identity that attaches the credential.
         *
         * @param credential the device credential, exactly as the Bridge issued it.
         */
        fun create(
            bridge: PairedBridge,
            credential: ByteArray,
            base: OkHttpClient = OkHttpClient(),
        ): OkHttpBridgeApi {
            val endpoint = requireNotNull(bridge.endpoint) {
                "A paired Bridge without a known address cannot be used for requests."
            }

            val trust = PairedBridgeTrustManagerFactory(bridge.identityCertificateDer)

            val client = base.newBuilder()
                .sslSocketFactory(trust.sslContext().socketFactory, trust.trustManager())
                // Hostname verification is left alone: OkHttp still checks the leaf's SAN against the
                // address, which is what stops a certificate for another host being accepted here.
                .addInterceptor(BearerCredentialInterceptor(credential))
                .build()

            return OkHttpBridgeApi(client, endpoint.httpsBaseUrl)
        }
    }
}

/**
 * Attaches the device credential to every request the pinned client makes.
 *
 * The token is read once into a string and never formatted into a message, a scope or a log line.
 */
internal class BearerCredentialInterceptor(credential: ByteArray) : Interceptor {

    private val header: String = "Bearer " + credential.toString(Charsets.UTF_8)

    override fun intercept(chain: Interceptor.Chain): Response =
        chain.proceed(
            chain.request().newBuilder()
                .header(OkHttpBridgeApi.AUTHORIZATION_HEADER, header)
                .build(),
        )

    /** Never renders the credential. */
    override fun toString(): String = "BearerCredentialInterceptor(credential=<redacted>)"
}

/**
 * The credential interceptor, for the live-connection client.
 *
 * It is a factory rather than a public class so the header-building rule stays in one place: both
 * the REST client and the WebSocket client authenticate through this exact interceptor, and neither
 * can invent its own way of attaching the credential.
 */
fun bearerInterceptor(credential: ByteArray): Interceptor = BearerCredentialInterceptor(credential)
