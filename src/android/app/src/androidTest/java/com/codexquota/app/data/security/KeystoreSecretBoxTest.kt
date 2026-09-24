package com.codexquota.app.data.security

import androidx.test.ext.junit.runners.AndroidJUnit4
import javax.crypto.AEADBadTagException
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertNotEquals
import kotlin.test.assertNotNull
import kotlin.test.assertTrue
import org.junit.runner.RunWith

/**
 * The Keystore-backed secret box, on a real device.
 *
 * This is the one part of the credential path the JVM cannot test: the Android Keystore does not
 * exist off-device, so the JVM tests prove everything above the [SecretBox] interface while the real
 * box is only exercised here. That makes this test the only place the actual at-rest protection of
 * the device credential is demonstrated rather than assumed.
 */
@RunWith(AndroidJUnit4::class)
class KeystoreSecretBoxTest {

    /** A distinct alias per test, so one test cannot inherit another's key. */
    private fun box(suffix: String) = KeystoreSecretBox(alias = "cqb_test_${suffix}_v1")

    @Test
    fun sealingAndOpeningRoundTripsThePlaintext() {
        val box = box("roundtrip")
        val plaintext = "sample-device-token-not-a-secret".toByteArray(Charsets.UTF_8)

        val sealed = box.seal(plaintext)

        assertEquals(
            "sample-device-token-not-a-secret",
            box.open(sealed).toString(Charsets.UTF_8),
        )
    }

    @Test
    fun theSealedBytesDoNotContainThePlaintext() {
        val box = box("atrest")
        val plaintext = "sample-device-token-not-a-secret".toByteArray(Charsets.UTF_8)

        val sealed = box.seal(plaintext)

        // Searching the ciphertext itself is the honest form of "the credential has no durable
        // plaintext representation": nothing downstream can write what is not there.
        assertFalse(
            sealed.ciphertext.toString(Charsets.UTF_8).contains("sample-device-token-not-a-secret"),
            "the sealed ciphertext must not contain the plaintext",
        )
        assertFalse(sealed.ciphertext.contentEquals(plaintext))
        assertNotEquals(0, sealed.iv.size, "GCM needs an initialisation vector")
    }

    @Test
    fun aTamperedCiphertextFailsAuthenticationRatherThanReturningGarbage() {
        val box = box("tamper")
        val sealed = box.seal("sample-device-token-not-a-secret".toByteArray(Charsets.UTF_8))

        // Flip one bit. GCM is authenticated, so this must fail rather than decrypt to something.
        val tampered = sealed.copy(ciphertext = sealed.ciphertext.copyOf().also {
            it[it.size / 2] = (it[it.size / 2].toInt() xor 0x01).toByte()
        })

        val failure = assertFailsWith<Exception> { box.open(tampered) }

        assertTrue(
            failure is AEADBadTagException || failure.cause is AEADBadTagException,
            "a tampered ciphertext must fail the authentication tag, but failed with $failure",
        )
    }

    @Test
    fun aTamperedIvFailsAuthentication() {
        val box = box("iv")
        val sealed = box.seal("sample-device-token-not-a-secret".toByteArray(Charsets.UTF_8))

        val tampered = sealed.copy(iv = sealed.iv.copyOf().also { it[0] = (it[0].toInt() xor 0x01).toByte() })

        assertFailsWith<Exception> { box.open(tampered) }
    }

    @Test
    fun aBoxWithADifferentKeyCannotOpenAnotherBoxsSecret() {
        // Two aliases are two Keystore keys, which is the closest a test can get to "somebody else's
        // key without exporting anything.
        val first = box("owner_a")
        val second = box("owner_b")

        val sealed = first.seal("sample-device-token-not-a-secret".toByteArray(Charsets.UTF_8))

        assertFailsWith<Exception> { second.open(sealed) }
    }

    @Test
    fun sealingTheSamePlaintextTwiceProducesDifferentCiphertext() {
        val box = box("freshness")

        val first = box.seal("sample-device-token-not-a-secret".toByteArray(Charsets.UTF_8))
        val second = box.seal("sample-device-token-not-a-secret".toByteArray(Charsets.UTF_8))

        // A fresh IV per seal: identical ciphertexts would leak that the credential had not changed.
        assertFalse(first.iv.contentEquals(second.iv), "each seal must use a fresh IV")
    }

    @Test
    fun theSealedFormNeverRendersItsContents() {
        val box = box("tostring")
        val sealed = box.seal("sample-device-token-not-a-secret".toByteArray(Charsets.UTF_8))

        assertFalse(sealed.toString().contains("sample-device-token"))
        assertTrue(sealed.toString().contains("ciphertext="))
    }

    @Test
    fun theCredentialIsStoredSealedAndNeverInPlaintext() {
        // The end-to-end form of the property: what a store writes to disk must not contain the
        // credential, and must still be openable afterwards.
        val storage = MemoryStorage()
        val store = PairedBridgeStore(storage = storage, secretBox = box("store"))

        val sealed = store.sealCredential("sample-device-token-not-a-secret".toByteArray(Charsets.UTF_8))

        val bridge = PairedBridge(
            bridgeId = "bridge-1",
            displayName = "Android phone",
            identityCertificateDer = byteArrayOf(1, 2, 3),
            identityFingerprint = "A1B2C3D4E5F60718293A4B5C6D7E8F90",
            endpoint = BridgeEndpoint("192.168.1.20", 47821),
            credential = sealed,
        )

        store.save(bridge)

        val written = assertNotNull(storage.content, "the pairing must have been written")
        assertFalse(
            written.contains("sample-device-token-not-a-secret"),
            "the persisted pairing document must not contain the credential",
        )

        val reloaded = assertNotNull(store.load(), "the pairing must be readable")
        assertEquals(
            "sample-device-token-not-a-secret",
            assertNotNull(store.openCredential(reloaded), "the credential must be openable").toString(Charsets.UTF_8),
        )
    }

    private class MemoryStorage : SecretStorage {
        var content: String? = null

        override fun read(): String? = content

        override fun write(content: String) {
            this.content = content
        }

        override fun clear() {
            content = null
        }
    }
}
