using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AdaPanel.Core.Dtos.Licensing;

namespace AdaPanel.Infrastructure.Licensing;

/// <summary>
/// Lisans sunucusu yanitinin IMZA/yas dogrulamasi (altyapi). Genel anahtar yapilandirilmamissa
/// dogrulama YAPILMAZ: lisans sunucusu bugun yaniti imzalamiyor ve bu istemcide degistirilemez.
/// Anahtar uydurulmaz; sunucu imzalamaya basladiginda yalniz yapilandirma degisir.
/// </summary>
public static class LicenseResponseValidator
{
    /// <summary>Imzalanan kanonik metin. Sunucu ayni sirayla ve ayni bicimle imzalamalidir.</summary>
    public static string CanonicalText(LicenseVerificationRequest request, LicenseVerificationResponse response) =>
        string.Join('\n',
            request.Product,
            request.Fingerprint,
            (response.Status ?? string.Empty).Trim().ToLowerInvariant(),
            response.Sku ?? string.Empty,
            response.MaxSubscriptions.ToString(CultureInfo.InvariantCulture),
            response.ExpiresAtUtc?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) ?? string.Empty,
            response.IssuedAtUtc?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) ?? string.Empty);

    /// <summary>Kusur varsa aciklamasi, yoksa (ya da dogrulama kapaliysa) <c>null</c>.</summary>
    public static string? Validate(
        LicenseVerificationResponse response,
        LicenseVerificationRequest request,
        string? publicKeyBase64,
        DateTime nowUtc,
        TimeSpan maxAge)
    {
        if (string.IsNullOrWhiteSpace(publicKeyBase64))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(response.Signature) || response.IssuedAtUtc is not { } issued)
        {
            return "signature_missing";
        }

        var age = nowUtc - issued.ToUniversalTime();
        if (age > maxAge || age < TimeSpan.FromMinutes(-10))
        {
            return "response_stale";
        }

        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeyBase64.Trim()), out _);
            var ok = ecdsa.VerifyData(
                Encoding.UTF8.GetBytes(CanonicalText(request, response)),
                Convert.FromBase64String(response.Signature.Trim()),
                HashAlgorithmName.SHA256);
            return ok ? null : "signature_invalid";
        }
        catch (Exception exception) when (exception is FormatException or CryptographicException)
        {
            return "signature_invalid";
        }
    }
}
