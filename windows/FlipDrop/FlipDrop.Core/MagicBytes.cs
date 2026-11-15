namespace FlipDrop.Core;

/// <summary>
/// Content sniffing by magic bytes. The declared Content-Type is never trusted;
/// the file extension is derived solely from these signatures.
/// </summary>
public static class MagicBytes
{
    /// <summary>Returns the canonical extension (no dot) or null if the bytes are not a supported image.</summary>
    public static string? Detect(ReadOnlySpan<byte> b)
    {
        if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF)
            return "jpg";                                                  // JPEG  FF D8 FF
        if (b.Length >= 4 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47)
            return "png";                                                  // PNG   89 50 4E 47
        if (b.Length >= 12 && b[0] == (byte)'R' && b[1] == (byte)'I' && b[2] == (byte)'F' && b[3] == (byte)'F'
            && b[8] == (byte)'W' && b[9] == (byte)'E' && b[10] == (byte)'B' && b[11] == (byte)'P')
            return "webp";                                                 // WEBP  RIFF....WEBP
        if (b.Length >= 4 && b[0] == (byte)'G' && b[1] == (byte)'I' && b[2] == (byte)'F' && b[3] == (byte)'8')
            return "gif";                                                  // GIF   GIF8
        return null;
    }
}
