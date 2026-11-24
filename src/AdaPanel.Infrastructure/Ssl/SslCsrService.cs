using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Ssl;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Support;

namespace AdaPanel.Infrastructure.Ssl;

/// <summary>
/// CSR uretimi ve CA imzali sertifikanin ice aktarimi.
/// <para>
/// OZEL ANAHTAR sunucudan CIKMAZ: PKCS#8 olarak DataProtection (<see cref="ISecretProtector"/>) ile
/// korunup <c>config/ssl-csr.json</c>'da bekler; hicbir DTO/uc/denetim kaydi onu tasimaz. Imzali
/// sertifika geldiginde genel anahtar ESLESMEZSE reddedilir; eslesirse anahtarla birlestirilip
/// mevcut ice aktarma yolundan gecer ve korunan anahtar kayittan SILINIR (artik sertifika deposundadir).
/// </para>
/// </summary>
public sealed partial class SslCsrService(
    JsonFileStore fileStore,
    ISecretProtector secretProtector,
    ISslService sslService) : ISslCsrService
{
    internal const string StatePath = "config/ssl-csr.json";
    private const int MaxPending = 50;
    private static readonly int[] AllowedKeySizes = [2048, 3072, 4096];

    public async Task<IReadOnlyCollection<SslCsrDto>> ListAsync(CancellationToken cancellationToken = default)
    {
        var all = await fileStore.ReadAsync(StatePath, Array.Empty<CsrRecord>(), cancellationToken);
        return all.OrderByDescending(item => item.CreatedAtUtc).Select(item => ToDto(item, includeCsr: false)).ToArray();
    }

    public async Task<SslCsrDto> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        var all = await fileStore.ReadAsync(StatePath, Array.Empty<CsrRecord>(), cancellationToken);
        var record = all.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.Ordinal))
            ?? throw new AppException("ssl.csr_not_found", 404, 404);
        return ToDto(record, includeCsr: true);
    }

    public async Task<SslCsrDto> CreateAsync(CreateSslCsrRequest request, CancellationToken cancellationToken = default)
    {
        var host = NormalizeHost(request.HostName);
        var alt = (request.AltNames ?? [])
            .Where(name => !string.IsNullOrWhiteSpace(name)).Select(NormalizeHost)
            .Where(name => !string.Equals(name, host, StringComparison.Ordinal)).Distinct(StringComparer.Ordinal).ToArray();
        if (alt.Length > 20) throw new AppException("ssl.csr_too_many_names", 400, 400);
        var keySize = request.KeySize == 0 ? 2048 : request.KeySize;
        if (!AllowedKeySizes.Contains(keySize)) throw new AppException("ssl.csr_key_size_invalid", 400, 400);
        var country = (request.Country ?? string.Empty).Trim().ToUpperInvariant();
        if (country.Length != 0 && !CountryPattern().IsMatch(country)) throw new AppException("ssl.csr_country_invalid", 400, 400);

        // X500DistinguishedNameBuilder KACIRIR: "O=Evil,CN=..." gibi enjeksiyon ek bir alan olusturamaz.
        var subject = new X500DistinguishedNameBuilder();
        subject.AddCommonName(host);
        AddIfPresent(value => subject.AddOrganizationName(value), request.Organization);
        AddIfPresent(value => subject.AddLocalityName(value), request.Locality);
        AddIfPresent(value => subject.AddStateOrProvinceName(value), request.State);
        if (country.Length != 0) subject.AddCountryOrRegion(country);

        using var rsa = RSA.Create(keySize);
        var san = new SubjectAlternativeNameBuilder();
        foreach (var name in new[] { host }.Concat(alt)) san.AddDnsName(name);
        var csr = new CertificateRequest(subject.Build(), rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        csr.CertificateExtensions.Add(san.Build());
        csr.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: true));
        var pem = csr.CreateSigningRequestPem();

        var pkcs8 = rsa.ExportPkcs8PrivateKey();
        string protectedKey;
        try { protectedKey = secretProtector.Protect(Convert.ToBase64String(pkcs8)); }
        finally { CryptographicOperations.ZeroMemory(pkcs8); }

        var record = new CsrRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            HostName = host,
            AltNames = alt,
            Subject = csr.SubjectName.Name,
            KeySize = keySize,
            CreatedAtUtc = DateTime.UtcNow,
            Status = "pending",
            CsrPem = pem,
            PublicKeySha256 = Convert.ToHexString(SHA256.HashData(rsa.ExportSubjectPublicKeyInfo())),
            ProtectedPrivateKey = protectedKey
        };
        await fileStore.UpdateAsync(StatePath, Array.Empty<CsrRecord>(), existing =>
        {
            if (existing.Count(item => item.Status == "pending") >= MaxPending)
                throw new AppException("ssl.csr_limit_reached", 409, 409);
            return existing.Append(record).ToArray();
        }, cancellationToken);
        return ToDto(record, includeCsr: true);
    }

    public async Task<SslCertificateDto> ImportSignedAsync(string id, ImportSignedCertificateRequest request,
        CancellationToken cancellationToken = default)
    {
        var all = await fileStore.ReadAsync(StatePath, Array.Empty<CsrRecord>(), cancellationToken);
        var record = all.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.Ordinal))
            ?? throw new AppException("ssl.csr_not_found", 404, 404);
        if (record.Status != "pending" || string.IsNullOrEmpty(record.ProtectedPrivateKey))
            throw new AppException("ssl.csr_not_pending", 409, 409);

        byte[] raw;
        try { raw = Convert.FromBase64String(request.Base64Content ?? string.Empty); }
        catch (FormatException) { throw new AppException("ssl.invalid_certificate_payload", 400, 400); }

        var collection = new X509Certificate2Collection();
        try
        {
            LoadCertificates(raw, collection);
            var leaf = collection.Cast<X509Certificate2>().FirstOrDefault(item => PublicKeyHash(item) == record.PublicKeySha256)
                ?? throw new AppException("ssl.csr_certificate_mismatch", 400, 400);

            var pkcs8 = Convert.FromBase64String(secretProtector.Unprotect(record.ProtectedPrivateKey));
            var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
            byte[] pfx;
            try
            {
                using var rsa = RSA.Create();
                rsa.ImportPkcs8PrivateKey(pkcs8, out _);
                using var withKey = leaf.CopyWithPrivateKey(rsa);
                var bundle = new X509Certificate2Collection { withKey };
                foreach (var other in collection.Cast<X509Certificate2>().Where(item => item.Thumbprint != leaf.Thumbprint))
                    bundle.Add(other);
                pfx = bundle.Export(X509ContentType.Pfx, password)!;
            }
            finally { CryptographicOperations.ZeroMemory(pkcs8); }

            SslCertificateDto imported;
            try
            {
                imported = await sslService.ImportCertificateAsync(new ImportCertificateRequest
                {
                    SiteName = request.SiteName,
                    HostName = record.HostName,
                    FileName = record.HostName + ".pfx",
                    Base64Content = Convert.ToBase64String(pfx),
                    Password = password,
                    Port = request.Port <= 0 ? 443 : request.Port
                }, cancellationToken);
            }
            finally { CryptographicOperations.ZeroMemory(pfx); }

            // Basari OLCULDU (ice aktarma dondu); simdi korunan anahtari kayittan sil.
            await fileStore.UpdateAsync(StatePath, Array.Empty<CsrRecord>(), existing =>
            {
                foreach (var item in existing.Where(item => item.Id == id))
                {
                    item.Status = "imported";
                    item.ImportedThumbprint = imported.Thumbprint;
                    item.ProtectedPrivateKey = string.Empty;
                }
                return existing.ToArray();
            }, cancellationToken);
            return imported;
        }
        catch (CryptographicException error)
        {
            throw new AppException("ssl.invalid_certificate_payload", 400, 400, error.Message);
        }
        finally
        {
            foreach (var certificate in collection) certificate.Dispose();
        }
    }

    public async Task DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        var found = false;
        await fileStore.UpdateAsync(StatePath, Array.Empty<CsrRecord>(), existing =>
        {
            var kept = existing.Where(item => !string.Equals(item.Id, id, StringComparison.Ordinal)).ToArray();
            found = kept.Length != existing.Length;
            return kept;
        }, cancellationToken);
        if (!found) throw new AppException("ssl.csr_not_found", 404, 404);
    }

    internal static string PublicKeyHash(X509Certificate2 certificate)
    {
        using var rsa = certificate.GetRSAPublicKey();
        return rsa is null ? string.Empty : Convert.ToHexString(SHA256.HashData(rsa.ExportSubjectPublicKeyInfo()));
    }

    /// <summary>PEM (birden cok blok), DER ya da P7B/PKCS#7 icerigini sertifika listesine acar.</summary>
    internal static void LoadCertificates(byte[] raw, X509Certificate2Collection target)
    {
        var text = Encoding.ASCII.GetString(raw);
        if (text.Contains("-----BEGIN CERTIFICATE-----", StringComparison.Ordinal))
        {
            // Her CERTIFICATE blogu tek tek okunur (ozel anahtar bloklari yok sayilir).
            // Bazi araclar bloklari satir sonu OLMADAN ard arda yazar; PemEncoding bunu bulamaz.
            var remaining = text.Replace("-----END CERTIFICATE-----", "-----END CERTIFICATE-----" + Environment.NewLine, StringComparison.Ordinal).AsSpan();
            while (PemEncoding.TryFind(remaining, out var fields))
            {
                if (remaining[fields.Label].SequenceEqual("CERTIFICATE"))
                    target.Add(new X509Certificate2(Convert.FromBase64String(remaining[fields.Base64Data].ToString())));
                remaining = remaining[fields.Location.End..];
            }
            if (target.Count == 0) throw new CryptographicException("No certificate in PEM.");
            return;
        }
        target.Import(raw);
        if (target.Count == 0) throw new CryptographicException("No certificate.");
    }

    private static string NormalizeHost(string value)
    {
        var host = (value ?? string.Empty).Trim().TrimEnd('.').ToLowerInvariant();
        if (host.Length == 0 || host.Length > 253) throw new AppException("ssl.csr_host_invalid", 400, 400);
        try { host = new IdnMapping().GetAscii(host); }
        catch (ArgumentException) { throw new AppException("ssl.csr_host_invalid", 400, 400); }
        if (!HostPattern().IsMatch(host)) throw new AppException("ssl.csr_host_invalid", 400, 400);
        return host;
    }

    private static void AddIfPresent(Action<string> add, string? value)
    {
        var trimmed = (value ?? string.Empty).Trim();
        if (trimmed.Length == 0) return;
        if (trimmed.Length > 64 || trimmed.Any(char.IsControl)) throw new AppException("ssl.csr_field_invalid", 400, 400);
        add(trimmed);
    }

    private static SslCsrDto ToDto(CsrRecord record, bool includeCsr) => new()
    {
        Id = record.Id,
        HostName = record.HostName,
        AltNames = record.AltNames,
        Subject = record.Subject,
        KeySize = record.KeySize,
        CreatedAtUtc = record.CreatedAtUtc,
        Status = record.Status,
        ImportedThumbprint = record.ImportedThumbprint,
        CsrPem = includeCsr ? record.CsrPem : string.Empty
    };

    [GeneratedRegex("^[A-Z]{2}$")]
    private static partial Regex CountryPattern();

    [GeneratedRegex(@"^(\*\.)?([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z0-9-]{2,63}$")]
    private static partial Regex HostPattern();

    internal sealed class CsrRecord
    {
        public string Id { get; set; } = string.Empty;
        public string HostName { get; set; } = string.Empty;
        public string[] AltNames { get; set; } = [];
        public string Subject { get; set; } = string.Empty;
        public int KeySize { get; set; }
        public DateTime CreatedAtUtc { get; set; }
        public string Status { get; set; } = "pending";
        public string CsrPem { get; set; } = string.Empty;
        public string PublicKeySha256 { get; set; } = string.Empty;
        public string ImportedThumbprint { get; set; } = string.Empty;

        /// <summary>DataProtection ile korunmus PKCS#8. Hicbir DTO'ya kopyalanmaz.</summary>
        public string ProtectedPrivateKey { get; set; } = string.Empty;
    }
}
