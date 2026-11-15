package io.github.jaspreet9kaur.flipdrop.fling

import android.content.Intent
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.widget.Toast
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.animation.core.Animatable
import androidx.compose.animation.core.FastOutLinearInEasing
import androidx.compose.animation.core.spring
import androidx.compose.animation.core.tween
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Text
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.produceState
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.shadow
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalConfiguration
import androidx.compose.ui.unit.dp
import kotlinx.coroutines.launch

/** "Share → Fling to laptop": the shared photos rise off the screen and land on the laptop. */
class ShareToLaptopActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val uris = sharedUris(intent)
        if (uris.isEmpty()) {
            finish()
            return
        }
        if (PairingStore.load(this) == null) {
            Toast.makeText(this, "Pair your laptop first", Toast.LENGTH_LONG).show()
            startActivity(Intent(this, FlingActivity::class.java).putExtra(FlingActivity.EXTRA_PAIR, true))
            finish()
            return
        }
        // A real share hands us read access to exactly what it shares. Without that, some
        // app launched us directly, hoping we'd read photos with our own permission: ask first.
        val granted = (intent.flags and Intent.FLAG_GRANT_READ_URI_PERMISSION) != 0
        setContent {
            var confirmed by remember { mutableStateOf(granted) }
            var current by remember { mutableStateOf(0) }
            var message by remember { mutableStateOf("Flinging…") }
            val rise = remember { Animatable(0f) }
            val appear = remember { Animatable(0f) }
            val screenH = LocalConfiguration.current.screenHeightDp

            if (!confirmed) {
                androidx.compose.material3.AlertDialog(
                    onDismissRequest = { finish() },
                    title = { Text("Send to your laptop?") },
                    text = { Text("Another app asked FlipDrop to send ${uris.size} photo${if (uris.size == 1) "" else "s"} to your paired laptop.") },
                    confirmButton = { androidx.compose.material3.TextButton(onClick = { confirmed = true }) { Text("Send") } },
                    dismissButton = { androidx.compose.material3.TextButton(onClick = { finish() }) { Text("Cancel") } },
                )
                return@setContent
            }

            LaunchedEffect(Unit) {
                var ok = 0
                var lastError: String? = null
                for ((i, uri) in uris.withIndex()) {
                    current = i
                    rise.snapTo(0f)
                    appear.snapTo(0f)
                    appear.animateTo(1f, spring(0.65f, 380f))
                    val send = launch {
                        try {
                            FlipDropClient.sendPhoto(this@ShareToLaptopActivity, uri, 1600f, 0.5f)
                            ok++
                        } catch (e: Exception) {
                            lastError = e.message
                        }
                    }
                    rise.animateTo(1f, tween(480, easing = FastOutLinearInEasing))
                    send.join()
                }
                message = if (lastError == null) "Sent $ok to laptop" else "Sent $ok of ${uris.size}: $lastError"
                Toast.makeText(this@ShareToLaptopActivity, message, Toast.LENGTH_LONG).show()
                finish()
            }

            Box(Modifier.fillMaxSize().background(Color.Black.copy(alpha = 0.35f * (1f - rise.value)))) {
                val uri = uris[current]
                val bmp by produceState(ThumbCache.peek(uri), uri) {
                    value = runCatching {
                        contentResolver.loadThumbnail(uri, android.util.Size(600, 600), null)
                    }.getOrNull()?.asImageBitmap()
                }
                Box(
                    Modifier
                        .align(Alignment.Center)
                        .graphicsLayer {
                            translationY = -rise.value * screenH * density
                            val s = (0.6f + 0.4f * appear.value) * (1f - 0.4f * rise.value)
                            scaleX = s
                            scaleY = s
                            alpha = appear.value * (1f - rise.value * rise.value)
                        }
                        .size(220.dp)
                        .shadow(24.dp, RoundedCornerShape(22.dp))
                        .clip(RoundedCornerShape(22.dp))
                        .background(Color(0xFF222226)),
                ) {
                    bmp?.let { Image(it, null, contentScale = ContentScale.Crop, modifier = Modifier.fillMaxSize()) }
                }
                Text(
                    message,
                    color = Color.White,
                    modifier = Modifier.align(Alignment.BottomCenter).navigationBarsPadding().padding(bottom = 32.dp),
                )
            }
        }
    }

    private fun sharedUris(i: Intent): List<Uri> {
        val list = when (i.action) {
            Intent.ACTION_SEND -> listOfNotNull(
                if (Build.VERSION.SDK_INT >= 33) i.getParcelableExtra(Intent.EXTRA_STREAM, Uri::class.java)
                else @Suppress("DEPRECATION") i.getParcelableExtra(Intent.EXTRA_STREAM),
            )
            Intent.ACTION_SEND_MULTIPLE ->
                (if (Build.VERSION.SDK_INT >= 33) i.getParcelableArrayListExtra(Intent.EXTRA_STREAM, Uri::class.java)
                else @Suppress("DEPRECATION") i.getParcelableArrayListExtra(Intent.EXTRA_STREAM)) ?: emptyList()
            else -> emptyList()
        }
        // Only content:// URIs granted to us by the sharing app. Never file:// paths.
        return list.filter { it.scheme == "content" }.take(50)
    }
}
