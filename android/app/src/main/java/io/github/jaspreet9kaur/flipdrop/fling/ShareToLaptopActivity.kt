package io.github.jaspreet9kaur.flipdrop.fling

import android.content.Intent
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.view.HapticFeedbackConstants
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.core.Animatable
import androidx.compose.animation.core.FastOutSlowInEasing
import androidx.compose.animation.core.spring
import androidx.compose.animation.core.tween
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.gestures.detectVerticalDragGestures
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Check
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.darkColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.produceState
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.shadow
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.input.pointer.util.VelocityTracker
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.platform.LocalView
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

private val Bg = Color(0xFF0B0B0D)
private val Surface = Color(0xFF16161C)
private val SurfaceHi = Color(0xFF1F1F27)
private val Accent1 = Color(0xFF8E7BFF)
private val Accent2 = Color(0xFF4FD1FF)
private val Success = Color(0xFF5EE38B)
private val Error = Color(0xFFFF6B6B)
private val TextHi = Color(0xFFF4F4F8)
private val TextMid = Color(0xFFB6B6C2)
private val TextLo = Color(0x80FFFFFF)
private val AccentBrush = Brush.linearGradient(listOf(Accent1, Accent2))

/** "Share → Fling to PC": confirm, then the files stream to the PC through the queue. */
class ShareToLaptopActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val uris = sharedUris(intent)
        if (uris.isEmpty()) {
            finish()
            return
        }
        enableEdgeToEdge()
        setContent {
            MaterialTheme(colorScheme = darkColorScheme()) {
                val activity = this@ShareToLaptopActivity
                val paired = remember { PairingStore.load(activity) }
                Box(Modifier.fillMaxSize().background(Color.Black.copy(alpha = 0.55f))) {
                    if (paired == null) {
                        NotPairedCard(
                            count = uris.size,
                            onOpen = {
                                startActivity(Intent(activity, FlingActivity::class.java).putExtra(FlingActivity.EXTRA_PAIR, true))
                                finish()
                            },
                            onCancel = { finish() },
                            modifier = Modifier.align(Alignment.BottomCenter),
                        )
                    } else {
                        ShareFlow(
                            uris = uris,
                            pcName = paired.name,
                            onFinish = { finish() },
                            modifier = Modifier.align(Alignment.BottomCenter),
                        )
                    }
                }
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
        // Only content:// URIs the sharing app granted us. Never file:// paths.
        return list.filter { it.scheme == "content" }.take(50)
    }
}

@Composable
private fun ShareFlow(uris: List<Uri>, pcName: String, onFinish: () -> Unit, modifier: Modifier = Modifier) {
    val ctx = LocalContext.current
    val view = LocalView.current
    val density = LocalDensity.current
    val scope = rememberCoroutineScope()

    val metas by produceState(emptyList<FileInfo.Meta>(), uris) {
        value = withContext(Dispatchers.IO) { uris.map { FileInfo.of(ctx, it) } }
    }
    var sending by remember { mutableStateOf(false) }
    var ids by remember { mutableStateOf<List<Long>>(emptyList()) }
    val transfers by TransferQueue.items.collectAsStateWithLifecycle()
    val mine = transfers.filter { ids.contains(it.id) }

    val offsetY = remember { Animatable(0f) }
    val tracker = remember { VelocityTracker() }

    fun confirm() {
        if (sending) return
        sending = true
        view.performHapticFeedback(HapticFeedbackConstants.CONFIRM)
        scope.launch(Dispatchers.IO) { ids = TransferQueue.enqueue(ctx, uris, 1600f, 0.5f) }
    }

    val allDone = sending && mine.isNotEmpty() && mine.all { it.state == TransferQueue.State.DONE }
    LaunchedEffect(allDone) {
        if (allDone) { delay(900); onFinish() }
    }

    Column(
        modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(topStart = 28.dp, topEnd = 28.dp))
            .background(Bg)
            .navigationBarsPadding()
            .padding(20.dp),
    ) {
        Box(Modifier.fillMaxWidth().padding(bottom = 10.dp), contentAlignment = Alignment.Center) {
            Box(Modifier.size(width = 38.dp, height = 4.dp).clip(RoundedCornerShape(50)).background(Color.White.copy(alpha = 0.18f)))
        }

        if (!sending) {
            val totalSize = metas.filter { it.size >= 0 }.sumOf { it.size }
            Text("Fling to $pcName", color = TextHi, fontSize = 20.sp, fontWeight = FontWeight.Bold)
            Spacer(Modifier.height(2.dp))
            Text(
                "${uris.size} ${if (uris.size == 1) "item" else "items"}" + if (totalSize > 0) " · ${FileInfo.formatSize(totalSize)}" else "",
                color = TextMid, fontSize = 13.sp,
            )
            Spacer(Modifier.height(18.dp))

            Box(
                Modifier
                    .fillMaxWidth()
                    .height(200.dp)
                    .pointerInput(Unit) {
                        detectVerticalDragGestures(
                            onDragStart = { tracker.resetTracking() },
                            onVerticalDrag = { change, dy ->
                                change.consume()
                                tracker.addPosition(change.uptimeMillis, change.position)
                                scope.launch { offsetY.snapTo((offsetY.value + dy).coerceAtMost(0f)) }
                            },
                            onDragEnd = {
                                val v = tracker.calculateVelocity()
                                val flung = v.y < -700f * density.density || offsetY.value < -with(density) { 120.dp.toPx() }
                                if (flung) {
                                    scope.launch { offsetY.animateTo(-with(density) { 1400.dp.toPx() }, tween(420, easing = FastOutSlowInEasing)) }
                                    confirm()
                                } else {
                                    scope.launch { offsetY.animateTo(0f, spring(0.8f, 320f)) }
                                }
                            },
                        )
                    },
                contentAlignment = Alignment.Center,
            ) {
                PreviewStack(uris, metas, offsetY.value)
            }

            Spacer(Modifier.height(18.dp))
            Text("Flick the stack up, or tap below", color = TextLo, fontSize = 12.sp, textAlign = TextAlign.Center, modifier = Modifier.fillMaxWidth())
            Spacer(Modifier.height(12.dp))
            Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                GhostButton("Cancel", Modifier.weight(1f)) { onFinish() }
                AccentButton("Send to $pcName", Modifier.weight(2f)) { confirm() }
            }
        } else {
            val done = mine.count { it.state == TransferQueue.State.DONE }
            val failed = mine.count { it.state == TransferQueue.State.FAILED }
            Text(
                when {
                    allDone -> "Sent to $pcName ✓"
                    failed > 0 && done + failed == mine.size -> "$done sent · $failed failed"
                    else -> "Sending to $pcName…"
                },
                color = if (allDone) Success else TextHi, fontSize = 20.sp, fontWeight = FontWeight.Bold,
            )
            Spacer(Modifier.height(14.dp))
            LazyColumn(
                Modifier.fillMaxWidth().heightIn(max = 340.dp),
                contentPadding = PaddingValues(bottom = 4.dp),
                verticalArrangement = Arrangement.spacedBy(8.dp),
            ) {
                items(mine, key = { it.id }) { t -> TransferRow(t) { TransferQueue.retry(ctx, t.id) } }
            }
            Spacer(Modifier.height(14.dp))
            AnimatedVisibility(done + failed == mine.size && mine.isNotEmpty()) {
                AccentButton("Done", Modifier.fillMaxWidth()) { onFinish() }
            }
        }
    }
}

@Composable
private fun PreviewStack(uris: List<Uri>, metas: List<FileInfo.Meta>, offsetY: Float) {
    val shown = uris.take(4)
    val extra = uris.size - shown.size
    Box(contentAlignment = Alignment.Center) {
        shown.forEachIndexed { i, uri ->
            val depth = shown.size - 1 - i
            val meta = metas.getOrNull(i)
            Box(
                Modifier
                    .graphicsLayer {
                        translationY = offsetY + depth * with(this) { 10.dp.toPx() }
                        translationX = (i - (shown.size - 1) / 2f) * with(this) { 16.dp.toPx() }
                        rotationZ = (i - (shown.size - 1) / 2f) * 5f
                        val s = 1f - depth * 0.05f
                        scaleX = s
                        scaleY = s
                    },
            ) {
                PreviewCard(uri, meta)
            }
        }
        if (extra > 0) {
            Box(
                Modifier.align(Alignment.BottomEnd).graphicsLayer { translationY = offsetY }.padding(6.dp)
                    .clip(RoundedCornerShape(50)).background(Color.Black.copy(alpha = 0.6f)).padding(horizontal = 10.dp, vertical = 5.dp),
            ) { Text("+$extra", color = Color.White, fontSize = 12.sp, fontWeight = FontWeight.Bold) }
        }
    }
}

@Composable
private fun PreviewCard(uri: Uri, meta: FileInfo.Meta?) {
    val ctx = LocalContext.current
    val kind = meta?.kind ?: FileKind.OTHER
    val showThumb = kind == FileKind.IMAGE || kind == FileKind.VIDEO
    val bmp by produceState(ThumbCache.peek(uri), uri, showThumb) { if (showThumb) value = ThumbCache.load(ctx, uri) }
    Box(
        Modifier.size(140.dp).shadow(22.dp, RoundedCornerShape(20.dp)).clip(RoundedCornerShape(20.dp)).background(SurfaceHi),
        contentAlignment = Alignment.Center,
    ) {
        val b = bmp
        if (showThumb && b != null) {
            Image(b, null, contentScale = ContentScale.Crop, modifier = Modifier.fillMaxSize())
            if (kind == FileKind.VIDEO) {
                Box(Modifier.size(40.dp).clip(CircleShape).background(Color.Black.copy(alpha = 0.4f)), contentAlignment = Alignment.Center) {
                    Icon(Icons.Filled.PlayArrow, null, tint = Color.White, modifier = Modifier.size(26.dp))
                }
            }
        } else {
            Column(Modifier.padding(12.dp), horizontalAlignment = Alignment.CenterHorizontally) {
                KindGlyph(kind, 52.dp, 13.sp)
                Spacer(Modifier.height(8.dp))
                Text(meta?.name ?: "File", color = TextHi, fontSize = 11.sp, maxLines = 2, overflow = TextOverflow.Ellipsis, textAlign = TextAlign.Center)
            }
        }
    }
}

@Composable
private fun TransferRow(t: TransferQueue.Item, onRetry: () -> Unit) {
    val ctx = LocalContext.current
    val showThumb = t.kind == FileKind.IMAGE || t.kind == FileKind.VIDEO
    val bmp by produceState(ThumbCache.peek(t.uri), t.uri, showThumb) { if (showThumb) value = ThumbCache.load(ctx, t.uri) }
    val pct = when (t.state) {
        TransferQueue.State.DONE -> 1f
        TransferQueue.State.FAILED -> 0f
        else -> if (t.size > 0) (t.sent.toFloat() / t.size).coerceIn(0f, 1f) else 0f
    }
    Row(
        Modifier.fillMaxWidth().clip(RoundedCornerShape(14.dp)).background(Surface).padding(10.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Box(Modifier.size(42.dp).clip(RoundedCornerShape(10.dp)).background(SurfaceHi), contentAlignment = Alignment.Center) {
            val b = bmp
            if (showThumb && b != null) Image(b, null, contentScale = ContentScale.Crop, modifier = Modifier.fillMaxSize())
            else KindGlyph(t.kind, 42.dp, 11.sp)
        }
        Spacer(Modifier.width(10.dp))
        Column(Modifier.weight(1f)) {
            Text(t.name, color = TextHi, fontSize = 13.sp, maxLines = 1, overflow = TextOverflow.Ellipsis)
            Spacer(Modifier.height(5.dp))
            when (t.state) {
                TransferQueue.State.DONE -> Text("Done", color = Success, fontSize = 11.sp)
                TransferQueue.State.FAILED -> Text(t.error ?: "Failed", color = Error, fontSize = 11.sp, maxLines = 1, overflow = TextOverflow.Ellipsis)
                TransferQueue.State.QUEUED -> Text("Queued", color = TextLo, fontSize = 11.sp)
                TransferQueue.State.SENDING -> ProgressBar(pct)
            }
        }
        if (t.state == TransferQueue.State.FAILED) {
            Spacer(Modifier.width(8.dp))
            Box(
                Modifier.clip(RoundedCornerShape(50)).background(Accent1.copy(alpha = 0.22f)).clickable(onClick = onRetry).padding(horizontal = 10.dp, vertical = 7.dp),
            ) { Icon(Icons.Filled.Refresh, "Retry", tint = Accent2, modifier = Modifier.size(16.dp)) }
        } else if (t.state == TransferQueue.State.DONE) {
            Spacer(Modifier.width(8.dp))
            Box(Modifier.size(22.dp).clip(CircleShape).background(Success), contentAlignment = Alignment.Center) {
                Icon(Icons.Filled.Check, null, tint = Color.White, modifier = Modifier.size(14.dp))
            }
        }
    }
}

@Composable
private fun NotPairedCard(count: Int, onOpen: () -> Unit, onCancel: () -> Unit, modifier: Modifier = Modifier) {
    Column(
        modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(topStart = 28.dp, topEnd = 28.dp))
            .background(Bg)
            .navigationBarsPadding()
            .padding(24.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
    ) {
        Box(Modifier.size(56.dp).clip(CircleShape).background(AccentBrush), contentAlignment = Alignment.Center) {
            Text("PC", color = Color.White, fontSize = 16.sp, fontWeight = FontWeight.Bold)
        }
        Spacer(Modifier.height(14.dp))
        Text("Pair a PC first", color = TextHi, fontSize = 19.sp, fontWeight = FontWeight.Bold)
        Spacer(Modifier.height(6.dp))
        Text(
            "FlipDrop needs to be paired with your PC before it can fling $count ${if (count == 1) "item" else "items"}.",
            color = TextMid, fontSize = 13.sp, textAlign = TextAlign.Center,
        )
        Spacer(Modifier.height(18.dp))
        Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(10.dp)) {
            GhostButton("Cancel", Modifier.weight(1f)) { onCancel() }
            AccentButton("Open FlipDrop", Modifier.weight(2f)) { onOpen() }
        }
    }
}

// ---- Shared bits -----------------------------------------------------------
private fun kindShort(kind: FileKind): String = when (kind) {
    FileKind.IMAGE -> "IMG"
    FileKind.VIDEO -> "VID"
    FileKind.AUDIO -> "AUD"
    FileKind.PDF -> "PDF"
    FileKind.DOCUMENT -> "DOC"
    FileKind.TEXT -> "TXT"
    FileKind.ARCHIVE -> "ZIP"
    FileKind.APP -> "APK"
    FileKind.OTHER -> "FILE"
}

private fun kindColors(kind: FileKind): List<Color> = when (kind) {
    FileKind.IMAGE -> listOf(Color(0xFF8E7BFF), Color(0xFF4FD1FF))
    FileKind.VIDEO -> listOf(Color(0xFFFF6B9D), Color(0xFF8E7BFF))
    FileKind.AUDIO -> listOf(Color(0xFF4FD1FF), Color(0xFF5EE38B))
    FileKind.PDF -> listOf(Color(0xFFFF6B6B), Color(0xFFFFB86B))
    FileKind.DOCUMENT -> listOf(Color(0xFF6B8CFF), Color(0xFF4FD1FF))
    FileKind.TEXT -> listOf(Color(0xFF9AA0AE), Color(0xFF6B7280))
    FileKind.ARCHIVE -> listOf(Color(0xFFFFB86B), Color(0xFFFFD27A))
    FileKind.APP -> listOf(Color(0xFF5EE38B), Color(0xFF4FD1FF))
    FileKind.OTHER -> listOf(Color(0xFF8E7BFF), Color(0xFF6B7280))
}

@Composable
private fun KindGlyph(kind: FileKind, diameter: androidx.compose.ui.unit.Dp, textSize: androidx.compose.ui.unit.TextUnit) {
    Box(Modifier.size(diameter).clip(CircleShape).background(Brush.linearGradient(kindColors(kind))), contentAlignment = Alignment.Center) {
        Text(kindShort(kind), color = Color.White, fontSize = textSize, fontWeight = FontWeight.Bold)
    }
}

@Composable
private fun ProgressBar(pct: Float) {
    Box(Modifier.fillMaxWidth().height(6.dp).clip(RoundedCornerShape(50)).background(Color.White.copy(alpha = 0.08f))) {
        Box(Modifier.fillMaxWidth(pct).height(6.dp).clip(RoundedCornerShape(50)).background(AccentBrush))
    }
}

@Composable
private fun AccentButton(text: String, modifier: Modifier = Modifier, onClick: () -> Unit) {
    Box(
        modifier.clip(RoundedCornerShape(50)).background(AccentBrush).clickable(onClick = onClick).padding(vertical = 13.dp),
        contentAlignment = Alignment.Center,
    ) { Text(text, color = Color.White, fontSize = 14.sp, fontWeight = FontWeight.SemiBold, maxLines = 1, overflow = TextOverflow.Ellipsis) }
}

@Composable
private fun GhostButton(text: String, modifier: Modifier = Modifier, onClick: () -> Unit) {
    Box(
        modifier.clip(RoundedCornerShape(50)).background(Color.White.copy(alpha = 0.06f)).clickable(onClick = onClick).padding(vertical = 13.dp),
        contentAlignment = Alignment.Center,
    ) { Text(text, color = TextMid, fontSize = 14.sp, fontWeight = FontWeight.SemiBold) }
}
