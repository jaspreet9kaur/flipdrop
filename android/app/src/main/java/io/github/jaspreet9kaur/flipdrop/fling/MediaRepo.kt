package io.github.jaspreet9kaur.flipdrop.fling

import android.Manifest
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
import androidx.compose.ui.graphics.asImageBitmap
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext

object MediaRepo {
    val permissions: Array<String> =
        if (Build.VERSION.SDK_INT >= 34) {
            arrayOf(Manifest.permission.READ_MEDIA_IMAGES, Manifest.permission.READ_MEDIA_VISUAL_USER_SELECTED)
        } else {
            arrayOf(Manifest.permission.READ_MEDIA_IMAGES)
        }

    fun hasAccess(ctx: Context): Boolean {
        fun granted(p: String) = ctx.checkSelfPermission(p) == PackageManager.PERMISSION_GRANTED
        return granted(Manifest.permission.READ_MEDIA_IMAGES) ||
            (Build.VERSION.SDK_INT >= 34 && granted(Manifest.permission.READ_MEDIA_VISUAL_USER_SELECTED))
    }

    suspend fun recent(ctx: Context, limit: Int = 600): List<Uri> = withContext(Dispatchers.IO) {
        val base = MediaStore.Images.Media.EXTERNAL_CONTENT_URI
        val args = Bundle().apply {
            putStringArray(android.content.ContentResolver.QUERY_ARG_SORT_COLUMNS, arrayOf(MediaStore.Images.Media.DATE_ADDED))
            putInt(android.content.ContentResolver.QUERY_ARG_SORT_DIRECTION, android.content.ContentResolver.QUERY_SORT_DIRECTION_DESCENDING)
            putInt(android.content.ContentResolver.QUERY_ARG_LIMIT, limit)
        }
        val out = ArrayList<Uri>()
        runCatching {
            ctx.contentResolver.query(base, arrayOf(MediaStore.Images.Media._ID), args, null)?.use { c ->
                val idCol = c.getColumnIndexOrThrow(MediaStore.Images.Media._ID)
                while (c.moveToNext()) out += ContentUris.withAppendedId(base, c.getLong(idCol))
            }
        }
        out
    }
}

object ThumbCache {
    private val cache = LruCache<Uri, ImageBitmap>(240)

    fun peek(uri: Uri): ImageBitmap? = cache.get(uri)

    suspend fun load(ctx: Context, uri: Uri, px: Int = 360): ImageBitmap? {
        cache.get(uri)?.let { return it }
        return withContext(Dispatchers.IO) {
            runCatching { ctx.contentResolver.loadThumbnail(uri, Size(px, px), null).asImageBitmap() }
                .getOrNull()
                ?.also { cache.put(uri, it) }
        }
    }
}
