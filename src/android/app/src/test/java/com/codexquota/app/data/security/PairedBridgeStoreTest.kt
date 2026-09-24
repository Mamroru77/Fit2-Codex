package com.codexquota.app.data.security

import java.util.Base64
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue

/**
 * The storage layer must never leave a plaintext credential anywhere it can be read.
 *
 * The sample credential below is a fixture, not a real one, and it is deliberately distinctive so a
 * substring search over everything the store wrote is meaningful.
 */
class PairedBridgeStoreTest {

    private val sampleCredential = "super-secret-device-token".toByteArray(Charsets.UTF_8)

    private val identityCertificate = "not-a-real-certificate".toByteArray(Charsets.UTF_8)

    private fun store(
        storage: InMemorySecretStorage = InMemorySecretStorage(),
        box: SecretBox = XorSecretBox(),
    ): Pair<PairedBridgeStore, InMemorySecretStorage> = PairedBridgeStore(storage, box) to storage

    private fun bridgeWithCredential(store: PairedBridgeStore) = PairedBridge(
        bridgeId = "bridge-1",
        displayName = "OPPO Find X8",
        identityCertificateDer = identityCertificate,
        identityFingerprint = "A1B2C3D4E5F60718",
        endpoint = BridgeEndpoint("192.168.1.20", 47821),
        credential = store.sealCredential(sampleCredential),
    )

    @Test
    fun theStoredDocumentNeverContainsThePlaintextCredential() {
        val (store, storage) = store()

        store.save(bridgeWithCredential(store))

        val written = assertNotNull(storage.content, "the store wrote nothing")
        assertFalse(
            written.contains("super-secret-device-token"),
            "the plaintext credential appears in the stored document",
        )
        assertFalse(
            written.contains(Base64.getEncoder().encodeToString(sampleCredential)),
            "the base64 of the plaintext credential appears in the stored document",
        )
    }

    @Test
    fun aSavedPairingRoundTrips() {
        val (store, _) = store()
        val saved = bridgeWithCredential(store)

        store.save(saved)
        val loaded = assertNotNull(store.load())

        assertEquals(saved.bridgeId, loaded.bridgeId)
        assertEquals(saved.displayName, loaded.displayName)
        assertEquals(saved.identityFingerprint, loaded.identityFingerprint)
        assertTrue(loaded.identityCertificateDer.contentEquals(identityCertificate))
        assertEquals(BridgeEndpoint("192.168.1.20", 47821), loaded.endpoint)
        assertTrue(loaded.hasCredential)
    }

    @Test
    fun theCredentialCanBeOpenedAfterARestart() {
        val (store, storage) = store()
        store.save(bridgeWithCredential(store))

        // A new store over the same bytes is what a process restart looks like.
        val restarted = PairedBridgeStore(storage, XorSecretBox())
        val loaded = assertNotNull(restarted.load())

        val credential = assertNotNull(restarted.openCredential(loaded))
        assertTrue(credential.contentEquals(sampleCredential))
    }

    @Test
    fun aCredentialSealedWithAnotherKeyIsReportedAsUnavailableRatherThanThrowing() {
        val (store, storage) = store()
        store.save(bridgeWithCredential(store))

        // A device wipe or a restored backup leaves a ciphertext whose key is gone. That is a
        // re-pair, not a crash.
        val foreign = PairedBridgeStore(storage, XorSecretBox(key = 0x11))
        val loaded = assertNotNull(foreign.load())

        assertNull(foreign.openCredential(loaded))
    }

    @Test
    fun anUnreadableDocumentIsTreatedAsNoPairing() {
        val (store, storage) = store()
        storage.content = "{ this is not json"

        assertNull(store.load())
    }

    @Test
    fun anImpossibleEndpointCostsTheAddressButNotThePairing() {
        // The address is a location; the identity and the credential are the pairing. A corrupt
        // address should cost a rediscovery, not a re-pair.
        val (store, storage) = store()
        store.save(bridgeWithCredential(store))
        storage.content = assertNotNull(storage.content)
            .replace("\"port\":47821", "\"port\":0")

        val loaded = assertNotNull(store.load())

        assertNull(loaded.endpoint)
        assertEquals("A1B2C3D4E5F60718", loaded.identityFingerprint)
        assertTrue(loaded.hasCredential)
    }

    @Test
    fun clearingForgetsEverything() {
        val (store, storage) = store()
        store.save(bridgeWithCredential(store))

        store.clear()

        assertNull(storage.content)
        assertNull(store.load())
    }

    @Test
    fun aBridgeWithoutACredentialIsStillUsableAsAPairing() {
        val (store, _) = store()
        val identityOnly = PairedBridge(
            bridgeId = "bridge-1",
            displayName = "OPPO Find X8",
            identityCertificateDer = identityCertificate,
            identityFingerprint = "A1B2C3D4E5F60718",
            endpoint = null,
            credential = null,
        )

        store.save(identityOnly)
        val loaded = assertNotNull(store.load())

        assertFalse(loaded.hasCredential)
        assertNull(store.openCredential(loaded))
    }

    @Test
    fun thePairedBridgeNeverRendersItsCredential() {
        val (store, _) = store()
        val text = bridgeWithCredential(store).toString()

        assertFalse(text.contains("super-secret-device-token"))
        assertTrue(text.contains("hasCredential=true"))
    }
}

/** In-memory [SecretStorage] for tests. */
internal class InMemorySecretStorage : SecretStorage {
    var content: String? = null

    override fun read(): String? = content

    override fun write(content: String) {
        this.content = content
    }

    override fun clear() {
        content = null
    }
}

/**
 * A stand-in for the Keystore-backed box.
 *
 * It is a real transformation, not an identity function, so a test that asserts the plaintext is
 * absent from the stored bytes is testing the store rather than the fake.
 */
internal class XorSecretBox(private val key: Byte = 0x5A) : SecretBox {
    override fun seal(plaintext: ByteArray): SealedSecret = SealedSecret(
        // A key tag in front of the payload stands in for GCM's authentication tag, so opening with
        // the wrong key fails instead of returning plausible-looking garbage.
        ciphertext = byteArrayOf(key) +
            ByteArray(plaintext.size) { (plaintext[it].toInt() xor key.toInt()).toByte() },
        iv = byteArrayOf(1, 2, 3, 4),
    )

    override fun open(sealed: SealedSecret): ByteArray {
        require(sealed.ciphertext.isNotEmpty() && sealed.ciphertext[0] == key) {
            "This secret was not sealed with this key."
        }

        val body = sealed.ciphertext.copyOfRange(1, sealed.ciphertext.size)

        return ByteArray(body.size) { (body[it].toInt() xor key.toInt()).toByte() }
    }
}
