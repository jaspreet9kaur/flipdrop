using System.Net;
using System.Net.Sockets;

namespace FlipDrop.Core;

/// <summary>
/// Private-range gate. Connections (TCP) and discovery packets (UDP) are only
/// honoured from RFC1918 / link-local addresses, plus loopback for local tests.
/// </summary>
public static class PrivateIp
{
    public static bool IsAllowed(IPAddress? ip)
    {
        if (ip is null) return false;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();

        if (ip.AddressFamily != AddressFamily.InterNetwork)
            return IPAddress.IsLoopback(ip); // ::1 allowed for local tests; all other IPv6 rejected

        Span<byte> b = ip.GetAddressBytes();
        if (b[0] == 127) return true;                                 // 127/8   loopback (local tests)
        if (b[0] == 10) return true;                                  // 10/8
        if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;     // 172.16/12
        if (b[0] == 192 && b[1] == 168) return true;                  // 192.168/16
        if (b[0] == 169 && b[1] == 254) return true;                  // 169.254/16 link-local
        return false;
    }
}
