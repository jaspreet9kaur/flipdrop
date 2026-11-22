package io.github.jaspreet9kaur.flipdrop.fling

import android.Manifest
import android.content.ContentResolver
import android.content.ContentUris
import android.content.Context
import android.content.pm.PackageManager
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.provider.MediaStore
import android.util.LruCache
import android.util.Size
import androidx.compose.ui.graphics.ImageBitmap
import androidx.compose.ui.graphics.asAndroidBitmap
import androidx.compose.ui.graphics.asImageBitmap
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext

/** Reads photos and videos from MediaStore so they can be lifted and flung to the PC. */
object MediaRepo {
    /** A photo or video ready to show in the grid. [kind] is only IMAGE or VIDEO here. */
    data class MediaItem(
        val uri: Uri,
        val kind: FileKind,
        val durationMs: Long = 0L,
        val size: Long = 0L,
    )

    val permissions: Array<String> =
        if (Build.VERSION.SDK_INT >= 34) {
            arrayOf(
                Manifest.permission.READ_MEDIA_IMAGES,
                Manifest.permission.READ_MEDIA_VIDEO,
                Manifest.permission.READ_MEDIA_VISUAL_USER_SELECTED,
            )
        } else {
            arrayOf(Manifest.permission.READ_MEDIA_IMAGES, Manifest.permission.READ_MEDIA_VIDEO)
        }

    private fun granted(ctx: Context, p: String) =
        ctx.checkSelfPermission(p) == PackageManager.PERMISSION_GRANTED

    /** True when we may read any photos or videos (full or user-selected subset). */
    fun hasAccess(ctx: Context): Boolean =
        granted(ctx, Manifest.permission.READ_MEDIA_IMAGES) ||
            granted(ctx, Manifest.permission.READ_MEDIA_VIDEO) ||
            (Build.VERSION.SDK_INT >= 34 && granted(ctx, Manifest.permission.READ_MEDIA_VISUAL_USER_SELECTED))

    /** Android 14+ only gave us a hand-picked subset — offer a "manage" shortcut. */
    fun partialAccess(ctx: Context): Boolean =
        Build.VERSION.SDK_INT >= 34 &&
            granted(ctx, Manifest.permission.READ_MEDIA_VISUAL_USER_SELECTED) &&
            !granted(ctx, Manifest.permission.READ_MEDIA_IMAGES) &&
            !granted(ctx, Manifest.permission.READ_MEDIA_VIDEO)

    suspend fun images(ctx: Context, limit: Int = 600): List<MediaItem> =
        rows(ctx, MediaStore.Images.Media.EXTERNAL_CONTENT_URI, FileKind.IMAGE, false, limit).map { it.item }

    suspend fun videos(ctx: Context, limit: Int = 600): List<MediaItem> =
        rows(ctx, MediaStore.Video.Media.EXTERNAL_CONTENT_URI, FileKind.VIDEO, true, limit).map { it.item }

    /** Photos and videos interleaved, newest first. */
    suspend fun all(ctx: Context, limit: Int = 600): List<MediaItem> {
        val images = rows(ctx, MediaStore.Images.Media.EXTERNAL_CONTENT_URI, FileKind.IMAGE, false, limit)
        val videos = rows(ctx, MediaStore.Video.Media.EXTERNAL_CONTENT_URI, FileKind.VIDEO, true, limit)
        return (images + videos).sortedByDescending { it.date }.map { it.item }.take(limit)
    }

    private data class Row(val item: MediaItem, val date: Long)

    private suspend fun rows(
        ctx: Context,
        base: Uri,
        kind: FileKind,
        hasDuration: Boolean,
        limit: Int,
    ): List<Row> = withContext(Dispatchers.IO) {
        val cols = buildList {
            add(MediaStore.MediaColumns._ID)
            add(MediaStore.MediaColumns.DATE_ADDED)
            add(MediaStore.MediaColumns.SIZE)
            if (hasDuration) add(MediaStore.MediaColumns.DURATION)
        }.toTypedArray()
        val args = Bundle().apply {
            putStringArray(ContentResolver.QUERY_ARG_SORT_COLUMNS, arrayOf(MediaStore.MediaColumns.DATE_ADDED))
            putInt(ContentResolver.QUERY_ARG_SORT_DIRECTION, ContentResolver.QUERY_SORT_DIRECTION_DESCENDING)
            putInt(ContentResolver.QUERY_ARG_LIMIT, limit)
        }
        val out = ArrayList<Row>()
        runCatching {
            ctx.contentResolver.query(base, cols, args, null)?.use { c ->
                val idCol = c.getColumnIndexOrThrow(MediaStore.MediaColumns._ID)
                val dateCol = c.getColumnIndexOrThrow(MediaStore.MediaColumns.DATE_ADDED)
                val sizeCol = c.getColumnIndexOrThrow(MediaStore.MediaColumns.SIZE)
                val durCol = if (hasDuration) c.getColumnIndex(MediaStore.MediaColumns.DURATION) else -1
                while (c.moveToNext()) {
                    out += Row(
                        MediaItem(
                            uri = ContentUris.withAppendedId(base, c.getLong(idCol)),
                            kind = kind,
                            durationMs = if (durCol >= 0 && !c.isNull(durCol)) c.getLong(durCol) else 0L,
                            size = if (!c.isNull(sizeCol)) c.getLong(sizeCol) else 0L,
                        ),
                        date = if (!c.isNull(dateCol)) c.getLong(dateCol) else 0L,
                    )
                }
            }
        }
        out
    }
}

/** Small LRU of decoded thumbnails. Works for both photo and video content URIs. */
object ThumbCache {
    /** Largest thumbnail edge we ever request, so a single bitmap can't blow the budget. */
    private const val MAX_PX = 512

    /** Cap the cache at 1/8 of the heap, measured in bytes (not entry count). */
    private val maxBytes: Int =
        (Runtime.getRuntime().maxMemory() / 8)
            .coerceIn(4L * 1024 * 1024, Int.MAX_VALUE.toLong())
            .toInt()

    private val cache = object : LruCache<Uri, ImageBitmap>(maxBytes) {
        override fun sizeOf(key: Uri, value: ImageBitmap): Int =
            runCatching { value.asAndroidBitmap().allocationByteCount }
                .getOrDefault(value.width * value.height * 4)
                .coerceAtLeast(1)
    }

    fun peek(uri: Uri): ImageBitmap? = cache.get(uri)

    suspend fun load(ctx: Context, uri: Uri, px: Int = 360): ImageBitmap? {
        cache.get(uri)?.let { return it }
        val edge = px.coerceIn(1, MAX_PX)
        return withContext(Dispatchers.IO) {
            runCatching { ctx.contentResolver.loadThumbnail(uri, Size(edge, edge), null).asImageBitmap() }
                .getOrNull()
                ?.also { cache.put(uri, it) }
        }
    }
}
