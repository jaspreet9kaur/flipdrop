package io.github.jaspreet9kaur.flipdrop.fling

import android.content.Context
import android.provider.Settings
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.util.Base64
import java.security.KeyStore
import java.util.UUID
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec

/** The paired laptop. The token is encrypted with a hardware-backed Android Keystore key. */
object PairingStore {
    data class Paired(val name: String, val host: String, val port: Int, val fp: String, val token: String)

    private const val PREFS = "flipdrop"
    private const val KEY_ALIAS = "flipdrop_token_key"

    fun load(ctx: Context): Paired? {
        val p = ctx.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
        val enc = p.getString("token", null) ?: return null
        val token = runCatching { decrypt(enc) }.getOrNull() ?: return null
        return Paired(
            name = p.getString("name", "Laptop") ?: "Laptop",
            host = p.getString("host", null) ?: return null,
            port = p.getInt("port", 47801),
            fp = p.getString("fp", null) ?: return null,
            token = token,
        )
    }

    fun save(ctx: Context, v: Paired) {
        ctx.getSharedPreferences(PREFS, Context.MODE_PRIVATE).edit()
            .putString("name", v.name)
            .putString("host", v.host)
            .putInt("port", v.port)
            .putString("fp", v.fp)
            .putString("token", encrypt(v.token))
            .apply()
    }

    fun clear(ctx: Context) {
        ctx.getSharedPreferences(PREFS, Context.MODE_PRIVATE).edit()
            .remove("name").remove("host").remove("port").remove("fp").remove("token").apply()
    }

    fun deviceId(ctx: Context): String {
        val p = ctx.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
        return p.getString("deviceId", null) ?: UUID.randomUUID().toString().also {
            p.edit().putString("deviceId", it).apply()
        }
    }

    fun deviceName(ctx: Context): String =
        Settings.Global.getString(ctx.contentResolver, Settings.Global.DEVICE_NAME)
            ?: android.os.Build.MODEL ?: "Galaxy Z Flip5"

    private fun key(): SecretKey {
        val ks = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
        (ks.getEntry(KEY_ALIAS, null) as? KeyStore.SecretKeyEntry)?.let { return it.secretKey }
        val gen = KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, "AndroidKeyStore")
        gen.init(
            KeyGenParameterSpec.Builder(KEY_ALIAS, KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT)
                .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
                .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                .setKeySize(256)
                .build(),
        )
        return gen.generateKey()
    }

    private fun encrypt(plain: String): String {
        val c = Cipher.getInstance("AES/GCM/NoPadding")
        c.init(Cipher.ENCRYPT_MODE, key())
        val ct = c.doFinal(plain.toByteArray())
        return Base64.encodeToString(c.iv + ct, Base64.NO_WRAP)
    }

    private fun decrypt(enc: String): String {
        val all = Base64.decode(enc, Base64.NO_WRAP)
        val iv = all.copyOfRange(0, 12)
        val ct = all.copyOfRange(12, all.size)
        val c = Cipher.getInstance("AES/GCM/NoPadding")
        c.init(Cipher.DECRYPT_MODE, key(), GCMParameterSpec(128, iv))
        return String(c.doFinal(ct))
    }
}
