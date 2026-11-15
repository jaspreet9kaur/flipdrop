using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FlipDrop.Core;

public sealed record PairedDevice(string DeviceId, string DeviceName, string TokenHashB64, DateTimeOffset PairedAt);

/// <summary>
/// Persists paired devices. Only SHA-256(token) is stored, never the token itself.
/// The whole file is DPAPI-protected (CurrentUser) at rest.
/// </summary>
public sealed class DeviceStore
{
    private readonly string _path;
    private readonly Lock _lock = new();
    private List<PairedDevice> _devices = [];

    public event Action? Changed;

    public DeviceStore(string path)
    {
        _path = path;
        Load();
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var plain = Dpapi.Unprotect(File.ReadAllBytes(_path));
                _devices = JsonSerializer.Deserialize<List<PairedDevice>>(plain) ?? [];
            }
        }
        catch
        {
            _devices = [];
        }
    }

    private void Save()
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(_devices);
        File.WriteAllBytes(_path, Dpapi.Protect(json));
    }

    public IReadOnlyList<PairedDevice> Devices
    {
        get { lock (_lock) return _devices.ToList(); }
    }

    public void Add(string deviceId, string deviceName, byte[] tokenHash)
    {
        lock (_lock)
        {
            _devices.RemoveAll(d => d.DeviceId == deviceId);
            _devices.Add(new PairedDevice(deviceId, deviceName, Convert.ToBase64String(tokenHash), DateTimeOffset.Now));
            Save();
        }
        Changed?.Invoke();
    }

    public void Revoke(string deviceId)
    {
        lock (_lock)
        {
            _devices.RemoveAll(d => d.DeviceId == deviceId);
            Save();
        }
        Changed?.Invoke();
    }

    /// <summary>Constant-time check of a presented bearer token against every stored hash.</summary>
    public bool TryValidateToken(string token, out PairedDevice? device)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        lock (_lock)
        {
            foreach (var d in _devices)
            {
                byte[] stored;
                try { stored = Convert.FromBase64String(d.TokenHashB64); }
                catch { continue; }
                if (stored.Length == hash.Length && CryptographicOperations.FixedTimeEquals(stored, hash))
                {
                    device = d;
                    return true;
                }
            }
        }
        device = null;
        return false;
    }
}
