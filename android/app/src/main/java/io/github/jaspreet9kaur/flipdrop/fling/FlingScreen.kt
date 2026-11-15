package io.github.jaspreet9kaur.flipdrop.fling

import android.net.Uri
import android.view.HapticFeedbackConstants
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.core.Animatable
import androidx.compose.animation.core.FastOutLinearInEasing
import androidx.compose.animation.core.VectorConverter
import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.animation.core.spring
import androidx.compose.animation.core.tween
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.slideInVertically
import androidx.compose.animation.slideOutVertically
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.gestures.detectDragGesturesAfterLongPress
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.items
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.produceState
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.shadow
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Rect
import androidx.compose.ui.graphics.BlurEffect
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.ImageBitmap
import androidx.compose.ui.graphics.TileMode
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.input.pointer.util.VelocityTracker
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.layout.boundsInRoot
import androidx.compose.ui.layout.onGloballyPositioned
import androidx.compose.ui.layout.onSizeChanged
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.platform.LocalView
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.unit.IntOffset
import androidx.compose.ui.unit.IntSize
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.compose.LifecycleEventEffect
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlin.math.abs
import kotlin.math.roundToInt

private val Bg = Color(0xFF0B0B0D)
private val Rainbow = listOf(
    Color(0xFFFF6B6B), Color(0xFFFFB86B), Color(0xFFFFF27A),
    Color(0xFF7CF29A), Color(0xFF6BD8FF), Color(0xFF8E7BFF), Color(0xFFE77BFF),
)

private sealed interface SendStatus {
    data object Sending : SendStatus
    data class Landed(val laptop: String) : SendStatus
    data class Failed(val message: String) : SendStatus
}

private data class Drag(val uri: Uri, val bitmap: ImageBitmap?, val cell: Rect, val grab: Offset)

@Composable
fun FlingScreen(onBack: () -> Unit) {
    val ctx = LocalContext.current
    val view = LocalView.current
    val density = LocalDensity.current
    val scope = rememberCoroutineScope()

    var paired by remember { mutableStateOf(PairingStore.load(ctx)) }
    var hasPerm by remember { mutableStateOf(MediaRepo.hasAccess(ctx)) }
    var photos by remember { mutableStateOf<List<Uri>>(emptyList()) }
    var reloadKey by remember { mutableIntStateOf(0) }
    val permLauncher = rememberLauncherForActivityResult(ActivityResultContracts.RequestMultiplePermissions()) {
        hasPerm = MediaRepo.hasAccess(ctx)
        reloadKey++
    }
    LaunchedEffect(Unit) {
        if (!hasPerm) {
            val extra = if (FlipDropClient.needsLocalNetworkPermission(ctx)) arrayOf(FlipDropClient.LOCAL_NETWORK_PERMISSION) else emptyArray()
            permLauncher.launch(MediaRepo.permissions + extra)
        }
    }
    LaunchedEffect(hasPerm, reloadKey) { if (hasPerm) photos = MediaRepo.recent(ctx) }
    LifecycleEventEffect(Lifecycle.Event.ON_RESUME) {
        hasPerm = MediaRepo.hasAccess(ctx)
        reloadKey++
    }

    var drag by remember { mutableStateOf<Drag?>(null) }
    var center by remember { mutableStateOf(Offset.Zero) }
    val lift = remember { Animatable(0f) }
    val fly = remember { Animatable(0f) }
    var status by remember { mutableStateOf<SendStatus?>(null) }
    var showPair by remember { mutableStateOf(false) }
    var rootSize by remember { mutableStateOf(IntSize.Zero) }
    val tracker = remember { VelocityTracker() }

    fun snapBack(d: Drag) {
        scope.launch {
            val a = Animatable(center, Offset.VectorConverter)
            launch { lift.animateTo(0f, spring(0.8f, 500f)) }
            a.animateTo(d.cell.center, spring(0.75f, 420f)) { center = value }
            drag = null
        }
    }

    fun flingAway(d: Drag, v: androidx.compose.ui.unit.Velocity) {
        val p = paired
        if (p == null) {
            status = SendStatus.Failed("Pair your laptop first")
            showPair = true
            snapBack(d)
            return
        }
        view.performHapticFeedback(HapticFeedbackConstants.CONFIRM)
        val vDp = abs(v.y) / density.density
        val xFrac = if (rootSize.width > 0) center.x / rootSize.width else 0.5f
        scope.launch {
            val a = Animatable(center, Offset.VectorConverter)
            val target = Offset(center.x + v.x * 0.12f, -rootSize.height * 0.35f)
            launch { fly.animateTo(1f, tween(420, easing = FastOutLinearInEasing)) }
            a.animateTo(target, tween(420, easing = FastOutLinearInEasing)) { center = value }
            drag = null
            fly.snapTo(0f)
            lift.snapTo(0f)
        }
        status = SendStatus.Sending
        scope.launch {
            status = try {
                FlipDropClient.sendPhoto(ctx, d.uri, vDp, xFrac)
                SendStatus.Landed(p.name)
            } catch (e: Exception) {
                SendStatus.Failed(e.message ?: "Couldn't send")
            }
            paired = PairingStore.load(ctx)
            delay(2800)
            status = null
        }
    }

    Box(
        Modifier
            .fillMaxSize()
            .background(Bg)
            .onSizeChanged { rootSize = it },
    ) {
        val dragging = drag != null
        val blur by animateFloatAsState(if (dragging) 1f else 0f, tween(280), label = "blur")

        Column(
            Modifier
                .fillMaxSize()
                .graphicsLayer {
                    val b = blur * 26f
                    renderEffect = if (b > 0.5f) BlurEffect(b, b, TileMode.Clamp) else null
                    alpha = 1f - 0.3f * blur
                },
        ) {
            Header(paired, onBack = onBack, onPair = { showPair = true }, onUnpair = {
                PairingStore.clear(ctx)
                paired = null
            })
            if (!hasPerm) {
                Column(Modifier.fillMaxWidth().padding(24.dp), horizontalAlignment = Alignment.CenterHorizontally) {
                    Text("Allow photo access to fling pictures to your laptop.", color = Color.White)
                    Spacer(Modifier.height(12.dp))
                    Button(onClick = { permLauncher.launch(MediaRepo.permissions) }) { Text("Allow photos") }
                }
            }
            Text(
                "Press and hold a photo, then fling it up toward your laptop",
                color = Color.White.copy(alpha = 0.45f),
                fontSize = 12.sp,
                modifier = Modifier.padding(horizontal = 16.dp, vertical = 6.dp),
            )
            LazyVerticalGrid(
                columns = GridCells.Fixed(3),
                contentPadding = PaddingValues(start = 2.dp, end = 2.dp, bottom = 80.dp),
                horizontalArrangement = Arrangement.spacedBy(2.dp),
                verticalArrangement = Arrangement.spacedBy(2.dp),
                modifier = Modifier.fillMaxSize(),
            ) {
                items(photos, key = { it.toString() }) { uri ->
                    var cell by remember { mutableStateOf(Rect.Zero) }
                    val bmp by produceState(ThumbCache.peek(uri), uri) { value = ThumbCache.load(ctx, uri) }
                    Box(
                        Modifier
                            .aspectRatio(1f)
                            .onGloballyPositioned { cell = it.boundsInRoot() }
                            .graphicsLayer { alpha = if (drag?.uri == uri) 0f else 1f }
                            .background(Color(0xFF1C1C20))
                            .pointerInput(uri) {
                                detectDragGesturesAfterLongPress(
                                    onDragStart = { off ->
                                        view.performHapticFeedback(HapticFeedbackConstants.LONG_PRESS)
                                        tracker.resetTracking()
                                        drag = Drag(uri, bmp, cell, off)
                                        center = cell.center
                                        scope.launch { lift.animateTo(1f, spring(0.62f, 380f)) }
                                    },
                                    onDrag = { change, _ ->
                                        change.consume()
                                        val inRoot = cell.topLeft + change.position
                                        tracker.addPosition(change.uptimeMillis, inRoot)
                                        center = inRoot - (drag?.grab ?: Offset.Zero) + Offset(cell.width / 2, cell.height / 2)
                                    },
                                    onDragEnd = {
                                        val d = drag ?: return@detectDragGesturesAfterLongPress
                                        val v = tracker.calculateVelocity()
                                        val vyDp = v.y / density.density
                                        val inTopZone = center.y < rootSize.height * 0.2f
                                        if ((vyDp < -700f && abs(v.y) > abs(v.x) * 0.7f) || inTopZone) flingAway(d, v) else snapBack(d)
                                    },
                                    onDragCancel = { drag?.let { snapBack(it) } },
                                )
                            },
                    ) {
                        bmp?.let { Image(it, null, contentScale = ContentScale.Crop, modifier = Modifier.fillMaxSize()) }
                    }
                }
            }
        }

        // Send zone: the top edge glows brighter as the photo gets close.
        drag?.let {
            val closeness = (1f - center.y / (rootSize.height * 0.65f)).coerceIn(0f, 1f)
            SendZone(closeness, paired?.name)
        }

        // The lifted photo.
        drag?.let { d ->
            FloatingPhoto(d, center, lift.value, fly.value)
        }

        // Status pill.
        AnimatedVisibility(
            visible = status != null,
            enter = fadeIn() + slideInVertically { it },
            exit = fadeOut() + slideOutVertically { it },
            modifier = Modifier.align(Alignment.BottomCenter).navigationBarsPadding().padding(bottom = 20.dp),
        ) {
            StatusPill(status)
        }
    }

    if (showPair) {
        PairDialog(
            onDismiss = { showPair = false },
            onPaired = {
                paired = it
                showPair = false
            },
        )
    }
}

@Composable
private fun Header(
    paired: PairingStore.Paired?,
    onBack: () -> Unit,
    onPair: () -> Unit,
    onUnpair: () -> Unit,
) {
    var confirmUnpair by remember { mutableStateOf(false) }
    Row(
        Modifier.statusBarsPadding().fillMaxWidth().padding(horizontal = 4.dp, vertical = 4.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        IconButton(onClick = onBack) { Icon(Icons.AutoMirrored.Filled.ArrowBack, "Back", tint = Color.White) }
        Text("Fling", color = Color.White, fontSize = 22.sp, fontWeight = FontWeight.SemiBold)
        Spacer(Modifier.weight(1f))
        if (paired == null) {
            Button(onClick = onPair) { Text("Pair laptop") }
        } else {
            Row(
                Modifier
                    .clip(RoundedCornerShape(50))
                    .background(Color.White.copy(alpha = 0.08f))
                    .pointerInput(Unit) {
                        detectTapGestures(onLongPress = { confirmUnpair = true })
                    }
                    .padding(horizontal = 12.dp, vertical = 6.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Box(Modifier.size(8.dp).clip(CircleShape).background(Color(0xFF5EE38B)))
                Spacer(Modifier.width(8.dp))
                Text(paired.name, color = Color.White, fontSize = 13.sp)
            }
        }
        Spacer(Modifier.width(8.dp))
    }
    if (confirmUnpair && paired != null) {
        AlertDialog(
            onDismissRequest = { confirmUnpair = false },
            title = { Text("Unpair ${paired.name}?") },
            text = { Text("This phone will forget the laptop. You can pair again any time.") },
            confirmButton = { TextButton(onClick = { confirmUnpair = false; onUnpair() }) { Text("Unpair") } },
            dismissButton = { TextButton(onClick = { confirmUnpair = false }) { Text("Cancel") } },
        )
    }
}

@Composable
private fun SendZone(closeness: Float, laptop: String?) {
    Box(Modifier.fillMaxWidth().height(220.dp)) {
        Canvas(Modifier.fillMaxSize()) {
            val a = 0.25f + 0.75f * closeness
            drawRect(
                Brush.verticalGradient(
                    listOf(Color(0xFFB89CFF).copy(alpha = 0.55f * a), Color(0xFF6BD8FF).copy(alpha = 0.25f * a), Color.Transparent),
                ),
            )
            drawOval(
                Brush.radialGradient(
                    Rainbow.map { it.copy(alpha = 0.55f * a) } + Color.Transparent,
                    center = Offset(size.width / 2, 0f),
                    radius = size.width * (0.55f + 0.25f * closeness),
                ),
                topLeft = Offset(-size.width * 0.2f, -size.height * 0.9f),
                size = androidx.compose.ui.geometry.Size(size.width * 1.4f, size.height * 1.6f),
            )
        }
        Text(
            if (laptop != null) "Fling up to send to $laptop" else "Pair a laptop to fling",
            color = Color.White.copy(alpha = 0.6f + 0.4f * closeness),
            fontSize = 15.sp,
            fontWeight = FontWeight.Medium,
            modifier = Modifier.align(Alignment.Center).statusBarsPadding(),
        )
    }
}

@Composable
private fun FloatingPhoto(d: Drag, center: Offset, lift: Float, fly: Float) {
    val density = LocalDensity.current
    val cellW = with(density) { d.cell.width.toDp() }
    val sizeDp = cellW + (210.dp - cellW) * lift
    val sizePx = with(density) { sizeDp.toPx() }
    val half = sizePx / 2f

    // Rainbow light trail behind the photo while it rises and flies off.
    if (lift > 0.3f) {
        val trail = with(density) { (90.dp + 420.dp * fly).toPx() }
        Canvas(
            Modifier
                .fillMaxSize()
                .graphicsLayer {
                    renderEffect = BlurEffect(36f, 36f, TileMode.Decal)
                    alpha = (0.35f + 0.65f * fly) * lift
                },
        ) {
            drawRect(
                Brush.verticalGradient(Rainbow.map { it.copy(alpha = 0.85f) } + Color.Transparent, startY = center.y, endY = center.y + half + trail),
                topLeft = Offset(center.x - half * 0.8f, center.y),
                size = androidx.compose.ui.geometry.Size(half * 1.6f, half + trail),
            )
        }
    }

    Box(
        Modifier
            .offset { IntOffset((center.x - half).roundToInt(), (center.y - half).roundToInt()) }
            .size(sizeDp)
            .graphicsLayer {
                val s = 1f - 0.5f * fly
                scaleX = s
                scaleY = s
                rotationX = 18f * fly
                alpha = 1f - fly * fly
            }
            .shadow((28 * lift).dp, RoundedCornerShape(20.dp))
            .clip(RoundedCornerShape((4 + 16 * lift).dp))
            .background(Color(0xFF222226)),
    ) {
        d.bitmap?.let { Image(it, null, contentScale = ContentScale.Crop, modifier = Modifier.fillMaxSize()) }
        // Glass sheen
        Canvas(Modifier.fillMaxSize()) {
            drawRect(Brush.linearGradient(listOf(Color.White.copy(alpha = 0.22f * lift), Color.Transparent), end = Offset(size.width * 0.6f, size.height * 0.6f)))
        }
    }
}

@Composable
private fun StatusPill(status: SendStatus?) {
    val (text, color) = when (status) {
        SendStatus.Sending -> "Sending…" to Color(0xFF8E7BFF)
        is SendStatus.Landed -> "Landed on ${status.laptop} ✓" to Color(0xFF5EE38B)
        is SendStatus.Failed -> status.message to Color(0xFFFF6B6B)
        null -> "" to Color.Transparent
    }
    Row(
        Modifier
            .clip(RoundedCornerShape(50))
            .background(Color(0xFF1E1E24))
            .padding(horizontal = 18.dp, vertical = 12.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        if (status == SendStatus.Sending) {
            CircularProgressIndicator(Modifier.size(16.dp), strokeWidth = 2.dp, color = color)
        } else {
            Box(Modifier.size(10.dp).clip(CircleShape).background(color))
        }
        Spacer(Modifier.width(10.dp))
        Text(text, color = Color.White, fontSize = 14.sp)
    }
}

private sealed interface PairStage {
    data object Searching : PairStage
    data class Found(val laptops: List<FlipDropClient.Laptop>) : PairStage
    data class Code(val laptop: FlipDropClient.Laptop, val error: String? = null, val busy: Boolean = false) : PairStage
}

@Composable
fun PairDialog(onDismiss: () -> Unit, onPaired: (PairingStore.Paired) -> Unit) {
    val ctx = LocalContext.current
    val scope = rememberCoroutineScope()
    var stage by remember { mutableStateOf<PairStage>(PairStage.Searching) }
    var searchNonce by remember { mutableIntStateOf(0) }
    var code by remember { mutableStateOf("") }
    var manualIp by remember { mutableStateOf("") }
    var manualError by remember { mutableStateOf<String?>(null) }
    var needsLan by remember { mutableStateOf(FlipDropClient.needsLocalNetworkPermission(ctx)) }
    val lanLauncher = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) {
        needsLan = FlipDropClient.needsLocalNetworkPermission(ctx)
        searchNonce++
    }

    LaunchedEffect(searchNonce) {
        if (needsLan) {
            // Android 17 blocks all Wi-Fi-local traffic until this is allowed.
            lanLauncher.launch(FlipDropClient.LOCAL_NETWORK_PERMISSION)
            return@LaunchedEffect
        }
        stage = PairStage.Searching
        stage = PairStage.Found(FlipDropClient.discover(ctx, 3000))
    }

    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text("Pair your laptop") },
        text = {
            when (val s = stage) {
                PairStage.Searching -> Row(verticalAlignment = Alignment.CenterVertically) {
                    CircularProgressIndicator(Modifier.size(20.dp), strokeWidth = 2.dp)
                    Spacer(Modifier.width(12.dp))
                    Text("Looking for FlipDrop on your Wi-Fi…")
                }
                is PairStage.Found -> Column {
                    if (needsLan) {
                        Text("Allow FlipDrop to find devices on your Wi-Fi (the \"local network\" permission).")
                        Spacer(Modifier.height(8.dp))
                        Button(onClick = { lanLauncher.launch(FlipDropClient.LOCAL_NETWORK_PERMISSION) }) { Text("Allow") }
                    } else if (s.laptops.isEmpty()) {
                        Text(
                            "No laptop found. Make sure FlipDrop is open on the laptop and both are on the same Wi-Fi. " +
                                "You can also type the laptop's IP address (it's shown in FlipDrop):",
                        )
                        Spacer(Modifier.height(8.dp))
                        Row(verticalAlignment = Alignment.CenterVertically) {
                            OutlinedTextField(
                                value = manualIp,
                                onValueChange = { v -> manualIp = v.filter { it.isDigit() || it == '.' }.take(15) },
                                singleLine = true,
                                label = { Text("Laptop IP, e.g. 192.168.1.20") },
                                keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Decimal),
                                modifier = Modifier.weight(1f),
                            )
                            Spacer(Modifier.width(8.dp))
                            TextButton(
                                enabled = manualIp.count { it == '.' } == 3,
                                onClick = {
                                    manualError = null
                                    scope.launch {
                                        val l = FlipDropClient.probe(manualIp)
                                        if (l != null) {
                                            code = ""
                                            stage = PairStage.Code(l)
                                        } else {
                                            manualError = "FlipDrop didn't answer at $manualIp"
                                        }
                                    }
                                },
                            ) { Text("Connect") }
                        }
                        manualError?.let { Text(it, color = Color(0xFFFF6B6B), fontSize = 13.sp) }
                    } else {
                        Text("Choose your laptop:")
                        Spacer(Modifier.height(8.dp))
                        s.laptops.forEach { l ->
                            TextButton(onClick = { code = ""; stage = PairStage.Code(l) }) {
                                Text("${l.name}  ·  ${l.host}")
                            }
                        }
                    }
                }
                is PairStage.Code -> Column {
                    Text("Enter the 6-digit code shown in FlipDrop on ${s.laptop.name}.")
                    Spacer(Modifier.height(12.dp))
                    OutlinedTextField(
                        value = code,
                        onValueChange = { v -> code = v.filter(Char::isDigit).take(6) },
                        singleLine = true,
                        label = { Text("Pairing code") },
                        keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.NumberPassword),
                        enabled = !s.busy,
                    )
                    s.error?.let {
                        Spacer(Modifier.height(8.dp))
                        Text(it, color = Color(0xFFFF6B6B), fontSize = 13.sp)
                    }
                }
            }
        },
        confirmButton = {
            when (val s = stage) {
                is PairStage.Found -> TextButton(onClick = { searchNonce++ }) { Text("Search again") }
                is PairStage.Code -> TextButton(
                    enabled = code.length == 6 && !s.busy,
                    onClick = {
                        stage = s.copy(busy = true, error = null)
                        scope.launch {
                            try {
                                val token = FlipDropClient.pair(
                                    s.laptop, code, PairingStore.deviceName(ctx), PairingStore.deviceId(ctx),
                                )
                                val p = PairingStore.Paired(s.laptop.name, s.laptop.host, s.laptop.port, s.laptop.fp, token)
                                PairingStore.save(ctx, p)
                                onPaired(p)
                            } catch (e: Exception) {
                                stage = s.copy(busy = false, error = e.message ?: "Pairing failed")
                            }
                        }
                    },
                ) { Text(if (s.busy) "Pairing…" else "Pair") }
                else -> {}
            }
        },
        dismissButton = { TextButton(onClick = onDismiss) { Text("Cancel") } },
    )
}
