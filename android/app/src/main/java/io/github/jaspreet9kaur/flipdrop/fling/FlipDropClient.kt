package io.github.jaspreet9kaur.flipdrop.fling

import android.content.Context
import android.graphics.Bitmap
import android.graphics.ImageDecoder
import android.net.ConnectivityManager
import android.net.Uri
import android.os.SystemClock
import android.provider.OpenableColumns
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import org.json.JSONObject
import java.io.ByteArrayOutputStream
import java.io.IOException
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.Inet4Address
import java.net.InetAddress
import java.net.SocketTimeoutException
import java.net.URL
import java.net.URLEncoder
import java.security.MessageDigest
import java.security.SecureRandom
import java.security.cert.CertificateException
import java.security.cert.X509Certificate
import javax.net.ssl.HostnameVerifier
import javax.net.ssl.HttpsURLConnection
import javax.net.ssl.SSLContext
import javax.net.ssl.SSLHandshakeException
import javax.net.ssl.SSLSocketFactory
import javax.net.ssl.X509TrustManager

/**
 * Client for the FlipDrop laptop receiver (see docs/flipdrop-protocol.md).
 * Traffic stays on the local Wi-Fi: UDP discovery, then HTTPS with a pinned certificate.
 */
object FlipDropClient {
    const val UDP_PORT = 47802
    private const val MAX_BYTES = 64L * 1024 * 1024
    private val FP = Regex("^[0-9a-f]{64}$")

    data class Laptop(val name: String, val host: String, val port: Int, val fp: String)

    /** Android 17's runtime permission for talking to devices on the local network. */
    const val LOCAL_NETWORK_PERMISSION = "android.permission.ACCESS_LOCAL_NETWORK"

    /** True when this Android version has the local-network permission and it isn't granted yet. */
    fun needsLocalNetworkPermission(context: Context): Boolean {
        val defined = runCatching { context.packageManager.getPermissionInfo(LOCAL_NETWORK_PERMISSION, 0) }.isSuccess
        return defined && context.checkSelfPermission(LOCAL_NETWORK_PERMISSION) != android.content.pm.PackageManager.PERMISSION_GRANTED
    }

    class FlipDropException(message: String) : Exception(message)

    /** Broadcasts a discovery ping and collects replies for [timeoutMs]. */
    suspend fun discover(context: Context, timeoutMs: Long = 2500): List<Laptop> = withContext(Dispatchers.IO) {
        val found = LinkedHashMap<String, Laptop>()
        DatagramSocket().use { s ->
            s.broadcast = true
            s.soTimeout = 250
            val payload = "FLIPDROP_DISCOVER_V1".toByteArray(Charsets.US_ASCII)
            // Broadcast first. Many home routers drop broadcasts between Wi-Fi bands or
            // clients, so also send a direct (unicast) ping to every address on the subnet.
            val targets = (
                listOf(InetAddress.getByName("255.255.255.255")) +
                    subnetBroadcasts(context) +
                    subnetHosts(context)
                ).distinct()
            val deadline = SystemClock.elapsedRealtime() + timeoutMs
            var nextSend = 0L
            val buf = ByteArray(2048)
            while (SystemClock.elapsedRealtime() < deadline) {
                if (SystemClock.elapsedRealtime() >= nextSend) {
                    targets.forEach { addr ->
                        runCatching { s.send(DatagramPacket(payload, payload.size, addr, UDP_PORT)) }
                    }
                    nextSend = SystemClock.elapsedRealtime() + 700
                }
                val pkt = DatagramPacket(buf, buf.size)
                try {
                    s.receive(pkt)
                } catch (_: SocketTimeoutException) {
                    continue
                }
                runCatching {
                    val j = JSONObject(String(pkt.data, 0, pkt.length, Charsets.UTF_8))
                    val fp = j.getString("fp").lowercase()
                    if (j.optString("app") == "flipdrop" && j.optInt("v") == 1 && FP.matches(fp)) {
                        val host = pkt.address.hostAddress ?: return@runCatching
                        found[fp] = Laptop(
                            name = j.optString("name", "Laptop").take(64),
                            host = host, // Always the address the reply came from, never from the JSON.
                            port = j.optInt("port", 47801).coerceIn(1, 65535),
                            fp = fp,
                        )
                    }
                }
            }
        }
        found.values.toList()
    }

    /** Exchanges the 6-digit code shown on the laptop for a long-lived device token. */
    suspend fun pair(laptop: Laptop, code: String, deviceName: String, deviceId: String): String =
        withContext(Dispatchers.IO) {
            val body = JSONObject()
                .put("code", code)
                .put("deviceName", deviceName.take(64))
                .put("deviceId", deviceId)
                .toString().toByteArray()
            val c = open(laptop.host, laptop.port, laptop.fp, "/v1/pair")
            try {
                c.requestMethod = "POST"
                c.doOutput = true
                c.setRequestProperty("Content-Type", "application/json")
                c.setFixedLengthStreamingMode(body.size)
                c.outputStream.use { it.write(body) }
                when (val code2 = c.responseCode) {
                    200 -> {
                        val j = JSONObject(c.inputStream.bufferedReader().readText())
                        j.getString("token").also { if (it.length < 32) throw FlipDropException("Laptop sent a bad token") }
                    }
                    401 -> throw FlipDropException("Wrong code. Check the code on the laptop and try again.")
                    429 -> throw FlipDropException("Too many tries. The laptop made a new code; use that one.")
                    else -> throw FlipDropException("Laptop said HTTP $code2")
                }
            } finally {
                c.disconnect()
            }
        }

    /**
     * Sends one photo. If the laptop moved to a new IP, rediscovers it by its pinned
     * fingerprint and retries once. Returns the file name the laptop saved it as.
     */
    suspend fun sendPhoto(
        context: Context,
        uri: Uri,
        velocityDp: Float,
        xFraction: Float,
    ): String = withContext(Dispatchers.IO) {
        val paired = PairingStore.load(context) ?: throw FlipDropException("Pair your laptop first")
        val (bytes, mime) = prepare(context, uri)
        val name = displayName(context, uri)
        try {
            upload(paired, bytes, mime, name, velocityDp, xFraction)
        } catch (e: IOException) {
            if (e is SSLHandshakeException) throw FlipDropException("Laptop identity changed. Pair again for safety.")
            val again = discover(context, 2000).firstOrNull { it.fp == paired.fp }
                ?: throw FlipDropException("Can't reach ${paired.name}. Is FlipDrop running on the laptop?")
            val updated = paired.copy(host = again.host, port = again.port)
            PairingStore.save(context, updated)
            upload(updated, bytes, mime, name, velocityDp, xFraction)
        }
    }

    private fun upload(p: PairingStore.Paired, bytes: ByteArray, mime: String, name: String, velocity: Float, x: Float): String {
        val c = open(p.host, p.port, p.fp, "/v1/photos")
        try {
            c.requestMethod = "POST"
            c.doOutput = true
            c.readTimeout = 60_000
            c.setRequestProperty("Authorization", "Bearer ${p.token}")
            c.setRequestProperty("Content-Type", mime)
            c.setRequestProperty("X-File-Name", URLEncoder.encode(name.take(120), "UTF-8"))
            c.setRequestProperty("X-Fling-Velocity", "%.1f".format(java.util.Locale.US, velocity))
            c.setRequestProperty("X-Fling-X", "%.3f".format(java.util.Locale.US, x.coerceIn(0f, 1f)))
            c.setFixedLengthStreamingMode(bytes.size)
            c.outputStream.use { it.write(bytes) }
            return when (val code = c.responseCode) {
                200 -> JSONObject(c.inputStream.bufferedReader().readText()).optString("saved", "photo")
                401 -> throw FlipDropException("The laptop no longer trusts this phone. Pair again.")
                413 -> throw FlipDropException("Photo is too large (max 64 MB)")
                415 -> throw FlipDropException("The laptop rejected this file type")
                403 -> throw FlipDropException("The laptop refused the connection")
                else -> throw FlipDropException("Laptop said HTTP $code")
            }
        } finally {
            c.disconnect()
        }
    }

    /** JPEG and PNG go as they are. Anything else (HEIC, WebP, …) is re-encoded to JPEG. */
    private fun prepare(context: Context, uri: Uri): Pair<ByteArray, String> {
        val cr = context.contentResolver
        val type = cr.getType(uri)?.lowercase()
        if (type == "image/jpeg" || type == "image/png") {
            val bytes = cr.openInputStream(uri)?.use { input ->
                val out = ByteArrayOutputStream()
                val buf = ByteArray(64 * 1024)
                var total = 0L
                while (true) {
                    val n = input.read(buf)
                    if (n < 0) break
                    total += n
                    if (total > MAX_BYTES) throw FlipDropException("Photo is too large (max 64 MB)")
                    out.write(buf, 0, n)
                }
                out.toByteArray()
            } ?: throw FlipDropException("Couldn't read that photo")
            return bytes to type
        }
        val bmp = ImageDecoder.decodeBitmap(ImageDecoder.createSource(cr, uri)) { decoder, _, _ ->
            decoder.allocator = ImageDecoder.ALLOCATOR_SOFTWARE
        }
        val out = ByteArrayOutputStream()
        bmp.compress(Bitmap.CompressFormat.JPEG, 92, out)
        bmp.recycle()
        return out.toByteArray() to "image/jpeg"
    }

    private fun displayName(context: Context, uri: Uri): String =
        runCatching {
            context.contentResolver.query(uri, arrayOf(OpenableColumns.DISPLAY_NAME), null, null, null)?.use {
                if (it.moveToFirst()) it.getString(0) else null
            }
        }.getOrNull() ?: "photo.jpg"

    private fun open(host: String, port: Int, fp: String, path: String): HttpsURLConnection {
        val c = URL("https", host, port, path).openConnection() as HttpsURLConnection
        c.sslSocketFactory = pinnedFactory(fp)
        // Hostname checks don't apply to a bare LAN IP. Identity comes from the pinned
        // certificate fingerprint instead, which is stricter.
        c.hostnameVerifier = HostnameVerifier { _, _ -> true }
        c.connectTimeout = 4000
        c.readTimeout = 20_000
        c.useCaches = false
        return c
    }

    private fun pinnedFactory(expectedFp: String): SSLSocketFactory {
        val tm = object : X509TrustManager {
            override fun checkClientTrusted(chain: Array<X509Certificate>, authType: String) =
                throw CertificateException("client certs not used")

            override fun checkServerTrusted(chain: Array<X509Certificate>, authType: String) {
                if (chain.isEmpty()) throw CertificateException("no certificate")
                val got = sha256Hex(chain[0].encoded)
                if (!MessageDigest.isEqual(got.toByteArray(), expectedFp.toByteArray())) {
                    throw CertificateException("certificate pin mismatch")
                }
            }

            override fun getAcceptedIssuers(): Array<X509Certificate> = emptyArray()
        }
        return SSLContext.getInstance("TLS").apply { init(null, arrayOf(tm), SecureRandom()) }.socketFactory
    }

    private fun sha256Hex(b: ByteArray): String =
        MessageDigest.getInstance("SHA-256").digest(b).joinToString("") { "%02x".format(it) }

    /** Every host address on the phone's own Wi-Fi subnet (only for /22 or smaller, max 1022 hosts). */
    private fun subnetHosts(context: Context): List<InetAddress> = runCatching {
        val cm = context.getSystemService(ConnectivityManager::class.java)
        val lp = cm.getLinkProperties(cm.activeNetwork) ?: return@runCatching emptyList()
        lp.linkAddresses.flatMap { la ->
            val a = la.address as? Inet4Address ?: return@flatMap emptyList()
            val prefix = la.prefixLength
            if (prefix !in 22..30) return@flatMap emptyList()
            val ip = a.address.fold(0) { acc, byte -> (acc shl 8) or (byte.toInt() and 0xff) }
            val mask = -1 shl (32 - prefix)
            val network = ip and mask
            val count = (1 shl (32 - prefix)) - 2
            (1..count).map { network + it }.filter { it != ip }.map { h ->
                InetAddress.getByAddress(byteArrayOf((h ushr 24).toByte(), (h ushr 16).toByte(), (h ushr 8).toByte(), h.toByte()))
            }
        }
    }.getOrDefault(emptyList())

    /** Asks one specific address directly (for typing in the laptop's IP by hand). */
    suspend fun probe(host: String, timeoutMs: Long = 2000): Laptop? = withContext(Dispatchers.IO) {
        val addr = runCatching { InetAddress.getByName(host) }.getOrNull() as? Inet4Address ?: return@withContext null
        DatagramSocket().use { s ->
            s.soTimeout = timeoutMs.toInt()
            val payload = "FLIPDROP_DISCOVER_V1".toByteArray(Charsets.US_ASCII)
            runCatching { s.send(DatagramPacket(payload, payload.size, addr, UDP_PORT)) }
            val buf = ByteArray(2048)
            val pkt = DatagramPacket(buf, buf.size)
            runCatching {
                s.receive(pkt)
                val j = JSONObject(String(pkt.data, 0, pkt.length, Charsets.UTF_8))
                val fp = j.getString("fp").lowercase()
                if (j.optString("app") == "flipdrop" && j.optInt("v") == 1 && FP.matches(fp)) {
                    Laptop(j.optString("name", "Laptop").take(64), pkt.address.hostAddress ?: host, j.optInt("port", 47801).coerceIn(1, 65535), fp)
                } else null
            }.getOrNull()
        }
    }

    private fun subnetBroadcasts(context: Context): List<InetAddress> = runCatching {
        val cm = context.getSystemService(ConnectivityManager::class.java)
        val lp = cm.getLinkProperties(cm.activeNetwork) ?: return@runCatching emptyList()
        lp.linkAddresses.mapNotNull { la ->
            val a = la.address as? Inet4Address ?: return@mapNotNull null
            val prefix = la.prefixLength
            if (prefix !in 8..30) return@mapNotNull null
            val ip = a.address.fold(0) { acc, byte -> (acc shl 8) or (byte.toInt() and 0xff) }
            val mask = -1 shl (32 - prefix)
            val bc = ip or mask.inv()
            InetAddress.getByAddress(byteArrayOf((bc ushr 24).toByte(), (bc ushr 16).toByte(), (bc ushr 8).toByte(), bc.toByte()))
        }
    }.getOrDefault(emptyList())
}
