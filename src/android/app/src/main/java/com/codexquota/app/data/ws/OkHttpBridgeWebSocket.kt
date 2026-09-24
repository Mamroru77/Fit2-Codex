package com.codexquota.app.data.ws

import com.codexquota.app.data.api.DataProtocolException
import com.codexquota.app.data.api.DeviceUnauthorizedException
import com.codexquota.app.data.api.ProtocolException
import com.codexquota.app.data.api.WsEnvelopeDto
import com.codexquota.app.data.api.WsHelloDto
import com.codexquota.app.data.api.decodeV1
import com.codexquota.app.data.api.toDomain
import com.codexquota.app.data.security.BridgeEndpoint
import com.codexquota.app.data.security.PairedBridge
import com.codexquota.app.data.security.PairedBridgeTrustManagerFactory
import java.io.IOException
import java.security.cert.CertificateException
import kotlinx.coroutines.channels.awaitClose
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.callbackFlow
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.Response
import okhttp3.WebSocket
import okhttp3.WebSocketListener

/**
 * The live connection, over OkHttp.
 *
 * The client it is given is pinned to the paired Bridge identity and already carries the device
 * credential, so the upgrade request is authenticated and can only reach the paired Bridge.
 *
 * A frame that does not parse is not a reason to tear the connection down: it is reported and the
 * stream continues, because dropping a healthy connection over one bad frame would be a worse
 * failure than the bad frame.
 */
class OkHttpBridgeWebSocket(
    private val client: OkHttpClient,
    private val endpoint: BridgeEndpoint,
) : BridgeWebSocket {

    private var socket: WebSocket? = null

    override fun events(): Flow<BridgeWebSocketEvent> = callbackFlow {
        val request = Request.Builder().url(endpoint.webSocketUrl).build()

        val listener = object : WebSocketListener() {
            override fun onMessage(webSocket: WebSocket, text: String) {
                when (val event = parseFrame(text)) {
                    null -> Unit
                    else -> trySend(event)
                }
            }

            override fun onClosing(webSocket: WebSocket, code: Int, reason: String) {
                webSocket.close(NORMAL_CLOSURE, null)
            }

            override fun onClosed(webSocket: WebSocket, code: Int, reason: String) {
                trySend(BridgeWebSocketEvent.Closed(reason.ifBlank { null }))
                close()
            }

            override fun onFailure(webSocket: WebSocket, t: Throwable, response: Response?) {
                trySend(BridgeWebSocketEvent.Failed(translate(t, response)))
                close()
            }
        }

        socket = client.newWebSocket(request, listener)

        awaitClose {
            socket?.cancel()
            socket = null
        }
    }

    override fun close() {
        socket?.close(NORMAL_CLOSURE, null)
        socket = null
    }

    /** Turns one frame into an event, or `null` when the frame carries nothing to act on. */
    private fun parseFrame(text: String): BridgeWebSocketEvent? {
        val envelope = try {
            decodeV1<WsEnvelopeDto>(text, "websocket frame")
        } catch (_: ProtocolException) {
            return null
        }

        return when (envelope.type) {
            "hello" -> {
                val hello = runCatching { decodeV1<WsHelloDto>(text, "hello frame") }.getOrNull()

                BridgeWebSocketEvent.Hello(
                    apiVersion = hello?.apiVersion ?: "v1",
                    bridgeVersion = hello?.bridgeVersion.orEmpty(),
                )
            }

            "quota.updated" -> {
                val sequence = envelope.sequence
                val payload = envelope.payload

                if (sequence == null || payload == null) {
                    null
                } else {
                    try {
                        BridgeWebSocketEvent.QuotaUpdated(sequence, payload.toDomain())
                    } catch (_: DataProtocolException) {
                        // A malformed snapshot is dropped rather than applied: applying half of it
                        // would be inventing quota.
                        null
                    }
                }
            }

            "bridge.shutdown" -> BridgeWebSocketEvent.Shutdown

            else -> null
        }
    }

    /**
     * Names a failure for what it is.
     *
     * Two of these are terminal and must not look like an ordinary drop:
     *
     * - a **verification failure** means the Bridge is not the paired one, so retrying would keep
     *   offering a credential to an impostor; and
     * - a **401** means the credential itself is no longer accepted, which needs the user to pair
     *   again rather than to wait for the network.
     */
    private fun translate(t: Throwable, response: Response?): Throwable = when {
        response?.code == 401 -> DeviceUnauthorizedException(
            "The Bridge no longer accepts this device's credential.",
            t,
        )

        t is CertificateException -> SecurityFailure(t.message ?: "The Bridge identity does not match.", t)
        t is javax.net.ssl.SSLException -> SecurityFailure(t.message ?: "The Bridge identity does not match.", t)
        else -> t
    }

    companion object {
        private const val NORMAL_CLOSURE = 1000

        /** Builds a live-connection client pinned to the paired Bridge identity. */
        fun create(
            bridge: PairedBridge,
            credential: ByteArray,
            base: OkHttpClient = OkHttpClient(),
        ): OkHttpBridgeWebSocketFactory {
            val trust = PairedBridgeTrustManagerFactory(bridge.identityCertificateDer)

            val client = base.newBuilder()
                .sslSocketFactory(trust.sslContext().socketFactory, trust.trustManager())
                .addInterceptor(com.codexquota.app.data.api.bearerInterceptor(credential))
                .build()

            return OkHttpBridgeWebSocketFactory(client)
        }
    }
}

/** Opens live connections with one pinned, authenticated client. */
class OkHttpBridgeWebSocketFactory(
    private val client: OkHttpClient,
) : BridgeWebSocketFactory {
    override fun connect(endpoint: BridgeEndpoint): BridgeWebSocket =
        OkHttpBridgeWebSocket(client, endpoint)
}
