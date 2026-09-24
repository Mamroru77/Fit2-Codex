package com.codexquota.app.data.pairing

import com.codexquota.app.data.security.BridgeEndpoint
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith

/**
 * A QR code is public — it is photographed and often kept — so the parser is strict about what it
 * will accept and refuses anything it cannot verify against.
 */
class PairingQrPayloadTest {

    private val valid = """
        {"v":"v1","bridgeId":"bridge-1","host":"192.168.1.20","port":47821,
         "pairingId":"pair-1","identityFingerprint":"A1B2C3D4E5F60718293A4B5C6D7E8F90"}
    """.trimIndent()

    @Test
    fun aBridgeCodeParses() {
        val payload = PairingQrPayload.parse(valid)

        assertEquals("bridge-1", payload.bridgeId)
        assertEquals(BridgeEndpoint("192.168.1.20", 47821), payload.endpoint)
        assertEquals("pair-1", payload.pairingId)
        assertEquals("A1B2C3D4E5F60718293A4B5C6D7E8F90", payload.identityFingerprint)
    }

    @Test
    fun theVersionMayBeWrittenWithoutItsPrefix() {
        // The Bridge writes the API path version `v1`; the approved plan names it `1`. Same version.
        val payload = PairingQrPayload.parse(valid.replace("\"v\":\"v1\"", "\"v\":\"1\""))

        assertEquals("bridge-1", payload.bridgeId)
    }

    @Test
    fun aFutureApiVersionIsRefused() {
        assertFailsWith<PairingQrException> {
            PairingQrPayload.parse(valid.replace("\"v\":\"v1\"", "\"v\":\"v2\""))
        }
    }

    @Test
    fun aCodeWithNoIdentityFingerprintIsRefused() {
        // Without a fingerprint there is nothing to verify the Bridge against, so pairing must not
        // start at all rather than proceed unverified.
        val withoutFingerprint = """
            {"v":"v1","bridgeId":"bridge-1","host":"192.168.1.20","port":47821,"pairingId":"pair-1"}
        """.trimIndent()

        assertFailsWith<PairingQrException> { PairingQrPayload.parse(withoutFingerprint) }
    }

    @Test
    fun aCodeWithAnEmptyFingerprintIsRefused() {
        assertFailsWith<PairingQrException> {
            PairingQrPayload.parse(valid.replace("A1B2C3D4E5F60718293A4B5C6D7E8F90", ""))
        }
    }

    @Test
    fun aCodeWithAnUnusablePortIsRefused() {
        assertFailsWith<PairingQrException> {
            PairingQrPayload.parse(valid.replace("\"port\":47821", "\"port\":0"))
        }
    }

    @Test
    fun aCodeWithNoHostIsRefused() {
        assertFailsWith<PairingQrException> {
            PairingQrPayload.parse(valid.replace("\"host\":\"192.168.1.20\"", "\"host\":\"\""))
        }
    }

    @Test
    fun somethingThatIsNotJsonIsRefused() {
        assertFailsWith<PairingQrException> {
            PairingQrPayload.parse("https://example.com/not-a-pairing-code")
        }
    }

    @Test
    fun anUnknownFieldIsIgnored() {
        // Within v1, adding a field is allowed, so a newer Bridge must not break this parser.
        val payload = PairingQrPayload.parse(
            valid.replace("\"v\":\"v1\"", "\"v\":\"v1\",\"futureField\":\"whatever\""),
        )

        assertEquals("bridge-1", payload.bridgeId)
    }
}
