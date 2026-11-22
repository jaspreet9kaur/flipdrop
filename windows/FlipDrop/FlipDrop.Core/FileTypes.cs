using System.Globalization;

namespace FlipDrop.Core;

/// <summary>Coarse category a received file falls into, used by the UI for icons and handling.</summary>
public enum FileKind { Image, Video, Audio, Pdf, Document, Text, Archive, App, Other }

/// <summary>
/// Maps file extensions to a <see cref="FileKind"/>, flags risky (executable / script) extensions,
/// and formats byte sizes for display. All extension checks are case-insensitive and take the
/// extension without a leading dot.
/// </summary>
public static class FileTypes
{
    private static readonly HashSet<string> Images = new(StringComparer.OrdinalIgnoreCase)
    { "jpg", "jpeg", "jpe", "png", "gif", "webp", "bmp", "dib", "tif", "tiff", "heic", "heif", "avif" };

    private static readonly HashSet<string> Videos = new(StringComparer.OrdinalIgnoreCase)
    { "mp4", "m4v", "mov", "qt", "3gp", "3g2", "mkv", "webm", "avi", "wmv", "flv", "mpg", "mpeg", "m2ts", "ts" };

    private static readonly HashSet<string> Audios = new(StringComparer.OrdinalIgnoreCase)
    { "mp3", "m4a", "wav", "wave", "ogg", "oga", "opus", "flac", "aac", "wma", "mid", "midi" };

    private static readonly HashSet<string> Documents = new(StringComparer.OrdinalIgnoreCase)
    { "doc", "docx", "dot", "dotx", "xls", "xlsx", "xlsm", "ppt", "pptx", "odt", "ods", "odp", "rtf", "epub" };

    private static readonly HashSet<string> Texts = new(StringComparer.OrdinalIgnoreCase)
    { "txt", "text", "md", "markdown", "csv", "tsv", "log", "json", "xml", "yaml", "yml",
      "html", "htm", "css", "ini", "cfg", "conf" };

    private static readonly HashSet<string> Archives = new(StringComparer.OrdinalIgnoreCase)
    { "zip", "7z", "rar", "gz", "tar", "tgz", "bz2", "xz" };

    private static readonly HashSet<string> Apps = new(StringComparer.OrdinalIgnoreCase)
    { "exe", "com", "scr", "msi", "msp", "msix", "msixbundle", "appx", "apk", "jar", "dll", "sys", "cpl", "pif" };

    // Risky = programs and scripts (protocol 5.3 item 5). Saved but never opened by FlipDrop.
    private static readonly HashSet<string> Risky = new(StringComparer.OrdinalIgnoreCase)
    {
        "exe", "com", "scr", "msi", "msp", "bat", "cmd", "ps1", "psm1", "vbs", "vbe", "js", "jse",
        "wsf", "wsh", "hta", "lnk", "url", "pif", "reg", "cpl", "inf", "dll", "sys", "jar", "apk",
        "appx", "msix", "msixbundle", "iso", "img", "vhd", "vhdx"
    };

    /// <summary>Maps an extension (without dot) to its <see cref="FileKind"/>. Unknown → Other.</summary>
    public static FileKind KindOfExtension(string extensionWithoutDot)
    {
        var ext = (extensionWithoutDot ?? "").Trim();
        if (ext.Length == 0) return FileKind.Other;
        if (ext.Equals("pdf", StringComparison.OrdinalIgnoreCase)) return FileKind.Pdf;
        if (Images.Contains(ext)) return FileKind.Image;
        if (Videos.Contains(ext)) return FileKind.Video;
        if (Audios.Contains(ext)) return FileKind.Audio;
        if (Documents.Contains(ext)) return FileKind.Document;
        if (Texts.Contains(ext)) return FileKind.Text;
        if (Archives.Contains(ext)) return FileKind.Archive;
        if (Apps.Contains(ext)) return FileKind.App;
        return FileKind.Other;
    }

    /// <summary>True for programs and scripts that must never be auto-opened (protocol 5.3 item 5).</summary>
    public static bool IsRiskyExtension(string extensionWithoutDot)
        => !string.IsNullOrEmpty(extensionWithoutDot) && Risky.Contains(extensionWithoutDot.Trim());

    /// <summary>Human-readable size, e.g. "512 KB", "4.2 MB", "1.3 GB".</summary>
    public static string FormatSize(long bytes)
    {
        if (bytes < 0) bytes = 0;
        if (bytes < 1024) return $"{bytes} B";

        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes;
        int u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }

        double r = Math.Round(v, 1, MidpointRounding.AwayFromZero);
        string num = r == Math.Floor(r)
            ? ((long)r).ToString(CultureInfo.InvariantCulture)
            : r.ToString("0.0", CultureInfo.InvariantCulture);
        return $"{num} {units[u]}";
    }
}
