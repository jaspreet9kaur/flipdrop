using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Net;
using BadHttpRequestException = Microsoft.AspNetCore.Http.BadHttpRequestException;

namespace FlipDrop.Core;

public sealed record PairRequest(string? code, string? deviceName, string? deviceId);

/// <summary>
/// Kestrel HTTPS receiver (TCP 47801). Hosts /v1/pair, /v1/ping, /v1/photos and /v2/files.
/// A connection gate drops any non-private remote address with 403 before routing.
///
/// Uploads are streamed straight to a hidden incoming folder with a 1 MiB buffer — nothing is held
/// whole in memory — then atomically moved into the save folder. The type is sniffed from the first
/// bytes and always wins over the client-supplied name and MIME type.
/// </summary>
public sealed class FlipDropServer : IAsyncDisposable
{
    public const int HttpsPort = 47801;
    public const long MaxFileBytes = 4L * 1024 * 1024 * 1024; // /v2/files cap (4 GiB)
    public const long MaxV1Bytes = 64L * 1024 * 1024;         // /v1/photos cap (64 MiB)

    private const long PairBodyLimit = 4L * 1024;             // /v1/pair body cap (4 KiB)
    private const long FreeSpaceReserve = 512L * 1024 * 1024; // keep this much free on the save drive
    private const int StreamBuffer = 1 << 20;                 // 1 MiB

    private readonly CertificateManager _cert;
    private readonly PairingService _pairing;
    private readonly DeviceStore _devices;
    private readonly Func<string> _saveFolder;
    private readonly SemaphoreSlim _uploadGate = new(3, 3);   // at most 3 uploads writing at once
    private long _reservedBytes;                              // sum of in-flight Content-Lengths (free-space reservation)
    private WebApplication? _app;

    public event EventHandler<TransferStartedEventArgs>? TransferStarted;
    public event EventHandler<TransferProgressEventArgs>? TransferProgress;
    public event EventHandler<FileReceivedEventArgs>? FileReceived;
    public event EventHandler<TransferFailedEventArgs>? TransferFailed;

    public FlipDropServer(CertificateManager cert, PairingService pairing, DeviceStore devices, Func<string> saveFolder)
    {
        _cert = cert;
        _pairing = pairing;
        _devices = devices;
        _saveFolder = saveFolder;
    }

    public async Task StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders(); // never log request bodies / headers (tokens)
#if DEBUG
        if (Environment.GetEnvironmentVariable("FLIPDROP_DEBUG") == "1")
        {
            builder.Logging.AddConsole();
            builder.Logging.SetMinimumLevel(LogLevel.Debug);
        }
#endif
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.AddServerHeader = false;
            k.Limits.MaxRequestBodySize = MaxFileBytes;                 // global cap; tightened per endpoint
            k.Limits.MaxConcurrentConnections = 64;
            k.Limits.MaxRequestHeadersTotalSize = 16 * 1024;
            k.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
            k.Limits.MinRequestBodyDataRate = new MinDataRate(bytesPerSecond: 1024, gracePeriod: TimeSpan.FromSeconds(10));
            k.Listen(IPAddress.Any, HttpsPort, listen => listen.UseHttps(_cert.Certificate));
        });

        var app = builder.Build();

        // Private-range gate: reject everything that is not loopback / RFC1918 / link-local.
        app.Use(async (ctx, next) =>
        {
            if (!PrivateIp.IsAllowed(ctx.Connection.RemoteIpAddress))
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                await ctx.Response.WriteAsJsonAsync(new { error = "forbidden" });
                return;
            }
            await next();
        });

        app.MapPost("/v1/pair", HandlePairAsync);
        app.MapGet("/v1/ping", HandlePing);
        app.MapPost("/v1/photos", ctx => HandleUploadAsync(ctx, v2: false));
        app.MapPost("/v2/files", ctx => HandleUploadAsync(ctx, v2: true));

        await app.StartAsync();
        _app = app;
    }

    // ---- /v1/pair ----------------------------------------------------------

    private async Task HandlePairAsync(HttpContext ctx)
    {
        SetBodyLimit(ctx, PairBodyLimit);

        PairRequest? req;
        try
        {
            req = await ctx.Request.ReadFromJsonAsync<PairRequest>();
        }
        catch
        {
            await Json(ctx, 401, new { error = "bad_code" });
            return;
        }

        if (req is null || string.IsNullOrEmpty(req.code))
        {
            await Json(ctx, 401, new { error = "bad_code" });
            return;
        }

        var result = _pairing.TryPair(req.code, req.deviceId ?? "", req.deviceName ?? "", out var token);
        switch (result)
        {
            case PairingService.PairResult.Ok:
                await Json(ctx, 200, new { token, name = Environment.MachineName });
                break;
            case PairingService.PairResult.Locked:
                var retryAfter = 1;
                if (_pairing.LockedUntil is DateTimeOffset until)
                    retryAfter = Math.Max(1, (int)Math.Ceiling((until - DateTimeOffset.UtcNow).TotalSeconds));
                ctx.Response.Headers.RetryAfter = retryAfter.ToString(CultureInfo.InvariantCulture);
                await Json(ctx, 429, new { error = "locked", retryAfter });
                break;
            default:
                await Json(ctx, 401, new { error = "bad_code" });
                break;
        }
    }

    // ---- /v1/ping ----------------------------------------------------------

    private async Task HandlePing(HttpContext ctx)
    {
        if (!TryAuthenticate(ctx, out _))
        {
            await Json(ctx, 401, new { error = "unauthorized" });
            return;
        }
        await Json(ctx, 200, new { name = Environment.MachineName, pv = 2, maxBytes = MaxFileBytes });
    }

    // ---- /v1/photos and /v2/files (shared streaming path) ------------------

    private async Task HandleUploadAsync(HttpContext ctx, bool v2)
    {
        if (!TryAuthenticate(ctx, out var device))
        {
            await Json(ctx, 401, new { error = "unauthorized" });
            return;
        }

        var maxBytes = v2 ? MaxFileBytes : MaxV1Bytes;
        SetBodyLimit(ctx, maxBytes);

        long? declared = ctx.Request.ContentLength;

        if (v2 && declared is null)
        {
            await Json(ctx, 411, new { error = "length_required" });
            return;
        }

        if (declared is long len && len > maxBytes)
        {
            await Json(ctx, 413, new { error = "too_large" });
            return;
        }

        var folder = _saveFolder();
        try { Directory.CreateDirectory(folder); } catch { /* reported later if the write fails */ }

        long reservedBytes = 0; // non-zero while this upload holds a slice of the free-space reserve
        bool acquired = false;
        try
        {
            // Free-space check (v2): keep at least 512 MiB free on the save drive after the file.
            // Reserve this request's Content-Length so concurrent uploads can't jointly eat the reserve:
            // the check accounts for the sum of all in-flight declared sizes, not just this one.
            if (v2 && declared is long want)
            {
                reservedBytes = want;
                var inFlight = Interlocked.Add(ref _reservedBytes, want);
                try
                {
                    var root = Path.GetPathRoot(Path.GetFullPath(folder));
                    if (!string.IsNullOrEmpty(root))
                    {
                        var drive = new DriveInfo(root);
                        if (drive.IsReady && drive.AvailableFreeSpace - inFlight < FreeSpaceReserve)
                        {
                            await Json(ctx, 507, new { error = "disk_full" });
                            return;
                        }
                    }
                }
                catch { /* can't determine free space — don't block the transfer */ }
            }

            // Concurrency gate: wait up to 30 s for a slot, then 503.
            try { acquired = await _uploadGate.WaitAsync(TimeSpan.FromSeconds(30), ctx.RequestAborted); }
            catch (OperationCanceledException) { return; }
            if (!acquired)
            {
                ctx.Response.Headers.RetryAfter = "2";
                await Json(ctx, 503, new { error = "busy" });
                return;
            }

            var transferId = Guid.NewGuid();
            var deviceName = device?.DeviceName ?? "Device";
            var flingX = ParseDouble(ctx.Request.Headers["X-Fling-X"]);
            var flingV = ParseDouble(ctx.Request.Headers["X-Fling-Velocity"]);
            var total = declared ?? -1;

            var incoming = Path.Combine(folder, ".flipdrop-incoming");
            var partPath = Path.Combine(incoming, transferId.ToString("N") + ".part");

            var head = new byte[64];
            var headLen = 0;
            long received = 0;
            var started = false;    // TransferStarted raised (type resolved)
            var completed = false;  // FileReceived raised: the one terminal event has fired
            var reject415 = false;
            string stem = "", ext = "bin";
            var kind = FileKind.Other;
            var risky = false;
            long lastProgressTick = 0;
            var writing = false;    // true only while inside fs.WriteAsync (to classify IOExceptions)

            try
            {
                EnsureIncomingDir(incoming);

                await using (var fs = new FileStream(partPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                                 StreamBuffer, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    var buffer = new byte[StreamBuffer];
                    int read;
                    while ((read = await ctx.Request.Body.ReadAsync(buffer, ctx.RequestAborted)) > 0)
                    {
                        // Keep accumulating the 64-byte head across chunks; the type is resolved only once
                        // the whole head is in hand (or, for a short body, after the loop). This avoids
                        // sniffing a partial head when the first read is tiny (which wrongly rejected v1
                        // JPEGs whose first chunk was shorter than the signature).
                        if (headLen < head.Length)
                        {
                            var c = Math.Min(head.Length - headLen, read);
                            Array.Copy(buffer, 0, head, headLen, c);
                            headLen += c;
                        }

                        writing = true;
                        await fs.WriteAsync(buffer.AsMemory(0, read), ctx.RequestAborted);
                        writing = false;
                        received += read;

                        if (!started && headLen >= head.Length)
                        {
                            if (!ResolveType(ctx, v2, head.AsSpan(0, headLen), out stem, out ext, out kind, out risky))
                            {
                                reject415 = true;
                                break;
                            }
                            RaiseStarted(transferId, stem, ext, kind, total, deviceName, flingX, flingV);
                            started = true;
                        }

                        if (started)
                        {
                            var nowTick = Environment.TickCount64;
                            if (nowTick - lastProgressTick >= 200) // <= 5 events/second per transfer
                            {
                                lastProgressTick = nowTick;
                                TransferProgress?.Invoke(this, new TransferProgressEventArgs
                                {
                                    TransferId = transferId,
                                    BytesReceived = received,
                                    TotalBytes = total < 0 ? received : total
                                });
                            }
                        }
                    }

                    // Flush buffered bytes here (not on an implicit dispose) so a disk-full surfaces as a
                    // write IOException we can turn into 507 rather than a generic error at block exit.
                    if (!reject415)
                    {
                        writing = true;
                        await fs.FlushAsync(ctx.RequestAborted);
                        writing = false;
                    }
                }

                if (reject415)
                {
                    TryDeleteFile(partPath);
                    await Json(ctx, 415, new { error = "unsupported_media_type" });
                    return;
                }

                // Short body or body <= 64 bytes: the head was never full, so resolve it now,
                // announcing (TransferStarted) before the terminal event.
                if (!started)
                {
                    if (!ResolveType(ctx, v2, head.AsSpan(0, headLen), out stem, out ext, out kind, out risky))
                    {
                        TryDeleteFile(partPath);
                        await Json(ctx, 415, new { error = "unsupported_media_type" });
                        return;
                    }
                    RaiseStarted(transferId, stem, ext, kind, total, deviceName, flingX, flingV);
                    started = true;
                }

                // Body must be exactly as long as declared (v2 always declares a length).
                if (declared is long expect && received != expect)
                {
                    TryDeleteFile(partPath);
                    TransferFailed?.Invoke(this, new TransferFailedEventArgs { TransferId = transferId, Reason = "incomplete" });
                    await Json(ctx, 400, new { error = "incomplete" });
                    return;
                }

                var (fileName, fullPath) = MoveUnique(partPath, folder, stem, ext);
                WriteMarkOfTheWeb(fullPath);

                // The file is saved: this is the single terminal event. Any later failure (e.g. writing
                // the response to a client that has already disconnected) must NOT also raise TransferFailed.
                completed = true;
                FileReceived?.Invoke(this, new FileReceivedEventArgs
                {
                    TransferId = transferId,
                    FilePath = fullPath,
                    FileName = fileName,
                    Kind = kind,
                    SizeBytes = received,
                    IsRisky = risky,
                    DeviceName = deviceName,
                    FlingX = flingX,
                    FlingVelocity = flingV
                });

                try
                {
                    if (v2)
                        await Json(ctx, 200, new { saved = fileName, kind = KindString(kind), size = received });
                    else
                        await Json(ctx, 200, new { saved = fileName });
                }
                catch { /* file is saved; failing to write the 200 to a vanished client is still success */ }
            }
            catch when (completed)
            {
                // File already saved and FileReceived already raised (e.g. a subscriber threw). The transfer
                // succeeded; swallow so no second terminal event fires and no error status overrides the 200.
            }
            catch (BadHttpRequestException bhr) when (!completed)
            {
                TryDeleteFile(partPath);
                if (started)
                    TransferFailed?.Invoke(this, new TransferFailedEventArgs { TransferId = transferId, Reason = "incomplete" });
                if (!ctx.Response.HasStarted)
                {
                    if (bhr.StatusCode == StatusCodes.Status413PayloadTooLarge)
                        await Json(ctx, 413, new { error = "too_large" });
                    else
                        await Json(ctx, 400, new { error = "incomplete" });
                }
            }
            catch (OperationCanceledException) when (!completed)
            {
                TryDeleteFile(partPath);
                if (started)
                    TransferFailed?.Invoke(this, new TransferFailedEventArgs { TransferId = transferId, Reason = "aborted" });
                // The client is gone; there is nothing to respond to.
            }
            catch (IOException ioe) when (!completed && writing)
            {
                // A disk error while writing the part file (full disk, I/O failure). Distinct from a
                // connection fault reading the body, which surfaces as BadHttpRequest/OperationCanceled above.
                TryDeleteFile(partPath);
                var diskFull = IsDiskFull(ioe);
                if (started)
                    TransferFailed?.Invoke(this, new TransferFailedEventArgs { TransferId = transferId, Reason = diskFull ? "disk_full" : "write_failed" });
                if (!ctx.Response.HasStarted)
                {
                    if (diskFull)
                        await Json(ctx, 507, new { error = "disk_full" });
                    else
                        await Json(ctx, 500, new { error = "write_failed" });
                }
            }
            catch (Exception) when (!completed)
            {
                TryDeleteFile(partPath);
                if (started)
                    TransferFailed?.Invoke(this, new TransferFailedEventArgs { TransferId = transferId, Reason = "error" });
                if (!ctx.Response.HasStarted)
                    await Json(ctx, 500, new { error = "error" });
            }
        }
        finally
        {
            if (acquired) _uploadGate.Release();
            if (reservedBytes != 0) Interlocked.Add(ref _reservedBytes, -reservedBytes);
        }
    }

    // ERROR_DISK_FULL (0x70) and ERROR_HANDLE_DISK_FULL (0x27) surface as these HRESULTs on Windows.
    private static bool IsDiskFull(IOException ex)
    {
        var hr = unchecked((uint)ex.HResult);
        return hr is 0x80070070 or 0x80070027;
    }

    // ---- naming / sniffing -------------------------------------------------

    private static readonly char[] PathSeps = { '/', '\\' };
    private static readonly Regex PhoneExtRegex = new("^[a-z0-9]{1,8}$", RegexOptions.Compiled);
    private static readonly HashSet<string> ZipKeepExts = new(StringComparer.OrdinalIgnoreCase)
        { "docx", "xlsx", "pptx", "odt", "ods", "odp", "epub", "apk", "jar", "zip" };
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
        { "CON", "PRN", "AUX", "NUL" };

    /// <summary>
    /// Decides the final stem, extension, kind and risk from the sniffed bytes and the phone's name.
    /// Returns false only for /v1/photos when the content is not an allowed image (caller returns 415).
    /// </summary>
    private static bool ResolveType(HttpContext ctx, bool v2, ReadOnlySpan<byte> head,
        out string stem, out string ext, out FileKind kind, out bool risky)
    {
        var sniff = MagicBytes.Sniff(head);

        var decoded = SafeUnescape(ctx.Request.Headers["X-File-Name"].ToString());
        var slash = decoded.LastIndexOfAny(PathSeps);
        var segment = slash >= 0 ? decoded[(slash + 1)..] : decoded;
        var phoneExt = "";
        var stemRaw = segment;
        var dot = segment.LastIndexOf('.');
        if (dot > 0)
        {
            phoneExt = segment[(dot + 1)..].ToLowerInvariant();
            stemRaw = segment[..dot];
        }

        if (!v2)
        {
            // /v1/photos: images only (jpg/png/webp/gif); the name is laptop-generated.
            if (sniff is not ("jpg" or "png" or "webp" or "gif"))
            {
                stem = ""; ext = "bin"; kind = FileKind.Other; risky = false;
                return false;
            }
            ext = sniff!;
            kind = FileKind.Image;
            risky = false;
            stem = $"Fling_{DateTime.Now:yyyyMMdd_HHmmss_fff}";
            return true;
        }

        // ZIP containers keep a known office/apk/jar extension if the phone claims one.
        if (sniff == "zip")
            sniff = ZipKeepExts.Contains(phoneExt) ? phoneExt : "zip";

        ext = sniff ?? (PhoneExtRegex.IsMatch(phoneExt) ? phoneExt : "bin");

        kind = FileTypes.KindOfExtension(ext);
        if (kind == FileKind.Other && sniff is null && IsProbablyText(head))
            kind = FileKind.Text;
        risky = FileTypes.IsRiskyExtension(ext);

        stem = SanitizeStem(stemRaw);
        if (stem.Length == 0) stem = $"Fling_{DateTime.Now:yyyyMMdd_HHmmss}";
        return true;
    }

    private static string SanitizeStem(string raw)
    {
        var sb = new StringBuilder(raw.Length);
        for (var i = 0; i < raw.Length; i++)
        {
            var ch = raw[i];

            // Keep a valid surrogate pair as a unit (and drop it if its code point is a disallowed category);
            // drop any unpaired surrogate on its own.
            if (char.IsHighSurrogate(ch) && i + 1 < raw.Length && char.IsLowSurrogate(raw[i + 1]))
            {
                if (!IsDisallowedCategory(CharUnicodeInfo.GetUnicodeCategory(raw, i)))
                {
                    sb.Append(ch);
                    sb.Append(raw[i + 1]);
                }
                i++; // consumed the low surrogate too
                continue;
            }
            if (char.IsSurrogate(ch)) continue; // unpaired surrogate

            if (ch is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*') continue;
            // Drop control, format (bidi / RTLO U+202E etc.) and line/paragraph separators.
            if (IsDisallowedCategory(CharUnicodeInfo.GetUnicodeCategory(ch))) continue;
            sb.Append(ch);
        }
        var s = sb.ToString().Trim(' ', '.');
        s = TruncateTextElements(s, 100);
        s = s.Trim(' ', '.');
        if (IsReservedDeviceName(s)) s = "_" + s;
        return s;
    }

    private static bool IsDisallowedCategory(UnicodeCategory cat) => cat
        is UnicodeCategory.Control
        or UnicodeCategory.Format
        or UnicodeCategory.LineSeparator
        or UnicodeCategory.ParagraphSeparator
        or UnicodeCategory.Surrogate; // unpaired surrogate code point

    /// <summary>Truncates to at most <paramref name="maxChars"/> UTF-16 code units on a text-element
    /// boundary, so a surrogate pair or combining sequence is never split.</summary>
    private static string TruncateTextElements(string s, int maxChars)
    {
        if (s.Length <= maxChars) return s;
        var e = StringInfo.GetTextElementEnumerator(s);
        var count = 0;
        while (e.MoveNext())
        {
            var element = (string)e.Current;
            if (count + element.Length > maxChars) break;
            count += element.Length;
        }
        return s[..count];
    }

    private static bool IsReservedDeviceName(string s)
    {
        if (s.Length == 0) return false;
        if (ReservedNames.Contains(s)) return true;
        if (s.Length == 4 &&
            (s.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || s.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
            s[3] >= '0' && s[3] <= '9')
            return true;
        return false;
    }

    /// <summary>Atomic, no-overwrite move. On a name clash, appends " (1)", " (2)", … (no File.Exists TOCTOU).</summary>
    private static (string name, string full) MoveUnique(string partPath, string folder, string stem, string ext)
    {
        for (var n = 0; ; n++)
        {
            var name = n == 0 ? $"{stem}.{ext}" : $"{stem} ({n}).{ext}";
            var dest = Path.Combine(folder, name);
            try
            {
                File.Move(partPath, dest, overwrite: false);
                return (name, dest);
            }
            catch (IOException) when (n < 10000 && File.Exists(dest))
            {
                // Destination taken by a concurrent transfer — try the next suffix.
            }
        }
    }

    private static void EnsureIncomingDir(string incoming)
    {
        var di = Directory.CreateDirectory(incoming);
        try
        {
            if ((di.Attributes & FileAttributes.Hidden) == 0)
                di.Attributes |= FileAttributes.Hidden;
        }
        catch { /* non-NTFS or attributes not settable */ }
    }

    /// <summary>Marks the file as downloaded (Mark of the Web) so SmartScreen / Protected View apply.</summary>
    private static void WriteMarkOfTheWeb(string path)
    {
        try
        {
            using var ads = new FileStream(path + ":Zone.Identifier", FileMode.Create, FileAccess.Write, FileShare.None);
            ads.Write("[ZoneTransfer]\r\nZoneId=3\r\n"u8);
        }
        catch { /* best-effort; ignored on non-NTFS volumes */ }
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best-effort cleanup */ }
    }

    private static bool IsProbablyText(ReadOnlySpan<byte> b)
    {
        if (b.Length == 0) return false;
        foreach (var c in b)
        {
            if (c == 0x00) return false;
            if (c < 0x09) return false;
            if (c > 0x0D && c < 0x20) return false;
        }
        return true;
    }

    private void RaiseStarted(Guid id, string stem, string ext, FileKind kind, long total,
        string deviceName, double? flingX, double? flingV)
    {
        TransferStarted?.Invoke(this, new TransferStartedEventArgs
        {
            TransferId = id,
            DisplayName = $"{stem}.{ext}",
            Kind = kind,
            TotalBytes = total < 0 ? 0 : total,
            DeviceName = deviceName,
            FlingX = flingX,
            FlingVelocity = flingV
        });
    }

    private static string KindString(FileKind k) => k switch
    {
        FileKind.Image => "image",
        FileKind.Video => "video",
        FileKind.Audio => "audio",
        FileKind.Pdf => "pdf",
        FileKind.Document => "document",
        FileKind.Text => "text",
        FileKind.Archive => "archive",
        FileKind.App => "app",
        _ => "other"
    };

    // ---- helpers -----------------------------------------------------------

    private bool TryAuthenticate(HttpContext ctx, out PairedDevice? device)
    {
        device = null;
        var auth = ctx.Request.Headers.Authorization.ToString();
        const string scheme = "Bearer ";
        if (string.IsNullOrEmpty(auth) || !auth.StartsWith(scheme, StringComparison.Ordinal))
            return false;
        var token = auth[scheme.Length..].Trim();
        if (token.Length == 0) return false;
        return _devices.TryValidateToken(token, out device);
    }

    private static void SetBodyLimit(HttpContext ctx, long bytes)
    {
        var f = ctx.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (f is { IsReadOnly: false }) f.MaxRequestBodySize = bytes;
    }

    private static string SafeUnescape(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        try { return Uri.UnescapeDataString(s); } catch { return s; }
    }

    private static double? ParseDouble(string? s)
        => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static async Task Json(HttpContext ctx, int status, object body)
    {
        ctx.Response.StatusCode = status;
        await ctx.Response.WriteAsJsonAsync(body);
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync(TimeSpan.FromSeconds(2));
            await _app.DisposeAsync();
            _app = null;
        }
        _uploadGate.Dispose();
    }
}
