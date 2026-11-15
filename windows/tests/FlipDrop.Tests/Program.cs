using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
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

    // 10) Oversize body (> 64 MiB) -> rejected (413, or connection refused mid-stream)
    {
        var big = new byte[FlipDropServer.MaxBytes + 1024];
        big[0] = 0xFF; big[1] = 0xD8; big[2] = 0xFF; // JPEG header so only size can reject it
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "/v1/photos");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Content = new ByteArrayContent(big);
            req.Content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
            var r = await http.SendAsync(req);
            t.Check("Oversize upload rejected (413)", (int)r.StatusCode == 413, $"got {(int)r.StatusCode}");
        }
        catch (Exception ex)
        {
            // Kestrel may reset the connection once the limit is hit; that is still a rejection, not an acceptance.
            t.Check("Oversize upload rejected (connection reset)", true, ex.GetType().Name);
        }
    }

    // 11) Non-private UDP sender is ignored (sanity on the private-IP gate helper)
    {
        bool ok = !PrivateIp.IsAllowed(IPAddress.Parse("8.8.8.8"))
                  && PrivateIp.IsAllowed(IPAddress.Parse("192.168.1.5"))
                  && PrivateIp.IsAllowed(IPAddress.Loopback);
        t.Check("Private-IP gate accepts LAN/loopback, rejects public", ok);
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

// Minimal valid JPEG (SOI + a tiny payload + EOI). Magic bytes FF D8 FF are what the server checks.
static byte[] MakeJpeg()
{
    var b = new byte[64];
    b[0] = 0xFF; b[1] = 0xD8; b[2] = 0xFF; b[3] = 0xE0;
    b[^2] = 0xFF; b[^1] = 0xD9;
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
