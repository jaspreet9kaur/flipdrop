using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Net;
using BadHttpRequestException = Microsoft.AspNetCore.Http.BadHttpRequestException;

namespace FlipDrop.Core;

public sealed class PhotoReceivedEventArgs : EventArgs
{
    public required string FilePath { get; init; }
    public required string FileName { get; init; }
    public double? FlingX { get; init; }
    public double? FlingVelocity { get; init; }
}

public sealed record PairRequest(string? code, string? deviceName, string? deviceId);

/// <summary>
/// Kestrel HTTPS receiver (TCP 47801). Hosts /v1/pair, /v1/ping and /v1/photos.
/// A connection gate drops any non-private remote address with 403 before routing.
/// </summary>
public sealed class FlipDropServer : IAsyncDisposable
{
    public const int HttpsPort = 47801;
    public const long MaxBytes = 64L * 1024 * 1024;

    private readonly CertificateManager _cert;
    private readonly PairingService _pairing;
    private readonly DeviceStore _devices;
    private readonly Func<string> _saveFolder;
    private WebApplication? _app;

    public event EventHandler<PhotoReceivedEventArgs>? PhotoReceived;

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
            k.Limits.MaxRequestBodySize = MaxBytes;
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
        app.MapPost("/v1/photos", HandlePhotosAsync);

        await app.StartAsync();
        _app = app;
    }

    private async Task HandlePairAsync(HttpContext ctx)
    {
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
                await Json(ctx, 429, new { error = "locked" });
                break;
            default:
                await Json(ctx, 401, new { error = "bad_code" });
                break;
        }
    }

    private async Task HandlePing(HttpContext ctx)
    {
        if (!TryAuthenticate(ctx, out _))
        {
            await Json(ctx, 401, new { error = "unauthorized" });
            return;
        }
        await Json(ctx, 200, new { name = Environment.MachineName });
    }

    private async Task HandlePhotosAsync(HttpContext ctx)
    {
        if (!TryAuthenticate(ctx, out _))
        {
            await Json(ctx, 401, new { error = "unauthorized" });
            return;
        }

        byte[] bytes;
        try
        {
            using var ms = new MemoryStream();
            await ctx.Request.Body.CopyToAsync(ms, ctx.RequestAborted);
            bytes = ms.ToArray();
        }
        catch (BadHttpRequestException) // Kestrel raises this when the body exceeds MaxRequestBodySize
        {
            await Json(ctx, 413, new { error = "too_large" });
            return;
        }

        if (bytes.LongLength > MaxBytes)
        {
            await Json(ctx, 413, new { error = "too_large" });
            return;
        }

        var ext = MagicBytes.Detect(bytes);
        if (ext is null)
        {
            await Json(ctx, 415, new { error = "unsupported_media_type" });
            return;
        }

        // File name is generated by the laptop. The client-supplied X-File-Name never touches the path.
        var folder = _saveFolder();
        Directory.CreateDirectory(folder);
        var fileName = UniqueName(folder, ext);
        var fullPath = Path.Combine(folder, fileName);
        await File.WriteAllBytesAsync(fullPath, bytes, ctx.RequestAborted);

        var args = new PhotoReceivedEventArgs
        {
            FilePath = fullPath,
            FileName = fileName,
            FlingX = ParseDouble(ctx.Request.Headers["X-Fling-X"]),
            FlingVelocity = ParseDouble(ctx.Request.Headers["X-Fling-Velocity"])
        };
        PhotoReceived?.Invoke(this, args);

        await Json(ctx, 200, new { saved = fileName });
    }

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

    private static string UniqueName(string folder, string ext)
    {
        var baseName = $"Fling_{DateTime.Now:yyyyMMdd_HHmmss_fff}";
        var name = $"{baseName}.{ext}";
        var n = 1;
        while (File.Exists(Path.Combine(folder, name)))
            name = $"{baseName}_{n++}.{ext}";
        return name;
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
    }
}
