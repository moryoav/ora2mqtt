using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace libgwmapi;

/// <summary>
/// Client-certificate enrollment for the mutual-TLS app-gateway. GWM started requiring a
/// per-install enrolled certificate (appAuth/applyCertificate) in 2026-08; the shared bootstrap
/// certificate no longer passes there. Generate a CSR, send it, and use the signed certificate
/// (with the locally kept private key) for the app-gateway.
/// </summary>
public static class CertificateEnrollment
{
    // Csr + private key (base64), plus the deviceId/iccid header value the applyCertificate
    // call must send (both derived from the same instant, matching the CSR common name).
    public sealed record Generated(string Csr, string PrivateKey, string EnrollmentDeviceId);

    public static Generated GenerateCsr(string country, string deviceId)
    {
        var now = DateTimeOffset.UtcNow;
        var normalizedCountry = (country ?? "").Trim().ToUpperInvariant();
        var normalizedDeviceId = NormalizeDeviceId(deviceId);
        // mirrors the app: CN = LGWMy GWM-AD-<country><deviceId(upper)><unixSeconds>
        var commonName = $"LGWMy GWM-AD-{normalizedCountry}{normalizedDeviceId.ToUpperInvariant()}{now.ToUnixTimeSeconds()}";
        // the applyCertificate deviceId/iccid header uses milliseconds
        var enrollmentDeviceId = normalizedDeviceId + now.ToUnixTimeMilliseconds();

        using var rsa = RSA.Create(2048);
        var subject = new X500DistinguishedNameBuilder();
        subject.AddCommonName(commonName);
        var request = new CertificateRequest(subject.Build(), rsa,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        return new Generated(
            Convert.ToBase64String(request.CreateSigningRequest()),
            Convert.ToBase64String(rsa.ExportPkcs8PrivateKey()),
            enrollmentDeviceId);
    }

    private static string NormalizeDeviceId(string deviceId)
    {
        var normalized = (deviceId ?? "").Replace("-", "");
        return normalized.Length >= 32 ? normalized[..32] : normalized.PadRight(32, '0');
    }

    /// <summary>
    /// Expiry of an enrolled certificate, so it can be renewed before the app-gateway starts
    /// rejecting it. Returns null when the stored certificate cannot be read.
    /// </summary>
    public static DateTime? NotAfterUtc(string encodedCertificate)
    {
        if (String.IsNullOrEmpty(encodedCertificate)) return null;
        try
        {
            using var certificate = new X509Certificate2(Convert.FromBase64String(encodedCertificate));
            return certificate.NotAfter.ToUniversalTime();
        }
        catch (Exception e) when (e is FormatException or CryptographicException)
        {
            return null;
        }
    }

    public static X509Certificate2 Load(string encodedCertificate, string encodedPrivateKey)
    {
        var certificate = new X509Certificate2(Convert.FromBase64String(encodedCertificate));
        using var rsa = RSA.Create();
        rsa.ImportPkcs8PrivateKey(Convert.FromBase64String(encodedPrivateKey), out _);
        var withKey = certificate.CopyWithPrivateKey(rsa);
        // export/re-import so the private key is usable for TLS on all platforms
        return new X509Certificate2(withKey.Export(X509ContentType.Pkcs12));
    }
}
