package com.codexquota.app.data.security

import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import java.security.KeyStore
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec

/**
 * Seals and opens a secret using a key that never leaves the Android Keystore.
 *
 * The key is generated on first use under a fixed alias, is not exportable, and is only ever used
 * for AES/GCM. The plaintext credential therefore has no durable representation: the bytes written
 * to disk are ciphertext, and the key needed to read them cannot be read out of the device.
 *
 * This is the only part of the credential path that is not unit-testable on the JVM, so it is kept
 * as thin as possible and everything above it depends on [SecretBox] instead.
 */
interface SecretBox {
    /** Encrypts a secret. A fresh IV is used every time. */
    fun seal(plaintext: ByteArray): SealedSecret

    /** Decrypts a secret, or throws when the ciphertext or key is not the one that produced it. */
    fun open(sealed: SealedSecret): ByteArray
}

/** The Keystore-backed [SecretBox]. */
class KeystoreSecretBox(
    private val alias: String = DEFAULT_ALIAS,
) : SecretBox {

    override fun seal(plaintext: ByteArray): SealedSecret {
        val cipher = Cipher.getInstance(TRANSFORMATION)
        cipher.init(Cipher.ENCRYPT_MODE, key())

        return SealedSecret(ciphertext = cipher.doFinal(plaintext), iv = cipher.iv)
    }

    override fun open(sealed: SealedSecret): ByteArray {
        val cipher = Cipher.getInstance(TRANSFORMATION)
        cipher.init(Cipher.DECRYPT_MODE, key(), GCMParameterSpec(TAG_BITS, sealed.iv))

        return cipher.doFinal(sealed.ciphertext)
    }

    /** The Keystore key, generated once and reused. */
    private fun key(): SecretKey {
        val keyStore = KeyStore.getInstance(PROVIDER).apply { load(null) }

        (keyStore.getEntry(alias, null) as? KeyStore.SecretKeyEntry)?.let { return it.secretKey }

        val generator = KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, PROVIDER)
        generator.init(
            KeyGenParameterSpec.Builder(
                alias,
                KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT,
            )
                // GCM with no padding: an authenticated mode, so a tampered ciphertext fails to open
                // rather than decrypting to something plausible.
                .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
                .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                // Deliberately not requiring user authentication: the credential has to be usable by
                // the background worker, which has no user present.
                .setUserAuthenticationRequired(false)
                .build(),
        )

        return generator.generateKey()
    }

    companion object {
        /** The documented alias for the V1 device-secret key. */
        const val DEFAULT_ALIAS = "codex_quota_device_secret_v1"

        private const val PROVIDER = "AndroidKeyStore"
        private const val TRANSFORMATION = "AES/GCM/NoPadding"
        private const val TAG_BITS = 128
    }
}
