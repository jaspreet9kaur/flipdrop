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

    // Pairing back-off (protocol 5.4): first lockout 30 s, doubling to a 15 min cap, reset on success.
    private static readonly TimeSpan BaseLockout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaxLockout = TimeSpan.FromMinutes(15);

    private readonly DeviceStore _devices;
    private readonly Lock _lock = new();
    private readonly bool _fixedCode;

    private string _code = "";
    private DateTimeOffset _issuedAt;
    private int _wrongAttempts;
    private int _lockoutCount;
    private DateTimeOffset? _lockedUntil;

    public event Action? CodeChanged;

    /// <summary>Raised when pairing becomes locked out after too many wrong codes.</summary>
    public event EventHandler<PairingLockedEventArgs>? LockedOut;

    /// <summary>When pairing is currently locked, the time it unlocks; otherwise null.</summary>
    public DateTimeOffset? LockedUntil
    {
        get
        {
            lock (_lock)
            {
                if (_lockedUntil is DateTimeOffset until && DateTimeOffset.UtcNow < until) return until;
                return null;
            }
        }
    }

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
        TimeSpan? justLockedFor = null;
        PairResult result;
        lock (_lock)
        {
            // While locked, refuse everything (even a correct code) without counting attempts.
            if (_lockedUntil is DateTimeOffset until)
            {
                if (DateTimeOffset.UtcNow < until) return PairResult.Locked;
                _lockedUntil = null; // lock expired
                _wrongAttempts = 0;
            }

            MaybeRotate();

            var expected = Encoding.UTF8.GetBytes(_code);
            var given = Encoding.UTF8.GetBytes(code ?? "");
            bool match = expected.Length == given.Length && CryptographicOperations.FixedTimeEquals(expected, given);

            if (!match)
            {
                _wrongAttempts++;
                if (_wrongAttempts >= MaxWrongAttempts)
                {
                    var lockFor = NextLockout();
                    _lockoutCount++;
                    _lockedUntil = DateTimeOffset.UtcNow + lockFor;
                    _wrongAttempts = 0;
                    RotateLocked(); // burn the code so a brute-force run can't continue
                    justLockedFor = lockFor;
                    result = PairResult.Locked;
                }
                else
                {
                    result = PairResult.BadCode;
                }
            }
            else
            {
                var tok = GenerateToken();
                var hash = SHA256.HashData(Encoding.UTF8.GetBytes(tok));
                _devices.Add(deviceId, string.IsNullOrWhiteSpace(deviceName) ? "Unknown device" : deviceName, hash);
                token = tok;
                // Successful pairing resets the back-off and rotates the code.
                _lockoutCount = 0;
                _lockedUntil = null;
                RotateLocked();
                result = PairResult.Ok;
            }
        }

        // Fire outside the lock so a handler can't deadlock us.
        if (justLockedFor is TimeSpan d)
            LockedOut?.Invoke(this, new PairingLockedEventArgs { LockedFor = d });

        return result;
    }

    /// <summary>Back-off duration for the next lockout: 30 s doubling each time, capped at 15 min.</summary>
    private TimeSpan NextLockout()
    {
        var secs = BaseLockout.TotalSeconds * Math.Pow(2, _lockoutCount);
        if (secs > MaxLockout.TotalSeconds) secs = MaxLockout.TotalSeconds;
        return TimeSpan.FromSeconds(secs);
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
