using System.Text;

namespace FlipDrop.Core;

/// <summary>
/// Content sniffing by magic bytes. The declared Content-Type and the client-supplied file name are
/// never trusted; the real type is derived solely from these signatures. Only the first bytes of a file
/// are needed (the receiver sniffs the first 64 bytes it streams to disk).
/// </summary>
public static class MagicBytes
{
    /// <summary>
    /// Returns the canonical extension (no dot) for a recognised type, or null when nothing matched
    /// (caller then falls back to a text/phone-extension/bin decision). ZIP containers return "zip";
    /// the caller decides whether a known ZIP-based extension (docx, apk, …) should be kept instead.
    /// </summary>
    public static string? Sniff(ReadOnlySpan<byte> b)
    {
        // --- Windows executable (PE) ---
        if (Has(b, 0, (byte)'M', (byte)'Z')) return "exe";                 // MZ (risky)

        // --- Images ---
        if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return "jpg";
        if (Has(b, 0, 0x89, 0x50, 0x4E, 0x47)) return "png";               // .PNG
        if (Has(b, 0, (byte)'G', (byte)'I', (byte)'F', (byte)'8')) return "gif";
        if (Has(b, 0, 0x42, 0x4D) && b.Length >= 2) return "bmp";          // BM
        if (Has(b, 0, 0x49, 0x49, 0x2A, 0x00) || Has(b, 0, 0x4D, 0x4D, 0x00, 0x2A)) return "tif"; // TIFF

        // RIFF container: WEBP / WAV / AVI
        if (Has(b, 0, (byte)'R', (byte)'I', (byte)'F', (byte)'F') && b.Length >= 12)
        {
            if (Has(b, 8, (byte)'W', (byte)'E', (byte)'B', (byte)'P')) return "webp";
            if (Has(b, 8, (byte)'W', (byte)'A', (byte)'V', (byte)'E')) return "wav";
            if (Has(b, 8, (byte)'A', (byte)'V', (byte)'I', (byte)' ')) return "avi";
        }

        // --- ISO base media (ftyp box): heic/heif/avif, mp4/m4v/mov/3gp, m4a ---
        if (b.Length >= 12 && Has(b, 4, (byte)'f', (byte)'t', (byte)'y', (byte)'p'))
        {
            var brand = Ascii(b, 8, 4).Trim().ToLowerInvariant();
            var ext = MapFtypBrand(brand);
            if (ext is not null) return ext;
            // Unknown brand but a valid ftyp box: treat as generic mp4.
            return "mp4";
        }

        // --- Matroska / WebM (EBML) ---
        if (Has(b, 0, 0x1A, 0x45, 0xDF, 0xA3))
            return ContainsAscii(b, "webm") ? "webm" : "mkv";

        // --- Audio ---
        if (Has(b, 0, (byte)'I', (byte)'D', (byte)'3')) return "mp3";       // ID3 tag
        if (b.Length >= 2 && b[0] == 0xFF && (b[1] == 0xFB || b[1] == 0xF3 || b[1] == 0xF2)) return "mp3"; // frame sync
        if (Has(b, 0, (byte)'O', (byte)'g', (byte)'g', (byte)'S')) return "ogg";
        if (Has(b, 0, (byte)'f', (byte)'L', (byte)'a', (byte)'C')) return "flac";

        // --- Documents / archives ---
        if (Has(b, 0, 0x25, 0x50, 0x44, 0x46)) return "pdf";               // %PDF
        if (Has(b, 0, 0x50, 0x4B, 0x03, 0x04)) return "zip";               // PK..  (docx/xlsx/apk/jar/... decided by caller)
        if (Has(b, 0, 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C)) return "7z";
        if (Has(b, 0, (byte)'R', (byte)'a', (byte)'r', (byte)'!', 0x1A, 0x07)) return "rar";
        if (Has(b, 0, 0x1F, 0x8B)) return "gz";

        return null;
    }

    private static string? MapFtypBrand(string brand) => brand switch
    {
        "heic" or "heix" or "hevc" or "heim" or "heis" or "mif1" or "msf1" or "heif" => "heic",
        "avif" or "avis" => "avif",
        "m4a" => "m4a",
        "m4v" => "m4v",
        "qt" => "mov",
        "mp41" or "mp42" or "isom" or "iso2" or "iso4" or "iso5" or "mmp4" or "dash" or "mp4" => "mp4",
        _ when brand.StartsWith("3gp", StringComparison.Ordinal) || brand.StartsWith("3g2", StringComparison.Ordinal) => "3gp",
        _ => null
    };

    private static bool Has(ReadOnlySpan<byte> b, int offset, params byte[] sig)
    {
        if (b.Length < offset + sig.Length) return false;
        for (int i = 0; i < sig.Length; i++)
            if (b[offset + i] != sig[i]) return false;
        return true;
    }

    private static string Ascii(ReadOnlySpan<byte> b, int offset, int len)
    {
        if (b.Length < offset + len) len = Math.Max(0, b.Length - offset);
        return len <= 0 ? "" : Encoding.ASCII.GetString(b.Slice(offset, len));
    }

    private static bool ContainsAscii(ReadOnlySpan<byte> b, string needle)
    {
        var n = Encoding.ASCII.GetBytes(needle);
        for (int i = 0; i + n.Length <= b.Length; i++)
        {
            bool all = true;
            for (int j = 0; j < n.Length; j++)
                if (b[i + j] != n[j]) { all = false; break; }
            if (all) return true;
        }
        return false;
    }
}
