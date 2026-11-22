package io.github.jaspreet9kaur.flipdrop.fling

import android.net.Uri
import android.view.HapticFeedbackConstants
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.animation.AnimatedContent
import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.animateColorAsState
import androidx.compose.animation.core.Animatable
import androidx.compose.animation.core.FastOutSlowInEasing
import androidx.compose.animation.core.LinearEasing
import androidx.compose.animation.core.RepeatMode
import androidx.compose.animation.core.VectorConverter
import androidx.compose.animation.core.animateFloat
import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.animation.core.infiniteRepeatable
import androidx.compose.animation.core.rememberInfiniteTransition
import androidx.compose.animation.core.spring
import androidx.compose.animation.core.tween
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.slideInVertically
import androidx.compose.animation.slideOutVertically
import androidx.compose.animation.togetherWith
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.gestures.detectDragGesturesAfterLongPress
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.items
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.BasicTextField
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.Check
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.Text
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
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Rect
import androidx.compose.ui.geometry.Size as GeomSize
import androidx.compose.ui.graphics.BlurEffect
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.ImageBitmap
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.graphics.TileMode
import androidx.compose.ui.graphics.drawscope.Stroke
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
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.IntOffset
import androidx.compose.ui.unit.IntSize
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.window.Dialog
import androidx.compose.ui.window.DialogProperties
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.compose.LifecycleEventEffect
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import kotlin.math.abs
import kotlin.math.roundToInt

// ---- Palette ---------------------------------------------------------------
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

/** Files the user picked this session (Files tab), kept in memory only. */
object RecentPicks {
    private val _items = MutableStateFlow<List<Uri>>(emptyList())
    val items: StateFlow<List<Uri>> = _items

    fun add(uris: List<Uri>) {
        if (uris.isEmpty()) return
        _items.value = (uris + _items.value).distinct().take(80)
    }
}

private enum class Tab(val label: String) { PHOTOS("Photos"), VIDEOS("Videos"), FILES("Files") }

/** A card/thumbnail lifted off the grid and flying toward the PC. */
private data class Lift(
    val uri: Uri,
    val kind: FileKind,
    val bitmap: ImageBitmap?,
    val title: String?,
    val subtitle: String?,
    val cell: Rect,
    val grab: Offset,
)

@Composable
fun FlingScreen(onBack: () -> Unit, openPairInitially: Boolean = false) {
    val ctx = LocalContext.current
    val view = LocalView.current
    val density = LocalDensity.current
    val scope = rememberCoroutineScope()

    var paired by remember { mutableStateOf(PairingStore.load(ctx)) }
    var hasPerm by remember { mutableStateOf(MediaRepo.hasAccess(ctx)) }
    var images by remember { mutableStateOf<List<MediaRepo.MediaItem>>(emptyList()) }
    var videos by remember { mutableStateOf<List<MediaRepo.MediaItem>>(emptyList()) }
    var reloadKey by remember { mutableIntStateOf(0) }
    var tab by remember { mutableStateOf(Tab.PHOTOS) }

    var selecting by remember { mutableStateOf(false) }
    val selected = remember { mutableStateOf<Set<Uri>>(emptySet()) }

    val recent by RecentPicks.items.collectAsStateWithLifecycle()
    val transfers by TransferQueue.items.collectAsStateWithLifecycle()
    var dockExpanded by remember { mutableStateOf(false) }

    var showPair by remember { mutableStateOf(false) }
    var managePaired by remember { mutableStateOf(false) }

    val permLauncher = rememberLauncherForActivityResult(ActivityResultContracts.RequestMultiplePermissions()) {
        hasPerm = MediaRepo.hasAccess(ctx)
        reloadKey++
    }
    val pickLauncher = rememberLauncherForActivityResult(ActivityResultContracts.OpenMultipleDocuments()) { uris ->
        RecentPicks.add(uris)
    }

    LaunchedEffect(Unit) {
        if (openPairInitially && paired == null) showPair = true
        if (!hasPerm) {
            val extra = if (FlipDropClient.needsLocalNetworkPermission(ctx)) arrayOf(FlipDropClient.LOCAL_NETWORK_PERMISSION) else emptyArray()
            permLauncher.launch(MediaRepo.permissions + extra)
        }
    }
    LaunchedEffect(hasPerm, reloadKey) {
        if (hasPerm) {
            images = MediaRepo.images(ctx)
            videos = MediaRepo.videos(ctx)
        }
    }
    LifecycleEventEffect(Lifecycle.Event.ON_RESUME) {
        hasPerm = MediaRepo.hasAccess(ctx)
        paired = PairingStore.load(ctx)
        reloadKey++
    }

    // ---- Drag / flick state ----
    var lift by remember { mutableStateOf<Lift?>(null) }
    var center by remember { mutableStateOf(Offset.Zero) }
    val liftAmt = remember { Animatable(0f) }
    val fly = remember { Animatable(0f) }
    var rootSize by remember { mutableStateOf(IntSize.Zero) }
    val tracker = remember { VelocityTracker() }

    fun snapBack(d: Lift) {
        scope.launch {
            val a = Animatable(center, Offset.VectorConverter)
            launch { liftAmt.animateTo(0f, spring(0.8f, 500f)) }
            a.animateTo(d.cell.center, spring(0.72f, 380f)) { center = value }
            lift = null
        }
    }

    fun flingAway(d: Lift, v: androidx.compose.ui.unit.Velocity) {
        val p = paired
        if (p == null) {
            showPair = true
            snapBack(d)
            return
        }
        view.performHapticFeedback(HapticFeedbackConstants.CONFIRM)
        val vDp = abs(v.y) / density.density
        val xFrac = if (rootSize.width > 0) (center.x / rootSize.width).coerceIn(0f, 1f) else 0.5f
        TransferQueue.enqueue(ctx, listOf(d.uri), vDp, xFrac)
        scope.launch {
            val a = Animatable(center, Offset.VectorConverter)
            val target = Offset(center.x + v.x * 0.1f, -rootSize.height * 0.4f)
            launch { fly.animateTo(1f, tween(460, easing = FastOutSlowInEasing)) }
            a.animateTo(target, spring(0.9f, 160f)) { center = value }
            lift = null
            fly.snapTo(0f)
            liftAmt.snapTo(0f)
        }
    }

    fun startLift(d: Lift) {
        view.performHapticFeedback(HapticFeedbackConstants.LONG_PRESS)
        tracker.resetTracking()
        lift = d
        center = d.cell.center
        scope.launch { liftAmt.animateTo(1f, spring(0.6f, 340f)) }
    }

    Box(
        Modifier
            .fillMaxSize()
            .background(Bg)
            .onSizeChanged { rootSize = it },
    ) {
        val dragging = lift != null
        val blur by animateFloatAsState(if (dragging) 1f else 0f, tween(260), label = "blur")

        Column(
            Modifier
                .fillMaxSize()
                .graphicsLayer {
                    val b = blur * 24f
                    renderEffect = if (b > 0.5f) BlurEffect(b, b, TileMode.Clamp) else null
                    alpha = 1f - 0.28f * blur
                },
        ) {
            TopBar(
                paired = paired,
                onBack = onBack,
                onPair = { showPair = true },
                onManage = { managePaired = true },
            )

            if (!hasPerm && tab != Tab.FILES) {
                PermissionCard(onAllow = { permLauncher.launch(MediaRepo.permissions) })
            }

            TabBar(tab, selecting, selected.value.size, onSelect = {
                tab = it
                if (it == Tab.FILES && selecting) { selecting = false; selected.value = emptySet() }
            }, onToggleSelect = {
                selecting = !selecting
                if (!selecting) selected.value = emptySet()
            })

            if (MediaRepo.partialAccess(ctx) && tab != Tab.FILES) {
                PartialAccessRow(onManage = { permLauncher.launch(MediaRepo.permissions) })
            }

            Text(
                when (tab) {
                    Tab.FILES -> "Pick anything, then flick a card up to send it to your PC"
                    else -> "Press and hold, then flick up toward your PC"
                },
                color = TextLo,
                fontSize = 12.sp,
                modifier = Modifier.padding(horizontal = 18.dp, vertical = 6.dp),
            )

            AnimatedContent(
                targetState = tab,
                transitionSpec = { fadeIn(tween(200)) togetherWith fadeOut(tween(160)) },
                label = "tab-content",
                modifier = Modifier.fillMaxSize(),
            ) { t ->
                when (t) {
                    Tab.PHOTOS, Tab.VIDEOS -> MediaGrid(
                        items = if (t == Tab.PHOTOS) images else videos,
                        selecting = selecting,
                        selected = selected.value,
                        draggedUri = lift?.uri,
                        onToggle = { u -> selected.value = selected.value.toggle(u) },
                        onLiftStart = ::startLift,
                        onDrag = { uptime, inRoot ->
                            lift?.let { d ->
                                tracker.addPosition(uptime, inRoot)
                                center = inRoot - d.grab + Offset(d.cell.width / 2, d.cell.height / 2)
                            }
                        },
                        onDragEnd = {
                            lift?.let { d ->
                                val v = tracker.calculateVelocity()
                                val vyDp = v.y / density.density
                                val inTop = center.y < rootSize.height * 0.2f
                                if ((vyDp < -700f && abs(v.y) > abs(v.x) * 0.7f) || inTop) flingAway(d, v) else snapBack(d)
                            }
                        },
                        onDragCancel = { lift?.let { snapBack(it) } },
                        emptyText = if (t == Tab.PHOTOS) "No photos yet" else "No videos yet",
                    )

                    Tab.FILES -> FilesTab(
                        recent = recent,
                        selecting = selecting,
                        selected = selected.value,
                        draggedUri = lift?.uri,
                        onPick = { pickLauncher.launch(arrayOf("*/*")) },
                        onToggle = { u -> selected.value = selected.value.toggle(u) },
                        onTapSend = { u ->
                            if (paired == null) showPair = true
                            else TransferQueue.enqueue(ctx, listOf(u))
                        },
                        onLiftStart = ::startLift,
                        onDrag = { uptime, inRoot ->
                            lift?.let { d ->
                                tracker.addPosition(uptime, inRoot)
                                center = inRoot - d.grab + Offset(d.cell.width / 2, d.cell.height / 2)
                            }
                        },
                        onDragEnd = {
                            lift?.let { d ->
                                val v = tracker.calculateVelocity()
                                val vyDp = v.y / density.density
                                val inTop = center.y < rootSize.height * 0.2f
                                if ((vyDp < -700f && abs(v.y) > abs(v.x) * 0.7f) || inTop) flingAway(d, v) else snapBack(d)
                            }
                        },
                        onDragCancel = { lift?.let { snapBack(it) } },
                    )
                }
            }
        }

        // Glowing send zone appears while a card is lifted.
        lift?.let {
            val closeness = (1f - center.y / (rootSize.height * 0.65f)).coerceIn(0f, 1f)
            SendZone(closeness, paired?.name)
        }

        // The lifted card itself.
        lift?.let { d -> FloatingItem(d, center, liftAmt.value, fly.value) }

        // Floating "Fling N items" pill while selecting.
        AnimatedVisibility(
            visible = selecting && selected.value.isNotEmpty() && lift == null,
            enter = fadeIn() + slideInVertically { it },
            exit = fadeOut() + slideOutVertically { it },
            modifier = Modifier.align(Alignment.BottomCenter).navigationBarsPadding().padding(bottom = 92.dp),
        ) {
            FlingPill(selected.value.size) {
                val p = paired
                if (p == null) { showPair = true; return@FlingPill }
                view.performHapticFeedback(HapticFeedbackConstants.CONFIRM)
                TransferQueue.enqueue(ctx, selected.value.toList())
                selected.value = emptySet()
                selecting = false
            }
        }

        // Transfer dock bound to the queue.
        TransferDock(
            transfers = transfers,
            expanded = dockExpanded,
            onToggle = { dockExpanded = !dockExpanded },
            onRetry = { TransferQueue.retry(ctx, it) },
            onClear = { TransferQueue.clearFinished() },
            modifier = Modifier.align(Alignment.BottomCenter),
        )
    }

    if (showPair) {
        PairSheet(
            onDismiss = { showPair = false },
            onPaired = { paired = it },
        )
    }
    if (managePaired) {
        paired?.let { p ->
            ManagePairedSheet(
                paired = p,
                onUnpair = { PairingStore.clear(ctx); paired = null; managePaired = false },
                onDismiss = { managePaired = false },
            )
        }
    }
}

private fun Set<Uri>.toggle(u: Uri): Set<Uri> = if (contains(u)) this - u else this + u

// ---- Top bar ---------------------------------------------------------------
@Composable
private fun TopBar(
    paired: PairingStore.Paired?,
    onBack: () -> Unit,
    onPair: () -> Unit,
    onManage: () -> Unit,
) {
    Row(
        Modifier.statusBarsPadding().fillMaxWidth().padding(horizontal = 6.dp, vertical = 6.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        IconButton(onClick = onBack) { Icon(Icons.AutoMirrored.Filled.ArrowBack, "Back", tint = TextHi) }
        Text("Fling", color = TextHi, fontSize = 24.sp, fontWeight = FontWeight.Bold)
        Spacer(Modifier.weight(1f))
        if (paired == null) {
            Box(
                Modifier
                    .clip(RoundedCornerShape(50))
                    .background(AccentBrush)
                    .clickable(onClick = onPair)
                    .padding(horizontal = 16.dp, vertical = 9.dp),
            ) { Text("Pair PC", color = Color.White, fontSize = 14.sp, fontWeight = FontWeight.SemiBold) }
        } else {
            Row(
                Modifier
                    .clip(RoundedCornerShape(50))
                    .background(Surface)
                    .clickable(onClick = onManage)
                    .padding(start = 12.dp, end = 14.dp, top = 8.dp, bottom = 8.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Box(Modifier.size(8.dp).clip(CircleShape).background(Success))
                Spacer(Modifier.width(8.dp))
                Text(paired.name, color = TextHi, fontSize = 13.sp, maxLines = 1, overflow = TextOverflow.Ellipsis)
            }
        }
        Spacer(Modifier.width(6.dp))
    }
}

// ---- Tab bar with animated gradient indicator ------------------------------
@Composable
private fun TabBar(
    selected: Tab,
    selecting: Boolean,
    selectedCount: Int,
    onSelect: (Tab) -> Unit,
    onToggleSelect: () -> Unit,
) {
    Row(
        Modifier.fillMaxWidth().padding(horizontal = 16.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        BoxWithConstraints(
            Modifier
                .weight(1f)
                .height(50.dp)
                .clip(RoundedCornerShape(18.dp))
                .background(Surface)
                .padding(5.dp),
        ) {
            val tabs = Tab.entries
            val segW = maxWidth / tabs.size
            val index by animateFloatAsState(tabs.indexOf(selected).toFloat(), spring(0.8f, 260f), label = "tab-ind")
            Box(
                Modifier
                    .width(segW)
                    .fillMaxHeight()
                    .offset(x = segW * index)
                    .clip(RoundedCornerShape(14.dp))
                    .background(AccentBrush),
            )
            Row(Modifier.fillMaxWidth().fillMaxHeight()) {
                tabs.forEach { t ->
                    val sel = t == selected
                    Box(
                        Modifier
                            .weight(1f)
                            .fillMaxHeight()
                            .clip(RoundedCornerShape(14.dp))
                            .clickable { onSelect(t) },
                        contentAlignment = Alignment.Center,
                    ) {
                        Text(
                            t.label,
                            color = if (sel) Color.White else TextMid,
                            fontSize = 14.sp,
                            fontWeight = if (sel) FontWeight.SemiBold else FontWeight.Medium,
                        )
                    }
                }
            }
        }
        Spacer(Modifier.width(10.dp))
        Box(
            Modifier
                .clip(RoundedCornerShape(50))
                .background(if (selecting) Accent1.copy(alpha = 0.22f) else Surface)
                .clickable(onClick = onToggleSelect)
                .padding(horizontal = 14.dp, vertical = 12.dp),
        ) {
            Text(
                if (selecting) (if (selectedCount > 0) "Cancel ($selectedCount)" else "Cancel") else "Select",
                color = if (selecting) Accent2 else TextMid,
                fontSize = 13.sp,
                fontWeight = FontWeight.SemiBold,
            )
        }
    }
}

@Composable
private fun PartialAccessRow(onManage: () -> Unit) {
    Row(
        Modifier
            .fillMaxWidth()
            .padding(horizontal = 16.dp, vertical = 6.dp)
            .clip(RoundedCornerShape(14.dp))
            .background(Surface)
            .clickable(onClick = onManage)
            .padding(horizontal = 14.dp, vertical = 10.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Text("You've shared only some items.", color = TextMid, fontSize = 12.sp, modifier = Modifier.weight(1f))
        Text("Manage", color = Accent2, fontSize = 12.sp, fontWeight = FontWeight.SemiBold)
    }
}

@Composable
private fun PermissionCard(onAllow: () -> Unit) {
    Column(
        Modifier
            .fillMaxWidth()
            .padding(16.dp)
            .clip(RoundedCornerShape(24.dp))
            .background(Surface)
            .padding(22.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
    ) {
        Text("Allow photo & video access", color = TextHi, fontSize = 16.sp, fontWeight = FontWeight.SemiBold)
        Spacer(Modifier.height(6.dp))
        Text("FlipDrop shows your gallery so you can flick pictures and clips to your PC.", color = TextMid, fontSize = 13.sp, textAlign = TextAlign.Center)
        Spacer(Modifier.height(16.dp))
        Box(
            Modifier.clip(RoundedCornerShape(50)).background(AccentBrush).clickable(onClick = onAllow).padding(horizontal = 22.dp, vertical = 11.dp),
        ) { Text("Allow access", color = Color.White, fontWeight = FontWeight.SemiBold) }
    }
}

// ---- Media grid ------------------------------------------------------------
@Composable
private fun MediaGrid(
    items: List<MediaRepo.MediaItem>,
    selecting: Boolean,
    selected: Set<Uri>,
    draggedUri: Uri?,
    onToggle: (Uri) -> Unit,
    onLiftStart: (Lift) -> Unit,
    onDrag: (Long, Offset) -> Unit,
    onDragEnd: () -> Unit,
    onDragCancel: () -> Unit,
    emptyText: String,
) {
    val ctx = LocalContext.current
    if (items.isEmpty()) {
        Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
            Text(emptyText, color = TextLo, fontSize = 14.sp)
        }
        return
    }
    LazyVerticalGrid(
        columns = GridCells.Fixed(3),
        contentPadding = PaddingValues(start = 3.dp, end = 3.dp, bottom = 120.dp),
        horizontalArrangement = Arrangement.spacedBy(3.dp),
        verticalArrangement = Arrangement.spacedBy(3.dp),
        modifier = Modifier.fillMaxSize(),
    ) {
        items(items, key = { it.uri.toString() }) { item ->
            var cell by remember { mutableStateOf(Rect.Zero) }
            val bmp by produceState(ThumbCache.peek(item.uri), item.uri) { value = ThumbCache.load(ctx, item.uri) }
            val isSel = selected.contains(item.uri)
            val tileMod = if (selecting) {
                Modifier.clickable { onToggle(item.uri) }
            } else {
                Modifier.pointerInput(item.uri, cell) {
                    detectDragGesturesAfterLongPress(
                        onDragStart = { off ->
                            onLiftStart(Lift(item.uri, item.kind, bmp, null, null, cell, off))
                        },
                        onDrag = { change, _ ->
                            change.consume()
                            onDrag(change.uptimeMillis, cell.topLeft + change.position)
                        },
                        onDragEnd = { onDragEnd() },
                        onDragCancel = { onDragCancel() },
                    )
                }
            }
            Box(
                Modifier
                    .aspectRatio(1f)
                    .onGloballyPositioned { cell = it.boundsInRoot() }
                    .graphicsLayer { alpha = if (draggedUri == item.uri) 0f else 1f }
                    .clip(RoundedCornerShape(4.dp))
                    .background(SurfaceHi)
                    .then(tileMod),
            ) {
                bmp?.let { Image(it, null, contentScale = ContentScale.Crop, modifier = Modifier.fillMaxSize()) }
                if (item.kind == FileKind.VIDEO) {
                    Box(
                        Modifier.align(Alignment.Center).size(34.dp).clip(CircleShape).background(Color.Black.copy(alpha = 0.4f)),
                        contentAlignment = Alignment.Center,
                    ) { Icon(Icons.Filled.PlayArrow, null, tint = Color.White, modifier = Modifier.size(22.dp)) }
                    if (item.durationMs > 0) {
                        Box(
                            Modifier.align(Alignment.BottomStart).padding(5.dp).clip(RoundedCornerShape(6.dp)).background(Color.Black.copy(alpha = 0.55f)).padding(horizontal = 5.dp, vertical = 2.dp),
                        ) { Text(fmtDuration(item.durationMs), color = Color.White, fontSize = 10.sp, fontWeight = FontWeight.Medium) }
                    }
                }
                if (selecting) SelectBadge(isSel, Modifier.align(Alignment.TopEnd).padding(5.dp))
            }
        }
    }
}

@Composable
private fun SelectBadge(selected: Boolean, modifier: Modifier = Modifier) {
    val bg by animateColorAsState(if (selected) Accent1 else Color.Black.copy(alpha = 0.35f), label = "badge")
    Box(
        modifier.size(24.dp).clip(CircleShape).background(bg).border(1.5.dp, Color.White.copy(alpha = 0.85f), CircleShape),
        contentAlignment = Alignment.Center,
    ) { if (selected) Icon(Icons.Filled.Check, null, tint = Color.White, modifier = Modifier.size(15.dp)) }
}

// ---- Files tab -------------------------------------------------------------
@Composable
private fun FilesTab(
    recent: List<Uri>,
    selecting: Boolean,
    selected: Set<Uri>,
    draggedUri: Uri?,
    onPick: () -> Unit,
    onToggle: (Uri) -> Unit,
    onTapSend: (Uri) -> Unit,
    onLiftStart: (Lift) -> Unit,
    onDrag: (Long, Offset) -> Unit,
    onDragEnd: () -> Unit,
    onDragCancel: () -> Unit,
) {
    LazyColumn(
        Modifier.fillMaxSize(),
        contentPadding = PaddingValues(start = 16.dp, end = 16.dp, top = 4.dp, bottom = 140.dp),
        verticalArrangement = Arrangement.spacedBy(10.dp),
    ) {
        item {
            Column(
                Modifier
                    .fillMaxWidth()
                    .clip(RoundedCornerShape(24.dp))
                    .background(Brush.linearGradient(listOf(Accent1.copy(alpha = 0.22f), Accent2.copy(alpha = 0.16f))))
                    .border(1.dp, Color.White.copy(alpha = 0.08f), RoundedCornerShape(24.dp))
                    .clickable(onClick = onPick)
                    .padding(22.dp),
                horizontalAlignment = Alignment.CenterHorizontally,
            ) {
                Box(Modifier.size(56.dp).clip(CircleShape).background(AccentBrush), contentAlignment = Alignment.Center) {
                    Icon(Icons.Filled.Add, null, tint = Color.White, modifier = Modifier.size(30.dp))
                }
                Spacer(Modifier.height(12.dp))
                Text("Pick files", color = TextHi, fontSize = 17.sp, fontWeight = FontWeight.Bold)
                Spacer(Modifier.height(4.dp))
                Text("Documents, PDFs, audio, archives — any file", color = TextMid, fontSize = 13.sp, textAlign = TextAlign.Center)
            }
        }
        if (recent.isNotEmpty()) {
            item { Text("Recent", color = TextLo, fontSize = 12.sp, fontWeight = FontWeight.SemiBold, modifier = Modifier.padding(top = 6.dp, bottom = 2.dp)) }
        }
        items(recent, key = { it.toString() }) { uri ->
            FileRow(
                uri = uri,
                selecting = selecting,
                selected = selected.contains(uri),
                dimmed = draggedUri == uri,
                onToggle = { onToggle(uri) },
                onTapSend = { onTapSend(uri) },
                onLiftStart = onLiftStart,
                onDrag = onDrag,
                onDragEnd = onDragEnd,
                onDragCancel = onDragCancel,
            )
        }
    }
}

@Composable
private fun FileRow(
    uri: Uri,
    selecting: Boolean,
    selected: Boolean,
    dimmed: Boolean,
    onToggle: () -> Unit,
    onTapSend: () -> Unit,
    onLiftStart: (Lift) -> Unit,
    onDrag: (Long, Offset) -> Unit,
    onDragEnd: () -> Unit,
    onDragCancel: () -> Unit,
) {
    val ctx = LocalContext.current
    val meta by produceState<FileInfo.Meta?>(null, uri) { value = withContext(Dispatchers.IO) { FileInfo.of(ctx, uri) } }
    var cell by remember { mutableStateOf(Rect.Zero) }
    val m = meta
    val title = m?.name ?: "File"
    val subtitle = m?.let { if (it.size >= 0) FileInfo.formatSize(it.size) else "" } ?: ""
    val kind = m?.kind ?: FileKind.OTHER

    val gestureMod = if (selecting) {
        Modifier.clickable { onToggle() }
    } else {
        Modifier.pointerInput(uri, cell) {
            detectTapGestures(onTap = { onTapSend() })
        }.pointerInput(uri, cell, title, subtitle, kind) {
            detectDragGesturesAfterLongPress(
                onDragStart = { off -> onLiftStart(Lift(uri, kind, null, title, subtitle, cell, off)) },
                onDrag = { change, _ ->
                    change.consume()
                    onDrag(change.uptimeMillis, cell.topLeft + change.position)
                },
                onDragEnd = { onDragEnd() },
                onDragCancel = { onDragCancel() },
            )
        }
    }

    Row(
        Modifier
            .fillMaxWidth()
            .onGloballyPositioned { cell = it.boundsInRoot() }
            .graphicsLayer { alpha = if (dimmed) 0f else 1f }
            .clip(RoundedCornerShape(18.dp))
            .background(Surface)
            .then(gestureMod)
            .padding(12.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        KindGlyph(kind, 44.dp)
        Spacer(Modifier.width(12.dp))
        Column(Modifier.weight(1f)) {
            Text(title, color = TextHi, fontSize = 14.sp, maxLines = 1, overflow = TextOverflow.Ellipsis)
            if (subtitle.isNotEmpty()) Text(subtitle, color = TextLo, fontSize = 12.sp)
        }
        if (selecting) SelectBadge(selected)
    }
}

// ---- Kind glyph (drawn, no extended icons) ---------------------------------
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
private fun KindGlyph(kind: FileKind, diameter: androidx.compose.ui.unit.Dp, textSize: androidx.compose.ui.unit.TextUnit = 12.sp) {
    Box(
        Modifier.size(diameter).clip(CircleShape).background(Brush.linearGradient(kindColors(kind))),
        contentAlignment = Alignment.Center,
    ) {
        Text(kindShort(kind), color = Color.White, fontSize = textSize, fontWeight = FontWeight.Bold)
    }
}

// ---- Send zone & floating item ---------------------------------------------
@Composable
private fun SendZone(closeness: Float, laptop: String?) {
    Box(Modifier.fillMaxWidth().height(230.dp)) {
        Canvas(Modifier.fillMaxSize()) {
            val a = 0.25f + 0.75f * closeness
            drawRect(
                Brush.verticalGradient(
                    listOf(Accent1.copy(alpha = 0.5f * a), Accent2.copy(alpha = 0.22f * a), Color.Transparent),
                ),
            )
            drawOval(
                Brush.radialGradient(
                    listOf(Accent2.copy(alpha = 0.6f * a), Accent1.copy(alpha = 0.4f * a), Color.Transparent),
                    center = Offset(size.width / 2, 0f),
                    radius = size.width * (0.55f + 0.25f * closeness),
                ),
                topLeft = Offset(-size.width * 0.2f, -size.height * 0.9f),
                size = GeomSize(size.width * 1.4f, size.height * 1.6f),
            )
        }
        Text(
            if (laptop != null) "Flick up to send to $laptop" else "Pair a PC to fling",
            color = Color.White.copy(alpha = 0.6f + 0.4f * closeness),
            fontSize = 15.sp,
            fontWeight = FontWeight.Medium,
            modifier = Modifier.align(Alignment.Center).statusBarsPadding(),
        )
    }
}

@Composable
private fun FloatingItem(d: Lift, center: Offset, lift: Float, fly: Float) {
    val density = LocalDensity.current
    val cellW = with(density) { d.cell.width.toDp() }
    val targetW = if (d.bitmap != null) 210.dp else 200.dp
    val sizeDp = cellW + (targetW - cellW) * lift
    val sizePx = with(density) { sizeDp.toPx() }
    val half = sizePx / 2f

    // Light trail behind the card as it rises and flies off.
    if (lift > 0.3f) {
        val trail = with(density) { (90.dp + 420.dp * fly).toPx() }
        Canvas(
            Modifier
                .fillMaxSize()
                .graphicsLayer {
                    renderEffect = BlurEffect(38f, 38f, TileMode.Decal)
                    alpha = (0.32f + 0.6f * fly) * lift
                },
        ) {
            drawRect(
                Brush.verticalGradient(listOf(Accent1.copy(alpha = 0.85f), Accent2.copy(alpha = 0.7f), Color.Transparent), startY = center.y, endY = center.y + half + trail),
                topLeft = Offset(center.x - half * 0.8f, center.y),
                size = GeomSize(half * 1.6f, half + trail),
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
                rotationX = 16f * fly
                alpha = 1f - fly * fly
            }
            .shadow((30 * lift).dp, RoundedCornerShape(24.dp))
            .clip(RoundedCornerShape((4 + 18 * lift).dp))
            .background(SurfaceHi),
        contentAlignment = Alignment.Center,
    ) {
        if (d.bitmap != null) {
            Image(d.bitmap, null, contentScale = ContentScale.Crop, modifier = Modifier.fillMaxSize())
            if (d.kind == FileKind.VIDEO) {
                Box(Modifier.size(48.dp).clip(CircleShape).background(Color.Black.copy(alpha = 0.4f)), contentAlignment = Alignment.Center) {
                    Icon(Icons.Filled.PlayArrow, null, tint = Color.White, modifier = Modifier.size(30.dp))
                }
            }
            Canvas(Modifier.fillMaxSize()) {
                drawRect(Brush.linearGradient(listOf(Color.White.copy(alpha = 0.22f * lift), Color.Transparent), end = Offset(size.width * 0.6f, size.height * 0.6f)))
            }
        } else {
            Column(
                Modifier.fillMaxSize().padding(18.dp),
                horizontalAlignment = Alignment.CenterHorizontally,
                verticalArrangement = Arrangement.Center,
            ) {
                KindGlyph(d.kind, 72.dp, 16.sp)
                Spacer(Modifier.height(14.dp))
                Text(d.title ?: "File", color = TextHi, fontSize = 14.sp, fontWeight = FontWeight.SemiBold, maxLines = 2, overflow = TextOverflow.Ellipsis, textAlign = TextAlign.Center)
                d.subtitle?.takeIf { it.isNotEmpty() }?.let {
                    Spacer(Modifier.height(4.dp))
                    Text(it, color = TextLo, fontSize = 12.sp)
                }
            }
        }
    }
}

// ---- Fling pill (multi-select) ---------------------------------------------
@Composable
private fun FlingPill(count: Int, onClick: () -> Unit) {
    Row(
        Modifier
            .shadow(20.dp, RoundedCornerShape(50))
            .clip(RoundedCornerShape(50))
            .background(AccentBrush)
            .clickable(onClick = onClick)
            .padding(horizontal = 24.dp, vertical = 14.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Text("Fling $count ${if (count == 1) "item" else "items"}", color = Color.White, fontSize = 15.sp, fontWeight = FontWeight.Bold)
    }
}

// ---- Transfer dock ---------------------------------------------------------
@Composable
private fun TransferDock(
    transfers: List<TransferQueue.Item>,
    expanded: Boolean,
    onToggle: () -> Unit,
    onRetry: (Long) -> Unit,
    onClear: () -> Unit,
    modifier: Modifier = Modifier,
) {
    AnimatedVisibility(
        visible = transfers.isNotEmpty(),
        enter = fadeIn() + slideInVertically { it },
        exit = fadeOut() + slideOutVertically { it },
        modifier = modifier,
    ) {
        val total = transfers.size
        val done = transfers.count { it.state == TransferQueue.State.DONE }
        val failed = transfers.count { it.state == TransferQueue.State.FAILED }
        val active = transfers.any { it.state == TransferQueue.State.SENDING || it.state == TransferQueue.State.QUEUED }
        val sentBytes = transfers.sumOf { it.sent }
        val totalBytes = transfers.sumOf { if (it.size > 0) it.size else it.sent }
        val pct = if (totalBytes > 0) (sentBytes.toFloat() / totalBytes).coerceIn(0f, 1f) else if (total > 0) done.toFloat() / total else 0f
        val pctAnim by animateFloatAsState(pct, tween(300), label = "pct")

        Column(Modifier.fillMaxWidth().navigationBarsPadding().padding(14.dp)) {
            AnimatedVisibility(expanded) {
                Column {
                    Column(
                        Modifier
                            .fillMaxWidth()
                            .heightInSheet()
                            .clip(RoundedCornerShape(22.dp))
                            .background(Surface)
                            .padding(bottom = 6.dp),
                    ) {
                        Row(Modifier.fillMaxWidth().padding(start = 16.dp, end = 10.dp, top = 12.dp, bottom = 6.dp), verticalAlignment = Alignment.CenterVertically) {
                            Text("Transfers", color = TextHi, fontSize = 15.sp, fontWeight = FontWeight.Bold, modifier = Modifier.weight(1f))
                            if (done + failed > 0) {
                                Text("Clear finished", color = Accent2, fontSize = 12.sp, fontWeight = FontWeight.SemiBold, modifier = Modifier.clickable(onClick = onClear).padding(8.dp))
                            }
                            IconButton(onClick = onToggle) { Icon(Icons.Filled.Close, "Collapse", tint = TextMid) }
                        }
                        LazyColumn(
                            Modifier.fillMaxWidth(),
                            contentPadding = PaddingValues(start = 12.dp, end = 12.dp, bottom = 10.dp),
                            verticalArrangement = Arrangement.spacedBy(8.dp),
                        ) {
                            items(transfers, key = { it.id }) { t -> TransferRow(t, onRetry) }
                        }
                    }
                    Spacer(Modifier.height(10.dp))
                }
            }

            // Collapsed summary bar.
            Column(
                Modifier
                    .fillMaxWidth()
                    .shadow(16.dp, RoundedCornerShape(18.dp))
                    .clip(RoundedCornerShape(18.dp))
                    .background(SurfaceHi)
                    .clickable(onClick = onToggle)
                    .padding(14.dp),
            ) {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    val label = when {
                        active -> "Sending ${(done + 1).coerceAtMost(total)} of $total · ${(pct * 100).roundToInt()}%"
                        failed > 0 -> "$done sent · $failed failed"
                        else -> "All sent ✓"
                    }
                    Text(label, color = if (!active && failed == 0) Success else TextHi, fontSize = 14.sp, fontWeight = FontWeight.SemiBold, modifier = Modifier.weight(1f))
                    Icon(if (expanded) Icons.Filled.Close else Icons.Filled.PlayArrow, null, tint = TextMid, modifier = Modifier.size(18.dp).graphicsLayer { rotationZ = if (expanded) 0f else -90f })
                }
                Spacer(Modifier.height(10.dp))
                ProgressBar(pctAnim, if (failed > 0 && !active) Error else null)
            }
        }
    }
}

private fun Modifier.heightInSheet(): Modifier = this.heightIn(max = 360.dp)

@Composable
private fun TransferRow(t: TransferQueue.Item, onRetry: (Long) -> Unit) {
    val ctx = LocalContext.current
    val showThumb = t.kind == FileKind.IMAGE || t.kind == FileKind.VIDEO
    val bmp by produceState(ThumbCache.peek(t.uri), t.uri, showThumb) { if (showThumb) value = ThumbCache.load(ctx, t.uri) }
    val itemPct = when (t.state) {
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
                TransferQueue.State.DONE -> Text("Done · ${FileInfo.formatSize(t.size.coerceAtLeast(t.sent))}", color = Success, fontSize = 11.sp)
                TransferQueue.State.FAILED -> Text(t.error ?: "Failed", color = Error, fontSize = 11.sp, maxLines = 1, overflow = TextOverflow.Ellipsis)
                TransferQueue.State.QUEUED -> Text("Queued", color = TextLo, fontSize = 11.sp)
                TransferQueue.State.SENDING -> ProgressBar(itemPct, null)
            }
        }
        if (t.state == TransferQueue.State.FAILED) {
            Spacer(Modifier.width(8.dp))
            Box(
                Modifier.clip(RoundedCornerShape(50)).background(Accent1.copy(alpha = 0.22f)).clickable { onRetry(t.id) }.padding(horizontal = 10.dp, vertical = 7.dp),
                contentAlignment = Alignment.Center,
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
private fun ProgressBar(pct: Float, overrideColor: Color?) {
    Box(Modifier.fillMaxWidth().height(6.dp).clip(RoundedCornerShape(50)).background(Color.White.copy(alpha = 0.08f))) {
        Box(
            Modifier
                .fillMaxWidth(pct)
                .height(6.dp)
                .clip(RoundedCornerShape(50))
                .background(overrideColor?.let { Brush.linearGradient(listOf(it, it)) } ?: AccentBrush),
        )
    }
}

private fun fmtDuration(ms: Long): String {
    val s = ms / 1000
    return "%d:%02d".format(s / 60, s % 60)
}

// ---- Pairing ---------------------------------------------------------------
private sealed interface PairStage {
    data object Searching : PairStage
    data class Found(val laptops: List<FlipDropClient.Laptop>) : PairStage
    data class Code(val laptop: FlipDropClient.Laptop, val error: String? = null, val busy: Boolean = false) : PairStage
    data class Done(val paired: PairingStore.Paired) : PairStage
}

@Composable
fun PairSheet(onDismiss: () -> Unit, onPaired: (PairingStore.Paired) -> Unit) {
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
            lanLauncher.launch(FlipDropClient.LOCAL_NETWORK_PERMISSION)
            return@LaunchedEffect
        }
        if (stage !is PairStage.Code && stage !is PairStage.Done) {
            stage = PairStage.Searching
            stage = PairStage.Found(FlipDropClient.discover(ctx, 3000))
        }
    }

    Dialog(onDismissRequest = onDismiss, properties = DialogProperties(usePlatformDefaultWidth = false)) {
        Column(
            Modifier
                .fillMaxWidth(0.92f)
                .clip(RoundedCornerShape(28.dp))
                .background(Surface)
                .border(1.dp, Color.White.copy(alpha = 0.06f), RoundedCornerShape(28.dp))
                .padding(22.dp),
        ) {
            when (val s = stage) {
                PairStage.Searching -> {
                    Text("Finding your PC", color = TextHi, fontSize = 20.sp, fontWeight = FontWeight.Bold)
                    Spacer(Modifier.height(18.dp))
                    Box(Modifier.fillMaxWidth(), contentAlignment = Alignment.Center) { SignalPulse() }
                    Spacer(Modifier.height(16.dp))
                    Text("Looking for FlipDrop on your Wi-Fi…", color = TextMid, fontSize = 13.sp, textAlign = TextAlign.Center, modifier = Modifier.fillMaxWidth())
                }

                is PairStage.Found -> {
                    Text("Pair your PC", color = TextHi, fontSize = 20.sp, fontWeight = FontWeight.Bold)
                    Spacer(Modifier.height(14.dp))
                    if (needsLan) {
                        Text("Let FlipDrop find devices on your Wi-Fi.", color = TextMid, fontSize = 13.sp)
                        Spacer(Modifier.height(12.dp))
                        AccentButton("Allow") { lanLauncher.launch(FlipDropClient.LOCAL_NETWORK_PERMISSION) }
                    } else {
                        if (s.laptops.isEmpty()) {
                            Text("No PC found yet. Make sure FlipDrop is open and both are on the same Wi-Fi.", color = TextMid, fontSize = 13.sp)
                        } else {
                            s.laptops.forEach { l ->
                                Row(
                                    Modifier
                                        .fillMaxWidth()
                                        .padding(vertical = 4.dp)
                                        .clip(RoundedCornerShape(16.dp))
                                        .background(SurfaceHi)
                                        .clickable { code = ""; stage = PairStage.Code(l) }
                                        .padding(14.dp),
                                    verticalAlignment = Alignment.CenterVertically,
                                ) {
                                    Box(Modifier.size(36.dp).clip(CircleShape).background(AccentBrush), contentAlignment = Alignment.Center) {
                                        Text("PC", color = Color.White, fontSize = 12.sp, fontWeight = FontWeight.Bold)
                                    }
                                    Spacer(Modifier.width(12.dp))
                                    Column(Modifier.weight(1f)) {
                                        Text(l.name, color = TextHi, fontSize = 15.sp, fontWeight = FontWeight.SemiBold)
                                        Text(l.host, color = TextLo, fontSize = 12.sp)
                                    }
                                }
                            }
                        }
                        Spacer(Modifier.height(14.dp))
                        Text("Or type the PC's IP (shown in FlipDrop):", color = TextLo, fontSize = 12.sp)
                        Spacer(Modifier.height(8.dp))
                        Row(verticalAlignment = Alignment.CenterVertically) {
                            PlainField(
                                value = manualIp,
                                onChange = { v -> manualIp = v.filter { it.isDigit() || it == '.' }.take(15) },
                                hint = "192.168.1.20",
                                keyboardType = KeyboardType.Decimal,
                                modifier = Modifier.weight(1f),
                            )
                            Spacer(Modifier.width(10.dp))
                            AccentButton("Connect", enabled = manualIp.count { it == '.' } == 3) {
                                manualError = null
                                scope.launch {
                                    val l = FlipDropClient.probe(manualIp)
                                    if (l != null) { code = ""; stage = PairStage.Code(l) } else manualError = "No answer at $manualIp"
                                }
                            }
                        }
                        manualError?.let { Spacer(Modifier.height(6.dp)); Text(it, color = Error, fontSize = 12.sp) }
                        Spacer(Modifier.height(12.dp))
                        Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween) {
                            GhostButton("Cancel") { onDismiss() }
                            GhostButton("Search again") { searchNonce++ }
                        }
                    }
                }

                is PairStage.Code -> {
                    Text("Enter the code", color = TextHi, fontSize = 20.sp, fontWeight = FontWeight.Bold)
                    Spacer(Modifier.height(6.dp))
                    Text("The 6-digit code shown in FlipDrop on ${s.laptop.name}.", color = TextMid, fontSize = 13.sp)
                    Spacer(Modifier.height(18.dp))
                    fun submit() {
                        if (code.length != 6 || s.busy) return
                        stage = s.copy(busy = true, error = null)
                        scope.launch {
                            try {
                                val token = FlipDropClient.pair(s.laptop, code, PairingStore.deviceName(ctx), PairingStore.deviceId(ctx))
                                val p = PairingStore.Paired(s.laptop.name, s.laptop.host, s.laptop.port, s.laptop.fp, token)
                                PairingStore.save(ctx, p)
                                onPaired(p)
                                stage = PairStage.Done(p)
                            } catch (e: Exception) {
                                code = ""
                                stage = s.copy(busy = false, error = e.message ?: "Pairing failed")
                            }
                        }
                    }
                    Box(Modifier.fillMaxWidth(), contentAlignment = Alignment.Center) {
                        CodeBoxes(code, enabled = !s.busy, onChange = { code = it }, onComplete = { submit() })
                    }
                    s.error?.let { Spacer(Modifier.height(10.dp)); Text(it, color = Error, fontSize = 12.sp) }
                    Spacer(Modifier.height(18.dp))
                    Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween) {
                        GhostButton("Back") { stage = PairStage.Found(emptyList()); searchNonce++ }
                        AccentButton(if (s.busy) "Pairing…" else "Pair", enabled = code.length == 6 && !s.busy) { submit() }
                    }
                }

                is PairStage.Done -> {
                    Text("Paired with ${s.paired.name}", color = TextHi, fontSize = 20.sp, fontWeight = FontWeight.Bold)
                    Spacer(Modifier.height(18.dp))
                    SecurityCodeBlock(FlipDropClient.securityCode(s.paired.fp))
                    Spacer(Modifier.height(14.dp))
                    Text("Check your PC shows the same code. If it doesn't, unpair and try again.", color = TextMid, fontSize = 13.sp, textAlign = TextAlign.Center, modifier = Modifier.fillMaxWidth())
                    Spacer(Modifier.height(18.dp))
                    AccentButton("Done", modifier = Modifier.fillMaxWidth()) { onDismiss() }
                }
            }
        }
    }
}

@Composable
private fun ManagePairedSheet(paired: PairingStore.Paired, onUnpair: () -> Unit, onDismiss: () -> Unit) {
    var confirm by remember { mutableStateOf(false) }
    Dialog(onDismissRequest = onDismiss, properties = DialogProperties(usePlatformDefaultWidth = false)) {
        Column(
            Modifier
                .fillMaxWidth(0.92f)
                .clip(RoundedCornerShape(28.dp))
                .background(Surface)
                .border(1.dp, Color.White.copy(alpha = 0.06f), RoundedCornerShape(28.dp))
                .padding(22.dp),
        ) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Box(Modifier.size(10.dp).clip(CircleShape).background(Success))
                Spacer(Modifier.width(10.dp))
                Text(paired.name, color = TextHi, fontSize = 20.sp, fontWeight = FontWeight.Bold)
            }
            Spacer(Modifier.height(4.dp))
            Text(paired.host, color = TextLo, fontSize = 13.sp)
            Spacer(Modifier.height(18.dp))
            Text("Security code", color = TextLo, fontSize = 12.sp, fontWeight = FontWeight.SemiBold)
            Spacer(Modifier.height(8.dp))
            SecurityCodeBlock(FlipDropClient.securityCode(paired.fp))
            Spacer(Modifier.height(8.dp))
            Text("This should match the code shown on your PC.", color = TextMid, fontSize = 12.sp)
            Spacer(Modifier.height(20.dp))
            if (!confirm) {
                Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween) {
                    GhostButton("Unpair", danger = true) { confirm = true }
                    AccentButton("Close") { onDismiss() }
                }
            } else {
                Text("Forget ${paired.name}? You can pair again any time.", color = TextMid, fontSize = 13.sp)
                Spacer(Modifier.height(12.dp))
                Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween) {
                    GhostButton("Cancel") { confirm = false }
                    GhostButton("Unpair", danger = true, onClick = onUnpair)
                }
            }
        }
    }
}

@Composable
private fun SecurityCodeBlock(code: String) {
    Box(
        Modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(18.dp))
            .background(Brush.linearGradient(listOf(Accent1.copy(alpha = 0.18f), Accent2.copy(alpha = 0.14f))))
            .padding(vertical = 18.dp),
        contentAlignment = Alignment.Center,
    ) {
        Text(code, color = TextHi, fontSize = 30.sp, fontWeight = FontWeight.Bold, letterSpacing = 4.sp)
    }
}

@Composable
private fun CodeBoxes(code: String, enabled: Boolean, onChange: (String) -> Unit, onComplete: () -> Unit) {
    val focus = remember { FocusRequester() }
    LaunchedEffect(Unit) { runCatching { focus.requestFocus() } }
    BasicTextField(
        value = code,
        onValueChange = { v ->
            val f = v.filter(Char::isDigit).take(6)
            onChange(f)
            if (f.length == 6) onComplete()
        },
        enabled = enabled,
        singleLine = true,
        keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.NumberPassword),
        cursorBrush = SolidColor(Color.Transparent),
        textStyle = TextStyle(color = Color.Transparent),
        modifier = Modifier.focusRequester(focus),
        decorationBox = {
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                repeat(6) { i ->
                    val ch = code.getOrNull(i)?.toString() ?: ""
                    val active = i == code.length && enabled
                    Box(
                        Modifier
                            .size(width = 44.dp, height = 54.dp)
                            .clip(RoundedCornerShape(12.dp))
                            .background(SurfaceHi)
                            .border(if (active) 2.dp else 1.dp, if (active) Accent2 else Color.White.copy(alpha = 0.08f), RoundedCornerShape(12.dp)),
                        contentAlignment = Alignment.Center,
                    ) { Text(ch, color = TextHi, fontSize = 22.sp, fontWeight = FontWeight.Bold) }
                }
            }
        },
    )
}

@Composable
private fun SignalPulse() {
    val t = rememberInfiniteTransition(label = "sig")
    val p by t.animateFloat(
        0f, 1f,
        infiniteRepeatable(tween(1800, easing = LinearEasing), RepeatMode.Restart),
        label = "p",
    )
    Canvas(Modifier.size(120.dp)) {
        val maxR = size.minDimension / 2f
        for (k in 0..2) {
            val f = (p + k / 3f) % 1f
            drawCircle(color = Accent2.copy(alpha = (1f - f) * 0.5f), radius = maxR * f, style = Stroke(width = 3.dp.toPx()))
        }
        drawCircle(brush = AccentBrush, radius = maxR * 0.22f, center = center)
    }
}

@Composable
private fun AccentButton(text: String, enabled: Boolean = true, modifier: Modifier = Modifier, onClick: () -> Unit) {
    Box(
        modifier
            .clip(RoundedCornerShape(50))
            .background(if (enabled) AccentBrush else Brush.linearGradient(listOf(SurfaceHi, SurfaceHi)))
            .clickable(enabled = enabled, onClick = onClick)
            .padding(horizontal = 20.dp, vertical = 11.dp),
        contentAlignment = Alignment.Center,
    ) { Text(text, color = if (enabled) Color.White else TextLo, fontSize = 14.sp, fontWeight = FontWeight.SemiBold) }
}

@Composable
private fun GhostButton(text: String, danger: Boolean = false, onClick: () -> Unit) {
    Box(
        Modifier
            .clip(RoundedCornerShape(50))
            .background(if (danger) Error.copy(alpha = 0.14f) else Color.White.copy(alpha = 0.06f))
            .clickable(onClick = onClick)
            .padding(horizontal = 18.dp, vertical = 11.dp),
    ) { Text(text, color = if (danger) Error else TextMid, fontSize = 14.sp, fontWeight = FontWeight.SemiBold) }
}

@Composable
private fun PlainField(
    value: String,
    onChange: (String) -> Unit,
    hint: String,
    keyboardType: KeyboardType,
    modifier: Modifier = Modifier,
) {
    Box(
        modifier
            .clip(RoundedCornerShape(12.dp))
            .background(SurfaceHi)
            .border(1.dp, Color.White.copy(alpha = 0.08f), RoundedCornerShape(12.dp))
            .padding(horizontal = 12.dp, vertical = 12.dp),
    ) {
        if (value.isEmpty()) Text(hint, color = TextLo, fontSize = 14.sp)
        BasicTextField(
            value = value,
            onValueChange = onChange,
            singleLine = true,
            keyboardOptions = KeyboardOptions(keyboardType = keyboardType),
            textStyle = TextStyle(color = TextHi, fontSize = 14.sp),
            cursorBrush = SolidColor(Accent2),
            modifier = Modifier.fillMaxWidth(),
        )
    }
}
