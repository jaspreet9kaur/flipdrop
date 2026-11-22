package io.github.jaspreet9kaur.flipdrop.fling

import android.content.Context
import android.net.ConnectivityManager
import android.net.Uri
import android.os.SystemClock
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.withContext
import kotlin.coroutines.coroutineContext
import org.json.JSONObject
import java.io.File
import java.io.FileInputStream
import java.io.FileOutputStream
import java.io.IOException
import java.io.InputStream
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
import java.util.concurrent.ConcurrentHashMap
import javax.net.ssl.HostnameVerifier
import javax.net.ssl.HttpsURLConnection
import javax.net.ssl.SSLContext
import javax.net.ssl.SSLHandshakeException
import javax.net.ssl.SSLSocketFactory
import javax.net.ssl.X509TrustManager

/**
 * Client for the FlipDrop laptop receiver (see docs/flipdrop-protocol.md).
 * Traffic stays on the local Wi-Fi: UDP discovery, then HTTPS with a pinned certificate.
 *
 * v1.1 adds streamed uploads of any file type (protocol v2), with a v1 `/v1/photos`
 * fallback for images when the PC still runs FlipDrop 1.0.
 */
object FlipDropClient {
    const val UDP_PORT = 47802

    /** 4 GiB — the hard upload ceiling enforced on both sides. */
    const val MAX_FILE_BYTES = 4L * 1024 * 1024 * 1024

    private const val PHOTO_FALLBACK_MAX = 64L * 1024 * 1024
    private const val CHUNK = 256 * 1024
    private val FP = Regex("^[0-9a-f]{64}$")
    private val IMAGE_MIMES = setOf("image/jpeg", "image/png", "image/webp", "image/gif")

    data class Laptop(val name: String, val host: String, val port: Int, val fp: String)

    /** Android 17's runtime permission for talking to devices on the local network. */
    const val LOCAL_NETWORK_PERMISSION = "android.permission.ACCESS_LOCAL_NETWORK"

    /** True when this Android version has the local-network permission and it isn't granted yet. */
    fun needsLocalNetworkPermission(context: Context): Boolean {
        val defined = runCatching { context.packageManager.getPermissionInfo(LOCAL_NETWORK_PERMISSION, 0) }.isSuccess
        return defined && context.checkSelfPermission(LOCAL_NETWORK_PERMISSION) != android.content.pm.PackageManager.PERMISSION_GRANTED
    }

    class FlipDropException(message: String) : Exception(message)

    /** Result of a successful send: the name the PC saved it as, its kind and final size. */
    data class SendResult(val savedName: String, val kind: String, val size: Long)

    /**
     * Broadcasts discovery pings and collects replies until [timeoutMs] elapses, or returns the
     * moment the PC with fingerprint [stopWhenFp] answers. Broadcast + subnet broadcast go out
     * every 400 ms; a unicast subnet sweep (only /24 and smaller) is added from the second round.
     */
    suspend fun discover(context: Context, timeoutMs: Long = 2500, stopWhenFp: String? = null): List<Laptop> =
        withContext(Dispatchers.IO) {
            val wanted = stopWhenFp?.lowercase()
            val found = LinkedHashMap<String, Laptop>()
            DatagramSocket().use { s ->
                s.broadcast = true
                s.soTimeout = 200
                val payload = "FLIPDROP_DISCOVER_V1".toByteArray(Charsets.US_ASCII)
                // Cheap, every-round targets: the global broadcast and each subnet broadcast.
                val broadcasts = (listOf(InetAddress.getByName("255.255.255.255")) + subnetBroadcasts(context)).distinct()
                // Expensive, only used from the second round: a unicast sweep of the subnet.
                val sweep = subnetHosts(context)
                val deadline = SystemClock.elapsedRealtime() + timeoutMs
                var nextSend = 0L
                var round = 0
                val buf = ByteArray(2048)
                while (SystemClock.elapsedRealtime() < deadline) {
                    if (SystemClock.elapsedRealtime() >= nextSend) {
                        broadcasts.forEach { addr ->
                            runCatching { s.send(DatagramPacket(payload, payload.size, addr, UDP_PORT)) }
                        }
                        if (round >= 1) {
                            sweep.forEach { addr ->
                                runCatching { s.send(DatagramPacket(payload, payload.size, addr, UDP_PORT)) }
                            }
                        }
                        round++
                        nextSend = SystemClock.elapsedRealtime() + 400
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
                        // v stays 1 for 1.0 compatibility; pv (optional) marks a 1.1 PC.
                        if (j.optString("app") == "flipdrop" && j.optInt("v") == 1 && FP.matches(fp)) {
                            val host = pkt.address.hostAddress ?: return@runCatching
                            found[fp] = Laptop(
                                name = j.optString("name", "Laptop").take(64),
                                host = host, // Always the address the reply came from, never from the JSON.
                                port = j.optInt("port", 47801).coerceIn(1, 65535),
                                fp = fp,
                            )
                            if (wanted != null && fp == wanted) return@use
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
     * Streams any file to the paired PC without re-encoding or buffering it whole in memory.
     * Only `content://` URIs are accepted. If the PC moved to a new IP, it is rediscovered by
     * its pinned fingerprint and the send retried. Reports progress at most every 100 ms.
     */
    suspend fun sendFile(
        context: Context,
        uri: Uri,
        velocityDp: Float = 0f,
        xFraction: Float = 0.5f,
        onProgress: (sent: Long, total: Long) -> Unit = { _, _ -> },
    ): SendResult = withContext(Dispatchers.IO) {
        if (uri.scheme != "content") throw FlipDropException("Only files shared from another app can be sent")
        val app = context.applicationContext
        val paired = PairingStore.load(app) ?: throw FlipDropException("Pair your PC first")
        val meta = FileInfo.of(app, uri)
        if (meta.size > MAX_FILE_BYTES) throw FlipDropException("That file is larger than the 4 GB limit")

        var temp: File? = null
        val size: Long
        val opener: () -> InputStream
        if (meta.size in 0..MAX_FILE_BYTES && isTrustedSizeProvider(uri)) {
            // MediaStore / local document providers report an exact, stable size we can trust.
            size = meta.size
            opener = { app.contentResolver.openInputStream(uri) ?: throw FlipDropException("Couldn't read that file") }
        } else {
            // Size unknown (-1), or a provider whose advertised size we can't trust (e.g. a cloud
            // document provider that streams on demand): spool to cache so the Content-Length is
            // the real byte count and the bytes can't shift under us between sizing and streaming.
            val t = spool(app, uri)
            temp = t
            size = t.length()
            opener = { FileInputStream(t) }
        }
        try {
            try {
                streamUpload(paired, meta, size, opener, velocityDp, xFraction, onProgress)
            } catch (e: IOException) {
                if (e is SSLHandshakeException) throw FlipDropException("Your PC's identity changed. Pair again for safety.")
                val updated = reconnect(app, paired)
                    ?: throw FlipDropException("Can't reach ${paired.name}. Is FlipDrop running on your PC?")
                // The retry runs outside the first IOException handler, so map its failures to
                // friendly messages too instead of leaking a raw Java exception to the user.
                try {
                    streamUpload(updated, meta, size, opener, velocityDp, xFraction, onProgress)
                } catch (e2: IOException) {
                    if (e2 is SSLHandshakeException) throw FlipDropException("Your PC's identity changed. Pair again for safety.")
                    throw FlipDropException("Can't reach ${updated.name}. Is FlipDrop running on your PC?")
                }
            }
        } finally {
            temp?.delete()
        }
    }

    /**
     * True when the provider gives a size we can rely on as an exact, stable Content-Length:
     * MediaStore (`media`) and the on-device document providers (`com.android.providers.*`).
     * Everything else (cloud/third-party providers) is spooled first.
     */
    private fun isTrustedSizeProvider(uri: Uri): Boolean {
        val a = uri.authority ?: return false
        return a == "media" || a.startsWith("com.android.providers.")
    }

    /** Kept for callers that only need the saved name; delegates to [sendFile]. */
    suspend fun sendPhoto(context: Context, uri: Uri, velocityDp: Float, xFraction: Float): String =
        sendFile(context, uri, velocityDp, xFraction).savedName

    /** The 8-char security code (protocol 5.5) both sides derive from the PC's fingerprint. */
    fun securityCode(fp: String): String {
        val digest = MessageDigest.getInstance("SHA-256").digest(("flipdrop-sas:$fp").toByteArray(Charsets.UTF_8))
        val hex = digest.joinToString("") { "%02x".format(it) }.uppercase().take(8)
        return hex.substring(0, 4) + "-" + hex.substring(4, 8)
    }

    // --- internals -----------------------------------------------------------------------------

    /** POSTs to /v2/files, honouring 503 back-off and falling back to /v1/photos on 404. */
    private suspend fun streamUpload(
        p: PairingStore.Paired,
        meta: FileInfo.Meta,
        size: Long,
        opener: () -> InputStream,
        velocity: Float,
        x: Float,
        onProgress: (Long, Long) -> Unit,
    ): SendResult {
        var attempt = 0
        while (true) {
            val c = open(p.host, p.port, p.fp, "/v2/files")
            c.requestMethod = "POST"
            c.doOutput = true
            c.connectTimeout = 3_000
            c.readTimeout = 120_000
            c.setRequestProperty("Authorization", "Bearer ${p.token}")
            c.setRequestProperty("Content-Type", meta.mime ?: "application/octet-stream")
            c.setRequestProperty("X-File-Name", encodeFileNameHeader(meta.name.take(255)))
            // Ask the PC to answer before we stream the body: a busy PC can return 503 off the
            // headers, so we don't push the whole file only to be told to back off.
            c.setRequestProperty("Expect", "100-continue")
            c.setRequestProperty("X-Fling-Velocity", "%.1f".format(java.util.Locale.US, velocity))
            c.setRequestProperty("X-Fling-X", "%.3f".format(java.util.Locale.US, x.coerceIn(0f, 1f)))
            c.setFixedLengthStreamingMode(size)

            val code = try {
                pump(opener, c, size, onProgress)
                c.responseCode
            } catch (e: IOException) {
                // Couldn't reach the PC / write the body: let the caller rediscover and retry.
                runCatching { c.disconnect() }
                throw e
            } catch (e: Throwable) {
                // Size mismatch (file changed) or cancellation: tidy up and propagate as-is, so
                // these never get routed into reconnect/rediscovery.
                runCatching { c.disconnect() }
                throw e
            }

            when (code) {
                200 -> return success(c, meta, size, onProgress)
                401 -> { drain(c); throw FlipDropException("Your PC no longer trusts this phone. Pair again.") }
                400, 411 -> { drain(c); throw FlipDropException("The transfer was interrupted. Please try again.") }
                413 -> { drain(c); throw FlipDropException("That file is larger than the 4 GB limit") }
                415 -> { drain(c); throw FlipDropException("Your PC rejected this file type") }
                507 -> { drain(c); throw FlipDropException("Your PC's disk is full") }
                503 -> {
                    val retryAfter = c.getHeaderFieldInt("Retry-After", 2).coerceIn(0, 5)
                    drain(c)
                    // Re-send the body at most once; the file was streamed, so repeated re-sends are
                    // expensive. A coroutine delay (not Thread.sleep) keeps the IO thread free.
                    if (attempt >= 1) throw FlipDropException("Your PC is busy. Please try again in a moment.")
                    attempt++
                    if (retryAfter > 0) delay(retryAfter * 1000L)
                    // loop: open a fresh connection (the socket is back in the pool).
                }
                404 -> { drain(c); return fallbackPhoto(p, meta, size, opener, velocity, x, onProgress) }
                else -> { drain(c); throw FlipDropException("Your PC said HTTP $code") }
            }
        }
    }

    /** v1 image path, used when the PC runs FlipDrop 1.0 (no /v2/files). Same streaming code. */
    private suspend fun fallbackPhoto(
        p: PairingStore.Paired,
        meta: FileInfo.Meta,
        size: Long,
        opener: () -> InputStream,
        velocity: Float,
        x: Float,
        onProgress: (Long, Long) -> Unit,
    ): SendResult {
        val mime = meta.mime?.lowercase()
        if (mime !in IMAGE_MIMES || size > PHOTO_FALLBACK_MAX) {
            throw FlipDropException("Update FlipDrop on your PC to send this file type")
        }
        val c = open(p.host, p.port, p.fp, "/v1/photos")
        c.requestMethod = "POST"
        c.doOutput = true
        c.connectTimeout = 3_000
        c.readTimeout = 120_000
        c.setRequestProperty("Authorization", "Bearer ${p.token}")
        c.setRequestProperty("Content-Type", mime)
        c.setRequestProperty("X-File-Name", encodeFileNameHeader(meta.name.take(255)))
        c.setRequestProperty("X-Fling-Velocity", "%.1f".format(java.util.Locale.US, velocity))
        c.setRequestProperty("X-Fling-X", "%.3f".format(java.util.Locale.US, x.coerceIn(0f, 1f)))
        c.setFixedLengthStreamingMode(size)

        val code = try {
            pump(opener, c, size, onProgress)
            c.responseCode
        } catch (e: IOException) {
            runCatching { c.disconnect() }
            throw e
        } catch (e: Throwable) {
            runCatching { c.disconnect() }
            throw e
        }
        return when (code) {
            200 -> {
                val j = runCatching { JSONObject(readInput(c)) }.getOrNull()
                onProgress(size, size)
                SendResult(j?.optString("saved")?.takeIf { it.isNotEmpty() } ?: meta.name, "image", size)
            }
            401 -> { drain(c); throw FlipDropException("Your PC no longer trusts this phone. Pair again.") }
            413 -> { drain(c); throw FlipDropException("Photo is too large (max 64 MB)") }
            415 -> { drain(c); throw FlipDropException("Your PC rejected this file type") }
            else -> { drain(c); throw FlipDropException("Your PC said HTTP $code") }
        }
    }

    /** Reads a 200 body; any hiccup reading it does not undo the upload that already succeeded. */
    private fun success(c: HttpsURLConnection, meta: FileInfo.Meta, size: Long, onProgress: (Long, Long) -> Unit): SendResult {
        val j = runCatching { JSONObject(readInput(c)) }.getOrNull()
        onProgress(size, size)
        return SendResult(
            savedName = j?.optString("saved")?.takeIf { it.isNotEmpty() } ?: meta.name,
            kind = j?.optString("kind")?.takeIf { it.isNotEmpty() } ?: meta.kind.name.lowercase(),
            size = j?.optLong("size", size) ?: size,
        )
    }

    /**
     * Streams [opener] into the request body in 256 KiB chunks, reporting at most every 100 ms.
     * The bytes actually read are counted and compared with [total] (the declared Content-Length):
     * if the source grows or shrinks mid-send (a provider changed the file under us), the transfer
     * is failed with a clear message rather than left to the fixed-length stream to abort with a
     * raw IOException that would be mistaken for a lost connection. Checks for cancellation every
     * chunk so an in-flight send stops promptly.
     */
    private suspend fun pump(opener: () -> InputStream, c: HttpsURLConnection, total: Long, onProgress: (Long, Long) -> Unit) {
        onProgress(0, total)
        opener().use { input ->
            c.outputStream.use { out ->
                val buf = ByteArray(CHUNK)
                var sent = 0L
                var lastReport = SystemClock.elapsedRealtime()
                while (true) {
                    coroutineContext.ensureActive()
                    val n = input.read(buf)
                    if (n < 0) break
                    if (sent + n > total) {
                        // The source is longer than the length we committed to: the file changed.
                        throw FlipDropException("The file changed while sending — try again")
                    }
                    out.write(buf, 0, n)
                    sent += n
                    val now = SystemClock.elapsedRealtime()
                    if (now - lastReport >= 100) {
                        onProgress(sent, total)
                        lastReport = now
                    }
                }
                if (sent < total) {
                    // The source ended before the length we committed to: the file changed.
                    throw FlipDropException("The file changed while sending — try again")
                }
                out.flush()
            }
        }
    }

    /**
     * RFC 3986 percent-encoding for the `X-File-Name` header. [URLEncoder] does
     * application/x-www-form-urlencoded, which turns spaces into `+`; the PC decodes with
     * `Uri.UnescapeDataString`, which leaves `+` untouched, so spaces would survive as literal
     * `+`. Converting `+` to `%20` makes both sides agree.
     */
    private fun encodeFileNameHeader(name: String): String =
        URLEncoder.encode(name, "UTF-8").replace("+", "%20")

    /** Reads and closes the response body so the socket returns to the keep-alive pool. */
    private fun readInput(c: HttpsURLConnection): String =
        c.inputStream.use { it.readBytes().toString(Charsets.UTF_8) }

    /** Drains the (small, JSON) error body and returns the connection to the pool. No disconnect. */
    private fun drain(c: HttpsURLConnection) {
        runCatching { (c.errorStream ?: c.inputStream)?.use { it.readBytes() } }
    }

    /** Learns the exact IP again: quick ping to the last host first, then discover by fingerprint. */
    private suspend fun reconnect(context: Context, p: PairingStore.Paired): PairingStore.Paired? {
        probe(p.host, 800)?.takeIf { it.fp == p.fp }?.let { again ->
            val updated = p.copy(host = again.host, port = again.port)
            PairingStore.save(context, updated)
            return updated
        }
        val again = discover(context, timeoutMs = 2000, stopWhenFp = p.fp).firstOrNull { it.fp == p.fp } ?: return null
        val updated = p.copy(host = again.host, port = again.port)
        PairingStore.save(context, updated)
        return updated
    }

    /** Copies an unknown-length stream to a private cache file so we can send an exact length. */
    private fun spool(context: Context, uri: Uri): File {
        val dir = File(context.cacheDir, "fling-spool").apply { mkdirs() }
        val tmp = File.createTempFile("fling_", ".part", dir)
        try {
            (context.contentResolver.openInputStream(uri) ?: throw FlipDropException("Couldn't read that file")).use { input ->
                FileOutputStream(tmp).use { out ->
                    val buf = ByteArray(CHUNK)
                    var total = 0L
                    while (true) {
                        val n = input.read(buf)
                        if (n < 0) break
                        total += n
                        if (total > MAX_FILE_BYTES) throw FlipDropException("That file is larger than the 4 GB limit")
                        out.write(buf, 0, n)
                    }
                }
            }
        } catch (e: Throwable) {
            tmp.delete()
            throw e
        }
        return tmp
    }

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

    // One SSLSocketFactory per fingerprint, so HttpsURLConnection keeps connections alive and
    // resumes TLS sessions across sends instead of handshaking from scratch every time.
    private val factoryCache = ConcurrentHashMap<String, SSLSocketFactory>()

    private fun pinnedFactory(expectedFp: String): SSLSocketFactory =
        factoryCache.getOrPut(expectedFp) { buildPinnedFactory(expectedFp) }

    private fun buildPinnedFactory(expectedFp: String): SSLSocketFactory {
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

    /** Host addresses on the phone's own Wi-Fi subnet, only for /24 and smaller (max 254 hosts). */
    private fun subnetHosts(context: Context): List<InetAddress> = runCatching {
        val cm = context.getSystemService(ConnectivityManager::class.java)
        val lp = cm.getLinkProperties(cm.activeNetwork) ?: return@runCatching emptyList()
        lp.linkAddresses.flatMap { la ->
            val a = la.address as? Inet4Address ?: return@flatMap emptyList()
            val prefix = la.prefixLength
            if (prefix !in 24..30) return@flatMap emptyList()
            val ip = a.address.fold(0) { acc, byte -> (acc shl 8) or (byte.toInt() and 0xff) }
            val mask = -1 shl (32 - prefix)
            val network = ip and mask
            val count = (1 shl (32 - prefix)) - 2
            (1..count).map { network + it }.filter { it != ip }.map { h ->
                InetAddress.getByAddress(byteArrayOf((h ushr 24).toByte(), (h ushr 16).toByte(), (h ushr 8).toByte(), h.toByte()))
            }
        }
    }.getOrDefault(emptyList())

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
