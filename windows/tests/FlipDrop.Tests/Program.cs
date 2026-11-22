using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FlipDrop.Core;

// FlipDrop protocol conformance test. Starts the real receiver code in-process
// (the same FlipDropServer + DiscoveryService the GUI app runs), drives it over
// 127.0.0.1, and stops it afterwards. Exit code 0 = all checks passed.

const string TestCode = "424242";
Environment.SetEnvironmentVariable("FLIPDROP_TEST_CODE", TestCode); // fixes the pairing code for this run only

var dataDir = Path.Combine(Path.GetTempPath(), "flipdrop-test-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(dataDir);
var saveFolder = Path.Combine(dataDir, "received");
Directory.CreateDirectory(saveFolder);

var t = new TestRun();

CertificateManager cert;
DeviceStore devices;
PairingService pairing;
FlipDropServer server;
DiscoveryService discovery;

try
{
    cert = new CertificateManager(dataDir);
    devices = new DeviceStore(Path.Combine(dataDir, "devices.dat"));
    pairing = new PairingService(devices);
    server = new FlipDropServer(cert, pairing, devices, () => saveFolder);
    await server.StartAsync();
    discovery = new DiscoveryService(cert);
    discovery.Start();
    await Task.Delay(300); // let sockets bind
}
catch (Exception ex)
{
    Console.WriteLine("FATAL: could not start services: " + ex);
    return 2;
}

try
{
    // 1) UDP discovery
    string? fp = null;
    try
    {
        var reply = await Discover();
        using var doc = JsonDocument.Parse(reply);
        var root = doc.RootElement;
        fp = root.GetProperty("fp").GetString();
        bool ok = root.GetProperty("app").GetString() == "flipdrop"
                  && root.GetProperty("v").GetInt32() == 1
                  && root.GetProperty("port").GetInt32() == FlipDropServer.HttpsPort
                  && !string.IsNullOrEmpty(fp);
        t.Check("UDP discovery returns descriptor with fp", ok, $"fp={Short(fp)} name={root.GetProperty("name").GetString()}");
        t.Check("Discovery fp matches certificate SHA-256", fp == cert.FingerprintHex);
    }
    catch (Exception ex)
    {
        t.Check("UDP discovery returns descriptor with fp", false, ex.Message);
    }

    // HttpClient that pins the discovered fingerprint (trust on first use), ignoring hostname.
    using var handler = new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = (_, c, _, _) =>
            c is not null && Convert.ToHexStringLower(SHA256.HashData(c.RawData)) == fp
    };
    using var http = new HttpClient(handler) { BaseAddress = new Uri($"https://127.0.0.1:{FlipDropServer.HttpsPort}") };
    http.Timeout = TimeSpan.FromMinutes(2);

    // 2) Pair with a WRONG code -> 401 bad_code
    {
        var r = await http.PostAsJsonAsync("/v1/pair", new { code = "000000", deviceName = "Tester", deviceId = "dev-wrong" });
        t.Check("Pair with wrong code -> 401", (int)r.StatusCode == 401, $"got {(int)r.StatusCode}");
    }

    // 3) Pair with the correct code -> 200 + token
    string token = "";
    {
        var r = await http.PostAsJsonAsync("/v1/pair", new { code = TestCode, deviceName = "Galaxy Z Flip5", deviceId = "dev-main" });
        var body = await r.Content.ReadAsStringAsync();
        if (r.IsSuccessStatusCode)
        {
            using var doc = JsonDocument.Parse(body);
            token = doc.RootElement.GetProperty("token").GetString() ?? "";
        }
        t.Check("Pair with correct code -> 200 + token", r.IsSuccessStatusCode && token.Length > 0, $"got {(int)r.StatusCode}, tokenLen={token.Length}");
    }

    // 4) Ping with the token -> 200
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/v1/ping");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var r = await http.SendAsync(req);
        t.Check("Ping with valid token -> 200", (int)r.StatusCode == 200, $"got {(int)r.StatusCode}");
    }

    // 5) Ping with a bad token -> 401
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/v1/ping");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-real-token");
        var r = await http.SendAsync(req);
        t.Check("Ping with bad token -> 401", (int)r.StatusCode == 401, $"got {(int)r.StatusCode}");
    }

    // 6) Upload a real JPEG with the token -> 200, generated name
    string savedName = "";
    {
        var jpeg = MakeJpeg();
        using var req = new HttpRequestMessage(HttpMethod.Post, "/v1/photos");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.TryAddWithoutValidation("X-File-Name", Uri.EscapeDataString("holiday.jpg"));
        req.Headers.TryAddWithoutValidation("X-Fling-X", "0.5");
        req.Headers.TryAddWithoutValidation("X-Fling-Velocity", "3200");
        req.Content = new ByteArrayContent(jpeg);
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        var r = await http.SendAsync(req);
        var body = await r.Content.ReadAsStringAsync();
        if (r.IsSuccessStatusCode)
        {
            using var doc = JsonDocument.Parse(body);
            savedName = doc.RootElement.GetProperty("saved").GetString() ?? "";
        }
        bool nameOk = Regex.IsMatch(savedName, @"^Fling_\d{8}_\d{6}_\d{3}\.jpg$");
        t.Check("Upload JPEG with token -> 200", r.IsSuccessStatusCode, $"got {(int)r.StatusCode}");
        t.Check("Saved file uses a generated Fling_* name", nameOk, $"saved={savedName}");
        t.Check("Saved file exists in the configured folder", File.Exists(Path.Combine(saveFolder, savedName)));
    }

    // 7) Upload with a BAD token -> 401
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/v1/photos");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "wrong-token");
        req.Content = new ByteArrayContent(MakeJpeg());
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        var r = await http.SendAsync(req);
        t.Check("Upload with bad token -> 401", (int)r.StatusCode == 401, $"got {(int)r.StatusCode}");
    }

    // 8) Upload non-image bytes (but image/* content-type) -> 415 (content-type is not trusted)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/v1/photos");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Content = new ByteArrayContent("this is definitely not an image"u8.ToArray());
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        var r = await http.SendAsync(req);
        t.Check("Upload non-image bytes -> 415", (int)r.StatusCode == 415, $"got {(int)r.StatusCode}");
    }

    // 9) Path-traversal in X-File-Name must never escape the save folder
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/v1/photos");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.TryAddWithoutValidation("X-File-Name", Uri.EscapeDataString(@"..\..\..\..\Windows\Temp\pwned.jpg"));
        req.Content = new ByteArrayContent(MakeJpeg());
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        var r = await http.SendAsync(req);
        var body = await r.Content.ReadAsStringAsync();
        string saved = "";
        if (r.IsSuccessStatusCode)
        {
            using var doc = JsonDocument.Parse(body);
            saved = doc.RootElement.GetProperty("saved").GetString() ?? "";
        }
        bool generated = Regex.IsMatch(saved, @"^Fling_\d{8}_\d{6}_\d{3}\.jpg$");
        bool noEscape = !File.Exists(@"C:\Windows\Temp\pwned.jpg")
                        && Directory.GetFiles(saveFolder, "pwned.jpg", SearchOption.AllDirectories).Length == 0;
        t.Check("Path-traversal file name is ignored (generated name used)", generated, $"saved={saved}");
        t.Check("No file escaped the save folder", noEscape);
    }

    // 10) Oversize body (> 64 MiB) -> rejected: either a 413 status, or the connection is reset while the
    //     oversize body is still being sent. Either way the upload must leave NOTHING behind on disk — so the
    //     "reset" branch no longer passes on its own; it must also prove no file and no .part were written.
    {
        var big = new byte[FlipDropServer.MaxV1Bytes + 1024];
        big[0] = 0xFF; big[1] = 0xD8; big[2] = 0xFF; // JPEG header so only size can reject it
        int status = -1;
        bool threw = false;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "/v1/photos");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Content = new ByteArrayContent(big);
            req.Content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
            var r = await http.SendAsync(req);
            status = (int)r.StatusCode;
        }
        catch
        {
            // Kestrel reset the connection as it rejected the oversize body — a rejection, not an acceptance.
            threw = true;
        }
        await Task.Delay(300); // let the server finish any cleanup
        var incoming = Path.Combine(saveFolder, ".flipdrop-incoming");
        int leftover = Directory.Exists(incoming) ? Directory.GetFiles(incoming, "*.part").Length : 0;
        bool noOversizeSaved = !Directory.EnumerateFiles(saveFolder)
            .Any(f => new FileInfo(f).Length >= FlipDropServer.MaxV1Bytes);
        bool rejected = status == 413 || threw;
        t.Check("Oversize upload rejected (413 or connection reset)", rejected && leftover == 0 && noOversizeSaved,
            $"status={status} threw={threw} leftover={leftover} oversizeSaved={!noOversizeSaved}");
    }

    // 11) Non-private UDP sender is ignored (sanity on the private-IP gate helper)
    {
        bool ok = !PrivateIp.IsAllowed(IPAddress.Parse("8.8.8.8"))
                  && PrivateIp.IsAllowed(IPAddress.Parse("192.168.1.5"))
                  && PrivateIp.IsAllowed(IPAddress.Loopback);
        t.Check("Private-IP gate accepts LAN/loopback, rejects public", ok);
    }

    // ===================== v2 (FlipDrop 1.1) checks =========================

    // 12) Ping advertises protocol v2 and the 4 GiB max
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/v1/ping");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var r = await http.SendAsync(req);
        var body = await r.Content.ReadAsStringAsync();
        bool ok = false;
        if (r.IsSuccessStatusCode)
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            ok = root.GetProperty("pv").GetInt32() == 2 && root.GetProperty("maxBytes").GetInt64() == FlipDropServer.MaxFileBytes;
        }
        t.Check("Ping advertises pv=2 and maxBytes=4 GiB", ok, body);
    }

    // 13) Discovery reply carries pv=2 and still v=1
    {
        try
        {
            var reply = await Discover();
            using var doc = JsonDocument.Parse(reply);
            var root = doc.RootElement;
            bool ok = root.GetProperty("pv").GetInt32() == 2 && root.GetProperty("v").GetInt32() == 1;
            t.Check("Discovery reply has pv=2 and v=1", ok, reply);
        }
        catch (Exception ex)
        {
            t.Check("Discovery reply has pv=2 and v=1", false, ex.Message);
        }
    }

    // 14) v2 PDF upload -> 200, kind "pdf" (and FileReceived event fires with Kind=Pdf)
    {
        FileReceivedEventArgs? ev = null;
        void OnReceived(object? _, FileReceivedEventArgs e) => ev = e;
        server.FileReceived += OnReceived;
        var (status, saved, kind, size) = await UploadV2(http, token, "report.pdf", MakePdf(), "application/pdf");
        server.FileReceived -= OnReceived;
        t.Check("v2 PDF upload -> 200 kind=pdf", status == 200 && kind == "pdf" && saved.EndsWith(".pdf"), $"status={status} kind={kind} saved={saved}");
        t.Check("v2 PDF saved file exists", File.Exists(Path.Combine(saveFolder, saved)));
        t.Check("FileReceived event fired with Kind=Pdf, not risky", ev is { Kind: FileKind.Pdf, IsRisky: false } && ev.SizeBytes == size, ev is null ? "no event" : $"kind={ev.Kind} risky={ev.IsRisky}");
    }

    // 15) v2 MP4 (ftyp box) -> kind "video", .mp4
    {
        var (status, saved, kind, _) = await UploadV2(http, token, "clip.mp4", MakeMp4(), "video/mp4");
        t.Check("v2 MP4 upload -> kind=video .mp4", status == 200 && kind == "video" && saved.EndsWith(".mp4"), $"status={status} kind={kind} saved={saved}");
    }

    // 16) v2 DOCX (ZIP magic + .docx name) -> kind "document", .docx kept
    {
        var (status, saved, kind, _) = await UploadV2(http, token, "memo.docx", MakeZip(), "application/vnd.openxmlformats-officedocument.wordprocessingml.document");
        t.Check("v2 DOCX upload -> kind=document .docx", status == 200 && kind == "document" && saved.EndsWith(".docx"), $"status={status} kind={kind} saved={saved}");
    }

    // 17) v2 plain text -> kind "text", .txt
    {
        var (status, saved, kind, _) = await UploadV2(http, token, "notes.txt", "Hello FlipDrop, this is plain text.\n"u8.ToArray(), "text/plain");
        t.Check("v2 text upload -> kind=text .txt", status == 200 && kind == "text" && saved.EndsWith(".txt"), $"status={status} kind={kind} saved={saved}");
    }

    // 18) v2 unknown binary (no magic, no usable extension) -> .bin, kind "other"
    {
        var blob = new byte[] { 0x00, 0x01, 0x02, 0x03, 0xAB, 0xCD, 0xEF, 0x10, 0x00, 0x55 };
        var (status, saved, kind, _) = await UploadV2(http, token, "mystery", blob, "application/octet-stream");
        t.Check("v2 unknown binary -> .bin kind=other", status == 200 && kind == "other" && saved.EndsWith(".bin"), $"status={status} kind={kind} saved={saved}");
    }

    // 19) Sniffed type beats a lying name: PE bytes named holiday.pdf -> .exe, kind "app", risky
    {
        FileReceivedEventArgs? ev = null;
        void OnReceived(object? _, FileReceivedEventArgs e) => ev = e;
        server.FileReceived += OnReceived;
        var (status, saved, kind, _) = await UploadV2(http, token, "holiday.pdf", MakePe(), "application/pdf");
        server.FileReceived -= OnReceived;
        t.Check("Sniffed PE beats .pdf name -> .exe kind=app", status == 200 && kind == "app" && saved.EndsWith(".exe"), $"status={status} kind={kind} saved={saved}");
        t.Check("Executable flagged risky in FileReceived", ev is { IsRisky: true }, ev is null ? "no event" : $"risky={ev.IsRisky}");
    }

    // 20) Hostile X-File-Name is sanitized, file stays in the save folder
    {
        var (status, saved, _, _) = await UploadV2(http, token, "../../evil:name?.pdf", MakePdf(), "application/pdf");
        bool safe = saved == "evilname.pdf";
        bool inFolder = File.Exists(Path.Combine(saveFolder, saved));
        bool noEscape = Directory.GetFiles(saveFolder, "*.pdf", SearchOption.AllDirectories).All(p => Path.GetDirectoryName(p) == saveFolder);
        t.Check("Hostile X-File-Name sanitized to evilname.pdf", status == 200 && safe, $"status={status} saved={saved}");
        t.Check("Sanitized file stays inside the save folder", inFolder && noEscape);
    }

    // 21) Duplicate display names become "name (1).ext"
    {
        var (_, saved1, _, _) = await UploadV2(http, token, "dup.pdf", MakePdf(), "application/pdf");
        var (_, saved2, _, _) = await UploadV2(http, token, "dup.pdf", MakePdf(), "application/pdf");
        t.Check("Duplicate names -> \"dup (1).pdf\"", saved1 == "dup.pdf" && saved2 == "dup (1).pdf", $"first={saved1} second={saved2}");
    }

    // 22) Zone.Identifier (Mark of the Web) stream written to a saved file
    {
        var (status, saved, _, _) = await UploadV2(http, token, "mark.pdf", MakePdf(), "application/pdf");
        string zone = "";
        try { zone = File.ReadAllText(Path.Combine(saveFolder, saved) + ":Zone.Identifier"); } catch { }
        t.Check("Zone.Identifier stream present (ZoneId=3)", status == 200 && zone.Contains("ZoneId=3"), $"zone={zone.Replace("\r", "").Replace("\n", "|")}");
    }

    // 23) Missing Content-Length (chunked) -> 411
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/v2/files");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.TransferEncodingChunked = true;
        req.Headers.TryAddWithoutValidation("X-File-Name", Uri.EscapeDataString("chunked.pdf"));
        req.Content = new StreamContent(new MemoryStream(MakePdf()));
        var r = await http.SendAsync(req);
        t.Check("Missing Content-Length -> 411", (int)r.StatusCode == 411, $"got {(int)r.StatusCode}");
    }

    // 24) Declared length over 4 GiB -> 413 without sending the body (raw request)
    {
        var headers = $"POST /v2/files HTTP/1.1\r\nHost: flipdrop\r\nAuthorization: Bearer {token}\r\n" +
                      "X-File-Name: huge.bin\r\nContent-Type: application/octet-stream\r\n" +
                      $"Content-Length: {FlipDropServer.MaxFileBytes + 1}\r\nConnection: close\r\n\r\n";
        var (status, _) = await SendRaw(fp!, headers, Array.Empty<byte>(), shutdownSend: false);
        t.Check("Declared length > 4 GiB -> 413 (body not sent)", status == 413, $"status={status}");
    }

    // 25) Short body (fewer bytes than Content-Length) -> rejected as incomplete, nothing saved, no leftover .part.
    //     The server returns 400 incomplete; because the body is never fully consumed, Kestrel may reset the
    //     half-closed connection before the 400 is read (status 0) — that is still a rejection, like oversize above.
    {
        var headers = $"POST /v2/files HTTP/1.1\r\nHost: flipdrop\r\nAuthorization: Bearer {token}\r\n" +
                      "X-File-Name: short.bin\r\nContent-Type: application/octet-stream\r\n" +
                      "Content-Length: 100000\r\n\r\n";
        var (status, _) = await SendRaw(fp!, headers, new byte[32], shutdownSend: true);
        await Task.Delay(400); // let the server finish its cleanup
        var incoming = Path.Combine(saveFolder, ".flipdrop-incoming");
        int leftover = Directory.Exists(incoming) ? Directory.GetFiles(incoming, "*.part").Length : 0;
        bool noSaved = !File.Exists(Path.Combine(saveFolder, "short.bin"));
        bool cleanedUp = leftover == 0 && noSaved;
        // A rejection is a clean 400, or a reset (status 0) because the half-closed body is never fully read —
        // but either outcome must leave nothing on disk, so the cleanup check is ANDed into the rejection.
        t.Check("Short body rejected as incomplete (400 or reset)", (status == 400 || status == 0) && cleanedUp,
            $"status={status} leftover={leftover} noSaved={noSaved}");
        t.Check("Short body saved nothing and left no .part file", cleanedUp, $"leftover={leftover} noSaved={noSaved}");
    }

    // 26) Streaming a 300 MB upload does not balloon process memory (server runs in-process)
    {
        const long size = 300L * 1024 * 1024;
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        long before = Process.GetCurrentProcess().PrivateMemorySize64;

        var content = new StreamContent(new GeneratedStream(size));
        content.Headers.ContentLength = size;
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var req = new HttpRequestMessage(HttpMethod.Post, "/v2/files") { Content = content };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.TryAddWithoutValidation("X-File-Name", Uri.EscapeDataString("big.bin"));
        var r = await http.SendAsync(req);
        string saved = "";
        if (r.IsSuccessStatusCode)
        {
            using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
            saved = doc.RootElement.GetProperty("saved").GetString() ?? "";
        }

        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        long after = Process.GetCurrentProcess().PrivateMemorySize64;
        long grewMb = (after - before) / (1024 * 1024);
        bool savedOk = (int)r.StatusCode == 200 && saved.Length > 0
                       && new FileInfo(Path.Combine(saveFolder, saved)).Length == size;
        t.Check("300 MB upload streamed to disk (full size, 200)", savedOk, $"status={(int)r.StatusCode} saved={saved}");
        t.Check("Process memory did not grow by ~300 MB while streaming", grewMb < 150, $"grew {grewMb} MB");
        try { File.Delete(Path.Combine(saveFolder, saved)); } catch { }
    }

    // 27) Three concurrent uploads with the same name all save under distinct names
    {
        var tasks = Enumerable.Range(0, 3)
            .Select(_ => UploadV2(http, token, "concurrent.pdf", MakePdf(), "application/pdf"))
            .ToArray();
        var results = await Task.WhenAll(tasks);
        var names = results.Select(x => x.saved).ToArray();
        bool allOk = results.All(x => x.status == 200);
        bool distinct = names.Distinct().Count() == 3;
        bool allExist = names.All(n => File.Exists(Path.Combine(saveFolder, n)));
        t.Check("3 concurrent uploads all saved with distinct names", allOk && distinct && allExist, $"names=[{string.Join(", ", names)}]");
    }

    // 28) A PE (MZ…) named invoice.pdf, streamed as a 1-byte first piece then the rest (raw TLS with flushes
    //     between pieces), is still sniffed from the full 64-byte head: saved as .exe, kind "app", flagged risky.
    //     Regression guard for sniffing only the first short read.
    {
        FileReceivedEventArgs? ev = null;
        void OnReceived(object? _, FileReceivedEventArgs e) => ev = e;
        server.FileReceived += OnReceived;
        var (status, saved, kind) = await SendInPieces(fp!, "/v2/files", token, "invoice.pdf", MakePe(), firstPiece: 1, "application/pdf", includeLength: true);
        server.FileReceived -= OnReceived;
        t.Check("PE streamed in a 1-byte first piece -> .exe kind=app", status == 200 && kind == "app" && saved.EndsWith(".exe"), $"status={status} kind={kind} saved={saved}");
        t.Check("Streamed PE saved file exists and flagged risky", File.Exists(Path.Combine(saveFolder, saved)) && ev is { IsRisky: true }, ev is null ? "no event" : $"risky={ev.IsRisky}");
    }

    // 29) v1 JPEG whose first read is a single byte must still be accepted (200), not wrongly 415'd because the
    //     magic bytes had not all arrived on the first chunk. Head is accumulated across reads.
    {
        var (status, saved, _) = await SendInPieces(fp!, "/v1/photos", token, "snap.jpg", MakeJpeg(), firstPiece: 1, "image/jpeg", includeLength: true);
        t.Check("v1 JPEG with a 1-byte first chunk -> 200", status == 200 && saved.EndsWith(".jpg"), $"status={status} saved={saved}");
    }

    // 30) A bidi/RTLO override (U+202E) in X-File-Name is stripped: "Photo<RLO>gpj.exe" with MZ bytes is saved
    //     as a plain .exe with no U+202E in the name (so the extension can't be visually spoofed).
    {
        var (status, saved, kind, _) = await UploadV2(http, token, "Photo‮gpj.exe", MakePe(), "application/octet-stream");
        bool noRlo = !saved.Contains('‮');
        t.Check("U+202E stripped from saved name", status == 200 && noRlo && saved == "Photogpj.exe" && kind == "app", $"status={status} saved={saved} kind={kind} noRlo={noRlo}");
    }

    // 31) Pairing back-off: 5 wrong codes -> 429 + retryAfter; correct code refused while locked
    {
        TimeSpan? lockedFor = null;
        void OnLocked(object? _, PairingLockedEventArgs e) => lockedFor = e.LockedFor;
        pairing.LockedOut += OnLocked;

        int lastStatus = 0, retryAfter = -1;
        for (int i = 0; i < 5; i++)
        {
            var r = await http.PostAsJsonAsync("/v1/pair", new { code = "999999", deviceName = "bf", deviceId = "dev-bf" });
            lastStatus = (int)r.StatusCode;
            if (lastStatus == 429)
            {
                using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
                doc.RootElement.TryGetProperty("retryAfter", out var ra);
                retryAfter = ra.ValueKind == JsonValueKind.Number ? ra.GetInt32() : -1;
            }
        }
        var r2 = await http.PostAsJsonAsync("/v1/pair", new { code = TestCode, deviceName = "bf", deviceId = "dev-bf2" });
        pairing.LockedOut -= OnLocked;

        t.Check("5 wrong codes -> 429 with retryAfter", lastStatus == 429 && retryAfter > 0, $"status={lastStatus} retryAfter={retryAfter}");
        t.Check("LockedOut event fired", lockedFor is { TotalSeconds: > 0 }, $"lockedFor={lockedFor}");
        t.Check("Correct code refused while locked -> 429", (int)r2.StatusCode == 429, $"got {(int)r2.StatusCode}");
    }

    // FileTypes unit sanity (format + risky classification)
    {
        bool ok = FileTypes.FormatSize(512L * 1024) == "512 KB"
                  && FileTypes.FormatSize(1300L * 1024 * 1024).EndsWith(" GB")
                  && FileTypes.IsRiskyExtension("exe")
                  && FileTypes.IsRiskyExtension("ps1")
                  && !FileTypes.IsRiskyExtension("pdf")
                  && FileTypes.KindOfExtension("DOCX") == FileKind.Document;
        t.Check("FileTypes format/risky/kind helpers behave", ok, $"512KiB={FileTypes.FormatSize(512L * 1024)}");
    }
}
finally
{
    try { discovery.Dispose(); } catch { }
    try { await server.DisposeAsync(); } catch { }
    try { Directory.Delete(dataDir, true); } catch { }
}

Console.WriteLine();
Console.WriteLine(t.Summary());
return t.Failed == 0 ? 0 : 1;

static string Short(string? s) => string.IsNullOrEmpty(s) ? "" : (s.Length <= 12 ? s : s[..12] + "…");

static async Task<string> Discover()
{
    using var udp = new UdpClient();
    udp.Client.ReceiveTimeout = 3000;
    var probe = Encoding.ASCII.GetBytes("FLIPDROP_DISCOVER_V1");
    await udp.SendAsync(probe, probe.Length, new IPEndPoint(IPAddress.Loopback, DiscoveryService.Port));
    var recv = udp.ReceiveAsync();
    var done = await Task.WhenAny(recv, Task.Delay(3000));
    if (done != recv) throw new TimeoutException("no discovery reply");
    return Encoding.UTF8.GetString(recv.Result.Buffer);
}

// Posts a file to /v2/files and parses the JSON result.
static async Task<(int status, string saved, string kind, long size)> UploadV2(
    HttpClient http, string token, string fileName, byte[] bytes, string contentType)
{
    using var req = new HttpRequestMessage(HttpMethod.Post, "/v2/files");
    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    req.Headers.TryAddWithoutValidation("X-File-Name", Uri.EscapeDataString(fileName));
    req.Content = new ByteArrayContent(bytes);
    req.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
    var r = await http.SendAsync(req);
    var body = await r.Content.ReadAsStringAsync();
    string saved = "", kind = "";
    long size = 0;
    if (r.IsSuccessStatusCode)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        saved = root.GetProperty("saved").GetString() ?? "";
        kind = root.GetProperty("kind").GetString() ?? "";
        size = root.GetProperty("size").GetInt64();
    }
    return ((int)r.StatusCode, saved, kind, size);
}

// Posts a file over a fingerprint-pinned raw TLS socket, writing the body as a small first piece and then
// the rest, flushing (with a short pause) between them so the server performs more than one body read.
// This exercises the head-accumulation path (type must be resolved from the whole 64-byte head, not the
// first short read). Returns the HTTP status and, for a 200, the parsed "saved"/"kind".
static async Task<(int status, string saved, string kind)> SendInPieces(
    string fp, string path, string token, string fileName, byte[] body, int firstPiece, string contentType, bool includeLength)
{
    using var tcp = new TcpClient();
    await tcp.ConnectAsync(IPAddress.Loopback, FlipDropServer.HttpsPort);
    await using var ns = tcp.GetStream();
    using var ssl = new SslStream(ns, false,
        (_, c, _, _) => c is not null && Convert.ToHexStringLower(SHA256.HashData(c.GetRawCertData())) == fp);
    await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "FlipDrop" });

    var hb = new StringBuilder();
    hb.Append($"POST {path} HTTP/1.1\r\nHost: flipdrop\r\nAuthorization: Bearer {token}\r\n");
    hb.Append($"X-File-Name: {Uri.EscapeDataString(fileName)}\r\n");
    hb.Append($"Content-Type: {contentType}\r\n");
    if (includeLength) hb.Append($"Content-Length: {body.Length}\r\n");
    hb.Append("Connection: close\r\n\r\n");
    await ssl.WriteAsync(Encoding.ASCII.GetBytes(hb.ToString()));
    await ssl.FlushAsync();

    int sent = 0;
    firstPiece = Math.Clamp(firstPiece, 1, Math.Max(1, body.Length));
    while (sent < body.Length)
    {
        int take = sent == 0 ? Math.Min(firstPiece, body.Length) : body.Length - sent;
        await ssl.WriteAsync(body.AsMemory(sent, take));
        await ssl.FlushAsync();
        sent += take;
        if (sent < body.Length) await Task.Delay(60); // encourage a separate server-side read
    }

    var resp = new StringBuilder();
    var rbuf = new byte[4096];
    using var rcts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    try
    {
        int n;
        while ((n = await ssl.ReadAsync(rbuf, rcts.Token)) > 0) // server closes (Connection: close) -> read to EOF
            resp.Append(Encoding.ASCII.GetString(rbuf, 0, n));
    }
    catch { /* reset after the status line is still a usable response */ }

    var raw = resp.ToString();
    var m = Regex.Match(raw, @"^HTTP/\d\.\d (\d{3})");
    int status = m.Success ? int.Parse(m.Groups[1].Value) : 0;
    string saved = "", kind = "";
    int sep = raw.IndexOf("\r\n\r\n", StringComparison.Ordinal);
    if (status == 200 && sep >= 0)
    {
        // The body may be chunk-framed (Transfer-Encoding: chunked), so isolate the JSON object itself
        // rather than parsing the raw body (which would include the chunk-size lines).
        var bodyText = raw[(sep + 4)..];
        int lb = bodyText.IndexOf('{'), rb = bodyText.LastIndexOf('}');
        if (lb >= 0 && rb > lb)
        {
            try
            {
                using var doc = JsonDocument.Parse(bodyText[lb..(rb + 1)]);
                var root = doc.RootElement;
                if (root.TryGetProperty("saved", out var s)) saved = s.GetString() ?? "";
                if (root.TryGetProperty("kind", out var k)) kind = k.GetString() ?? "";
            }
            catch { /* leave saved/kind empty if the body is not the expected JSON */ }
        }
    }
    return (status, saved, kind);
}

// Sends a hand-built HTTP request over a fingerprint-pinned TLS socket and returns the status code.
static async Task<(int status, string raw)> SendRaw(string fp, string headers, byte[] body, bool shutdownSend)
{
    using var tcp = new TcpClient();
    await tcp.ConnectAsync(IPAddress.Loopback, FlipDropServer.HttpsPort);
    await using var ns = tcp.GetStream();
    using var ssl = new SslStream(ns, false,
        (_, c, _, _) => c is not null && Convert.ToHexStringLower(SHA256.HashData(c.GetRawCertData())) == fp);
    await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "FlipDrop" });

    await ssl.WriteAsync(Encoding.ASCII.GetBytes(headers));
    if (body.Length > 0) await ssl.WriteAsync(body);
    await ssl.FlushAsync();
    if (shutdownSend)
    {
        await Task.Delay(300);                       // let the server park on the body read
        tcp.Client.Shutdown(SocketShutdown.Send);    // half-close so it sees an early end-of-body
    }

    var sb = new StringBuilder();
    var buf = new byte[4096];
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
    try
    {
        int n;
        while ((n = await ssl.ReadAsync(buf, cts.Token)) > 0)
        {
            sb.Append(Encoding.ASCII.GetString(buf, 0, n));
            if (sb.ToString().Contains("\r\n\r\n")) break;
        }
    }
    catch { /* the server may reset the connection right after the status line */ }

    var raw = sb.ToString();
    var m = Regex.Match(raw, @"^HTTP/\d\.\d (\d{3})");
    return (m.Success ? int.Parse(m.Groups[1].Value) : 0, raw);
}

// --- tiny file builders (just enough magic bytes to drive the sniffer) ---

static byte[] MakeJpeg()
{
    var b = new byte[64];
    b[0] = 0xFF; b[1] = 0xD8; b[2] = 0xFF; b[3] = 0xE0;
    b[^2] = 0xFF; b[^1] = 0xD9;
    return b;
}

static byte[] MakePdf()
{
    var header = "%PDF-1.7\n%\u00E2\u00E3\u00CF\u00D3\n1 0 obj<<>>endobj\n"u8.ToArray();
    return header;
}

static byte[] MakeMp4()
{
    // ftyp box: size(24) + 'ftyp' + major brand 'isom' + minor + compatible brands
    var b = new byte[]
    {
        0x00, 0x00, 0x00, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p',
        (byte)'i', (byte)'s', (byte)'o', (byte)'m', 0x00, 0x00, 0x02, 0x00,
        (byte)'i', (byte)'s', (byte)'o', (byte)'m', (byte)'m', (byte)'p', (byte)'4', (byte)'1'
    };
    return b;
}

static byte[] MakeZip()
{
    // Minimal local-file-header signature PK\x03\x04 followed by filler.
    var b = new byte[64];
    b[0] = 0x50; b[1] = 0x4B; b[2] = 0x03; b[3] = 0x04;
    for (int i = 4; i < b.Length; i++) b[i] = (byte)(i * 7);
    return b;
}

static byte[] MakePe()
{
    // 'MZ' DOS header -> Windows executable.
    var b = new byte[128];
    b[0] = (byte)'M'; b[1] = (byte)'Z';
    for (int i = 2; i < b.Length; i++) b[i] = (byte)(i & 0xFF);
    return b;
}

sealed class TestRun
{
    public int Passed { get; private set; }
    public int Failed { get; private set; }

    public void Check(string name, bool ok, string? detail = null)
    {
        if (ok) Passed++; else Failed++;
        var tag = ok ? "PASS" : "FAIL";
        Console.WriteLine($"[{tag}] {name}" + (detail is null ? "" : $"  ({detail})"));
    }

    public string Summary() => $"==== {Passed} passed, {Failed} failed ====";
}

// Read-only stream that yields `length` bytes on the fly (no buffering), so a large upload
// can be sent without holding it in memory. First byte is non-text to keep sniffing as "other".
sealed class GeneratedStream : Stream
{
    private long _remaining;
    private readonly long _length;

    public GeneratedStream(long length) { _length = length; _remaining = length; }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _length;
    public override long Position { get => _length - _remaining; set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_remaining <= 0) return 0;
        int n = (int)Math.Min(count, _remaining);
        // Fill with a cheap pattern; byte 0 stays 0x00 so the sniffer sees non-text binary.
        for (int i = 0; i < n; i++) buffer[offset + i] = (byte)((Position + i) & 0xFF);
        _remaining -= n;
        return n;
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
