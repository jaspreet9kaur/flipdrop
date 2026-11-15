using System.Security.Cryptography;
using System.Text;

namespace FlipDrop.Core;

/// <summary>
/// Owns the rotating 6-digit pairing code and issues tokens.
/// Rotation triggers: every 120 s, after any successful pairing, after 5 wrong attempts.
/// Codes are compared in constant time.
///
/// Test hook: when the environment variable FLIPDROP_TEST_CODE holds a 6-digit value the
/// code is fixed to it and rotation is disabled, so automated tests have a stable code.
/// Debug builds only, and off unless that variable is explicitly set.
/// </summary>
public sealed class PairingService
{
    public enum PairResult { Ok, BadCode, Locked }

    public static readonly TimeSpan RotationPeriod = TimeSpan.FromSeconds(120);
    private const int MaxWrongAttempts = 5;

    private readonly DeviceStore _devices;
    private readonly Lock _lock = new();
    private readonly bool _fixedCode;

    private string _code = "";
    private DateTimeOffset _issuedAt;
    private int _wrongAttempts;

    public event Action? CodeChanged;

    public PairingService(DeviceStore devices)
    {
        _devices = devices;

#if DEBUG
        var test = Environment.GetEnvironmentVariable("FLIPDROP_TEST_CODE");
#else
        string? test = null; // the fixed-code test hook exists in Debug builds only
#endif
        if (!string.IsNullOrEmpty(test) && test.Length == 6 && test.All(char.IsDigit))
        {
            _fixedCode = true;
            _code = test;
            _issuedAt = DateTimeOffset.Now;
        }
        else
        {
            RotateLocked();
        }
    }

    public string CurrentCode
    {
        get { lock (_lock) { MaybeRotate(); return _code; } }
    }

    public DateTimeOffset IssuedAt
    {
        get { lock (_lock) { MaybeRotate(); return _issuedAt; } }
    }

    public TimeSpan TimeRemaining
    {
        get
        {
            lock (_lock)
            {
                if (_fixedCode) return RotationPeriod;
                MaybeRotate();
                var remaining = RotationPeriod - (DateTimeOffset.Now - _issuedAt);
                return remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining;
            }
        }
    }

    public void ForceRotate()
    {
        lock (_lock) RotateLocked();
    }

    public PairResult TryPair(string? code, string deviceId, string deviceName, out string? token)
    {
        token = null;
        lock (_lock)
        {
            MaybeRotate();

            var expected = Encoding.UTF8.GetBytes(_code);
            var given = Encoding.UTF8.GetBytes(code ?? "");
            bool match = expected.Length == given.Length && CryptographicOperations.FixedTimeEquals(expected, given);

            if (!match)
            {
                _wrongAttempts++;
                if (_wrongAttempts >= MaxWrongAttempts)
                {
                    RotateLocked(); // burn the code so a brute-force run can't continue
                    return PairResult.Locked;
                }
                return PairResult.BadCode;
            }

            var tok = GenerateToken();
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(tok));
            _devices.Add(deviceId, string.IsNullOrWhiteSpace(deviceName) ? "Unknown device" : deviceName, hash);
            token = tok;
            RotateLocked(); // fresh code after a successful pairing
            return PairResult.Ok;
        }
    }

    private void MaybeRotate()
    {
        if (_fixedCode) return;
        if (DateTimeOffset.Now - _issuedAt >= RotationPeriod) RotateLocked();
    }

    private void RotateLocked()
    {
        _wrongAttempts = 0;
        if (_fixedCode) return;
        _code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        _issuedAt = DateTimeOffset.Now;
        CodeChanged?.Invoke();
    }

    private static string GenerateToken()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
