using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace FlipDrop.Core;

/// <summary>
/// Owns the self-signed ECDSA P-256 server certificate. Generated once on first
/// run (CertificateRequest -> self-signed), exported to PFX and stored
/// DPAPI-protected (CurrentUser) under the data folder, then reused.
/// Exposes the SHA-256 DER fingerprint the phone pins (trust on first use).
/// </summary>
public sealed class CertificateManager
{
    private readonly string _pfxPath;

    public X509Certificate2 Certificate { get; }

    /// <summary>SHA-256 of the certificate DER encoding, lowercase hex (the "fp" in discovery).</summary>
    public string FingerprintHex { get; }

    public CertificateManager(string dataDir)
    {
        _pfxPath = Path.Combine(dataDir, "flipdrop.pfx");
        Certificate = LoadOrCreate();
        FingerprintHex = Convert.ToHexStringLower(SHA256.HashData(Certificate.RawData));
    }

    // Windows Schannel (used by Kestrel for server-side TLS) cannot use an ephemeral,
    // in-memory private key — it needs a key-container-backed key. UserKeySet gives a
    // container that is tied to this X509Certificate2's lifetime and removed when the
    // certificate is disposed, so nothing durable is left in the user profile.
    private const X509KeyStorageFlags KeyFlags = X509KeyStorageFlags.UserKeySet;

    private X509Certificate2 LoadOrCreate()
    {
        if (File.Exists(_pfxPath))
        {
            try
            {
                var pfx = Dpapi.Unprotect(File.ReadAllBytes(_pfxPath));
                var loaded = X509CertificateLoader.LoadPkcs12(pfx, null, KeyFlags);
                CryptographicOperations.ZeroMemory(pfx);
                return loaded;
            }
            catch
            {
                // Corrupt / unreadable (e.g. restored on a different user) — regenerate.
            }
        }

        using var fresh = Create();
        var bytes = fresh.Export(X509ContentType.Pfx);
        File.WriteAllBytes(_pfxPath, Dpapi.Protect(bytes));
        // Re-import so the private key is backed by a key container Schannel accepts.
        var cert = X509CertificateLoader.LoadPkcs12(bytes, null, KeyFlags);
        CryptographicOperations.ZeroMemory(bytes);
        return cert;
    }

    private static X509Certificate2 Create()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest("CN=FlipDrop", ecdsa, HashAlgorithmName.SHA256);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        req.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyAgreement, true));
        req.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false)); // serverAuth

        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("FlipDrop");
        try { san.AddDnsName(Environment.MachineName); } catch { /* unusual machine names */ }
        san.AddIpAddress(IPAddress.Loopback);
        req.CertificateExtensions.Add(san.Build());

        var notBefore = DateTimeOffset.UtcNow.AddMinutes(-5);
        return req.CreateSelfSigned(notBefore, notBefore.AddYears(10));
    }
}
