package io.github.jaspreet9kaur.flipdrop.fling

import android.content.Context
import android.net.Uri
import android.provider.OpenableColumns

/** Broad category of a file, mirrored by the PC's `kind` in the v2 protocol. */
enum class FileKind { IMAGE, VIDEO, AUDIO, PDF, DOCUMENT, TEXT, ARCHIVE, APP, OTHER }

/**
 * Reads display metadata for a shared `content://` URI without ever touching the bytes:
 * name and size come from [OpenableColumns], the MIME type from the content resolver.
 */
object FileInfo {
    data class Meta(
        /** Display name, e.g. "holiday.mp4". Never used as a path by the PC. */
        val name: String,
        /** Exact size in bytes, or -1 when the provider does not report one. */
        val size: Long,
        /** The provider's MIME guess, or null. */
        val mime: String?,
        val kind: FileKind,
    )

    /** Looks up name/size/mime for [uri] and derives its [FileKind]. */
    fun of(context: Context, uri: Uri): Meta {
        val cr = context.contentResolver
        var name: String? = null
        var size = -1L
        runCatching {
            cr.query(uri, arrayOf(OpenableColumns.DISPLAY_NAME, OpenableColumns.SIZE), null, null, null)?.use { c ->
                if (c.moveToFirst()) {
                    val ni = c.getColumnIndex(OpenableColumns.DISPLAY_NAME)
                    if (ni >= 0 && !c.isNull(ni)) name = c.getString(ni)
                    val si = c.getColumnIndex(OpenableColumns.SIZE)
                    if (si >= 0 && !c.isNull(si)) size = c.getLong(si)
                }
            }
        }
        val mime = runCatching { cr.getType(uri) }.getOrNull()
        val display = name?.takeIf { it.isNotBlank() }
            ?: uri.lastPathSegment?.substringAfterLast('/')?.takeIf { it.isNotBlank() }
            ?: "file"
        return Meta(display, if (size < 0) -1L else size, mime, kindOf(mime, display))
    }

    /** Classifies by MIME type first (authoritative), falling back to the file extension. */
    fun kindOf(mime: String?, name: String?): FileKind {
        val m = mime?.lowercase()?.substringBefore(';')?.trim()
        if (!m.isNullOrEmpty()) {
            when {
                m.startsWith("image/") -> return FileKind.IMAGE
                m.startsWith("video/") -> return FileKind.VIDEO
                m.startsWith("audio/") -> return FileKind.AUDIO
                m == "application/pdf" -> return FileKind.PDF
                m == "application/vnd.android.package-archive" -> return FileKind.APP
                m.startsWith("text/") -> return FileKind.TEXT
                m.contains("officedocument") || m.contains("opendocument") ||
                    m.contains("msword") || m.contains("ms-word") ||
                    m.contains("ms-excel") || m.contains("ms-powerpoint") ||
                    m.contains("rtf") -> return FileKind.DOCUMENT
                m.contains("zip") || m.contains("rar") || m.contains("7z") ||
                    m.contains("gzip") || m.contains("tar") -> return FileKind.ARCHIVE
            }
        }
        val ext = name?.substringAfterLast('.', "")?.lowercase()?.takeIf { it.isNotEmpty() && it.length <= 8 }
            ?: return FileKind.OTHER
        return extKind(ext)
    }

    /** Human-friendly size: "512 KB", "4.2 MB", "1.3 GB". */
    fun formatSize(bytes: Long): String {
        if (bytes < 0) return "?"
        val k = 1024.0
        return when {
            bytes < 1024 -> "$bytes B"
            bytes < 1024L * 1024 -> "${Math.round(bytes / k)} KB"
            bytes < 1024L * 1024 * 1024 -> String.format(java.util.Locale.US, "%.1f MB", bytes / (k * k))
            else -> String.format(java.util.Locale.US, "%.1f GB", bytes / (k * k * k))
        }
    }

    private fun extKind(ext: String): FileKind = when (ext) {
        "jpg", "jpeg", "png", "gif", "webp", "heic", "heif", "bmp", "avif", "tif", "tiff" -> FileKind.IMAGE
        "mp4", "mkv", "mov", "avi", "webm", "m4v", "3gp", "3gpp", "mpeg", "mpg", "ts", "flv" -> FileKind.VIDEO
        "mp3", "wav", "flac", "aac", "ogg", "oga", "m4a", "opus", "amr", "mid", "midi" -> FileKind.AUDIO
        "pdf" -> FileKind.PDF
        "doc", "docx", "xls", "xlsx", "ppt", "pptx", "odt", "ods", "odp", "rtf", "pages", "numbers", "key" -> FileKind.DOCUMENT
        "txt", "md", "csv", "tsv", "log", "json", "xml", "yaml", "yml", "ini", "cfg" -> FileKind.TEXT
        "zip", "rar", "7z", "gz", "gzip", "tar", "tgz", "bz2", "xz", "zst" -> FileKind.ARCHIVE
        "apk", "aab", "jar", "exe", "msi", "appx", "msix" -> FileKind.APP
        else -> FileKind.OTHER
    }
}
