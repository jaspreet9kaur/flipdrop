using System.IO;
using FlipDrop.Core;

namespace FlipDrop.App;

/// <summary>
/// Decides which received files FlipDrop is willing to hand to the shell with "Open", and whether a
/// remembered path still lives inside the current save folder.
///
/// Opening uses a positive <b>allowlist</b> of safe, viewer-friendly extensions whose sniffed
/// <see cref="FileKind"/> agrees, rather than a denylist. A denylist silently lets any shell-executable
/// type it hasn't heard of (e.g. <c>chm</c>, <c>scf</c>, <c>hlp</c>, <c>diagcab</c>, <c>msc</c>,
/// <c>jnlp</c>, <c>wsb</c>) through to <c>ShellExecute</c>; an allowlist cannot. <see cref="FileTypes.IsRiskyExtension"/>
/// stays as a secondary block. Everything not on the allowlist is "Show in folder" only.
/// </summary>
internal static class OpenPolicy
{
    // Extensions FlipDrop is willing to open. Kept in sync with the FlipDrop 1.1 protocol's safe set.
    private static readonly HashSet<string> Openable = new(StringComparer.OrdinalIgnoreCase)
    {
        // images
        "jpg", "jpeg", "png", "gif", "webp", "bmp", "tif", "tiff", "heic", "heif", "avif",
        // video
        "mp4", "m4v", "mov", "mkv", "webm", "avi", "3gp", "wmv",
        // audio
        "mp3", "m4a", "wav", "ogg", "flac", "aac", "wma", "opus",
        // pdf
        "pdf",
        // documents + text
        "doc", "docx", "xls", "xlsx", "ppt", "pptx", "odt", "ods", "odp", "rtf",
        "txt", "md", "csv", "epub",
    };

    private static readonly FileKind[] OpenableKinds =
        { FileKind.Image, FileKind.Video, FileKind.Audio, FileKind.Pdf, FileKind.Document, FileKind.Text };

    /// <summary>
    /// True only when FlipDrop may "Open" the file: a safe, allowlisted extension whose sniffed kind is a
    /// viewer-friendly one, and never a risky (program/script) extension. Everything else is "Show in
    /// folder" only. The single gate used by both the toast and the history list.
    /// </summary>
    public static bool CanOpen(string? path, FileKind kind)
    {
        if (string.IsNullOrEmpty(path)) return false;
        var ext = Path.GetExtension(path).TrimStart('.');
        if (ext.Length == 0) return false;
        if (FileTypes.IsRiskyExtension(ext)) return false;   // secondary guard
        if (!Openable.Contains(ext)) return false;           // positive allowlist
        return Array.IndexOf(OpenableKinds, kind) >= 0;      // sniffed kind must agree
    }

    /// <summary>
    /// True when <paramref name="path"/> resolves to a location inside <paramref name="folder"/>.
    /// Used to reject remembered history entries that point anywhere the receiver never wrote. Any
    /// malformed path is treated as "not contained".
    /// </summary>
    public static bool IsInside(string? folder, string? path)
    {
        if (string.IsNullOrEmpty(folder) || string.IsNullOrEmpty(path)) return false;
        try
        {
            var root = Path.GetFullPath(folder);
            if (root.Length == 0) return false;
            if (root[^1] != Path.DirectorySeparatorChar && root[^1] != Path.AltDirectorySeparatorChar)
                root += Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(path);
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
}
