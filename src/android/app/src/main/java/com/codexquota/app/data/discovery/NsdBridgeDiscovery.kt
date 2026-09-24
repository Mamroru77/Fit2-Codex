package com.codexquota.app.data.discovery

import android.content.Context
import android.net.nsd.NsdManager
import android.net.nsd.NsdServiceInfo
import android.net.wifi.WifiManager
import kotlinx.coroutines.channels.awaitClose
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.callbackFlow

/**
 * DNS-SD discovery of Bridges on the local network.
 *
 * This is a thin adapter over `NsdManager`: everything decision-shaped about discovery lives above
 * it, and the published metadata is only ever used to offer a candidate. It deliberately reads only
 * the keys the spec allows the Bridge to advertise.
 */
class NsdBridgeDiscovery(
    private val context: Context,
) : BridgeDiscovery {

    override fun services(): Flow<DiscoveredService> = callbackFlow {
        val manager = context.getSystemService(Context.NSD_SERVICE) as NsdManager

        // Android gates multicast reception behind a wake lock; without it, mDNS responses are
        // dropped on many devices and discovery silently never finds anything.
        val multicastLock = (context.getSystemService(Context.WIFI_SERVICE) as? WifiManager)
            ?.createMulticastLock(MULTICAST_LOCK_TAG)
            ?.apply { setReferenceCounted(true) }
            ?.also { runCatching { it.acquire() } }

        val listeners = mutableListOf<NsdManager.DiscoveryListener>()
        var resolveAttempts = 0

        val listener = object : NsdManager.DiscoveryListener {
            override fun onDiscoveryStarted(serviceType: String) = Unit

            override fun onServiceFound(service: NsdServiceInfo) {
                if (service.serviceType != SERVICE_TYPE) {
                    return
                }

                // Resolving is one-at-a-time on Android; asking for several in parallel makes the
                // framework return FAILURE_ALREADY_ACTIVE and the extra services are lost.
                if (resolveAttempts > 0) {
                    return
                }

                resolveAttempts++

                @Suppress("DEPRECATION")
                manager.resolveService(
                    service,
                    object : NsdManager.ResolveListener {
                        override fun onResolveFailed(serviceInfo: NsdServiceInfo, errorCode: Int) {
                            resolveAttempts--
                        }

                        override fun onServiceResolved(serviceInfo: NsdServiceInfo) {
                            resolveAttempts--

                            val host = serviceInfo.host?.hostAddress ?: return

                            trySend(
                                DiscoveredService(
                                    serviceName = serviceInfo.serviceName.orEmpty(),
                                    host = host,
                                    port = serviceInfo.port,
                                    bridgeId = serviceInfo.attributes[ATTR_BRIDGE_ID]?.toString(Charsets.UTF_8),
                                    apiVersion = serviceInfo.attributes[ATTR_API_VERSION]?.toString(Charsets.UTF_8),
                                ),
                            )
                        }
                    },
                )
            }

            override fun onServiceLost(service: NsdServiceInfo) = Unit

            override fun onDiscoveryStopped(serviceType: String) = Unit

            override fun onStartDiscoveryFailed(serviceType: String, errorCode: Int) {
                close()
            }

            override fun onStopDiscoveryFailed(serviceType: String, errorCode: Int) = Unit
        }

        listeners += listener
        manager.discoverServices(SERVICE_TYPE, NsdManager.PROTOCOL_DNS_SD, listener)

        awaitClose {
            runCatching { manager.stopServiceDiscovery(listener) }
            runCatching { multicastLock?.release() }
        }
    }

    companion object {
        /** The service type the Bridge advertises. */
        const val SERVICE_TYPE = "_codexquota._tcp"

        private const val MULTICAST_LOCK_TAG = "codexquota-mdns"

        /** The only TXT keys this app reads. */
        private const val ATTR_BRIDGE_ID = "bridgeId"
        private const val ATTR_API_VERSION = "apiVersion"
    }
}
