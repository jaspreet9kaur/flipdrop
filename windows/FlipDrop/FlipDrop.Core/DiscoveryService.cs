using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace FlipDrop.Core;

/// <summary>
/// UDP discovery responder. Listens on 47802 for the ASCII probe
/// "FLIPDROP_DISCOVER_V1" and replies unicast with the receiver descriptor.
/// Non-matching payloads and non-private senders are ignored silently.
/// </summary>
public sealed class DiscoveryService : IDisposable
{
    public const int Port = 47802;
    private const string Probe = "FLIPDROP_DISCOVER_V1";

    private readonly CertificateManager _cert;
    private readonly int _httpsPort;
    private readonly CancellationTokenSource _cts = new();
    private UdpClient? _udp;

    public DiscoveryService(CertificateManager cert, int httpsPort = FlipDropServer.HttpsPort)
    {
        _cert = cert;
        _httpsPort = httpsPort;
    }

    public void Start()
    {
        _udp = new UdpClient(AddressFamily.InterNetwork);
        _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _udp.Client.Bind(new IPEndPoint(IPAddress.Any, Port));
        _ = Task.Run(() => ReceiveLoopAsync(_cts.Token));
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var udp = _udp!;
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult r;
            try
            {
                r = await udp.ReceiveAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch { continue; }

            try
            {
                if (!PrivateIp.IsAllowed(r.RemoteEndPoint.Address)) continue;

                var text = Encoding.ASCII.GetString(r.Buffer).Trim('\0', ' ', '\r', '\n', '\t');
                if (!string.Equals(text, Probe, StringComparison.Ordinal)) continue;

                var reply = JsonSerializer.SerializeToUtf8Bytes(new DiscoveryReply
                {
                    app = "flipdrop",
                    v = 1,
                    name = Environment.MachineName,
                    port = _httpsPort,
                    fp = _cert.FingerprintHex
                });
                await udp.SendAsync(reply, reply.Length, r.RemoteEndPoint);
            }
            catch { /* ignore a single bad packet */ }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _udp?.Dispose();
        _cts.Dispose();
    }

    private sealed class DiscoveryReply
    {
        public string app { get; set; } = "";
        public int v { get; set; }
        public string name { get; set; } = "";
        public int port { get; set; }
        public string fp { get; set; } = "";
    }
}
