using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using AdaPanel.Infrastructure.Ssl;
using Org.BouncyCastle.Pkcs;

namespace AdaPanel.Infrastructure.Mail;

internal sealed record MailCertificateMaterial(string CertificatePem, string PrivateKeyPem, string Thumbprint);

internal static class MailCertificateStore
{
    internal static MailCertificateMaterial? Find(string host)
    {
        var candidates = new List<X509Certificate2>();
        try
        {
            foreach (var name in new[] { "My", "WebHosting" })
            {
                using var store = new X509Store(name, StoreLocation.LocalMachine);
                try { store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly); }
                catch (CryptographicException) { continue; }
                candidates.AddRange(store.Certificates.Cast<X509Certificate2>());
            }
            return FindInCandidates(host, candidates);
        }
        finally { foreach (var certificate in candidates) certificate.Dispose(); }
    }

    // The caller owns the candidates. Tests configure trust only in memory; the live path
    // always uses Windows trust and the normal revocation policy.
    internal static MailCertificateMaterial? FindInCandidates(string host, IEnumerable<X509Certificate2> candidates,
        Action<X509ChainPolicy>? configureTrust = null)
    {
        CryptographicException? exportFailure = null;
        // A malformed SAN in one installed certificate must not prevent selection from
        // the rest of the store. Share the strict, failure-safe SAN matcher with web SSL.
        foreach (var certificate in candidates.Where(c => c.HasPrivateKey && SslCertificateSelection.MatchesHost(c, host)
            && c.NotBefore.ToUniversalTime() <= DateTime.UtcNow && c.NotAfter.ToUniversalTime() > DateTime.UtcNow)
            .OrderByDescending(c => c.NotAfter))
        {
            using var chain = new X509Chain();
            chain.ChainPolicy.UrlRetrievalTimeout = TimeSpan.FromSeconds(5);
            chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1"));
            configureTrust?.Invoke(chain.ChainPolicy);
            if (!chain.Build(certificate)) continue;
            // Never replace a trusted certificate with an ACME fallback because its key is not exportable.
            // Windows CNG may allow encrypted PFX export but reject direct plaintext key
            // export. Use an ephemeral in-memory copy; do not alter the installed key policy.
            byte[] archive;
            try { archive = certificate.Export(X509ContentType.Pfx, string.Empty); }
            catch (CryptographicException error)
            {
                exportFailure = error;
                var cached = FindCachedCertificate(certificate.Thumbprint);
                if (cached is null) continue;
                using (cached) archive = cached.Export(X509ContentType.Pfx, string.Empty);
            }
            string key;
            try { key = ExportArchiveKey(archive); }
            finally { CryptographicOperations.ZeroMemory(archive); }
            // A self-issued rollover intermediate can share its issuer's name without being
            // self-signed. Omit only the validated chain's trust anchor, never an intermediate.
            var pem = string.Join("\n", chain.ChainElements.Cast<X509ChainElement>()
                .Where((_, index) => index == 0 || index < chain.ChainElements.Count - 1)
                .Select(e => e.Certificate.ExportCertificatePem())) + "\n";
            return new(pem, key, certificate.Thumbprint);
        }
        if (exportFailure is not null)
            throw new CryptographicException("The matching certificate key cannot be exported and no matching win-acme cache was usable.", exportFailure);
        return null;
    }

    internal static string ExportArchiveKey(byte[] archive)
    {
        // Decode the already-authorized PFX in memory. Re-importing it into CNG can preserve
        // an encrypted-export-only policy and still reject ExportPkcs8PrivateKeyPem.
        var store = new Pkcs12StoreBuilder().Build();
        using var input = new MemoryStream(archive, writable: false);
        store.Load(input, []);
        var alias = store.Aliases.FirstOrDefault(store.IsKeyEntry)
            ?? throw new CryptographicException("The certificate archive has no private key.");
        var bytes = PrivateKeyInfoFactory.CreatePrivateKeyInfo(store.GetKey(alias).Key).GetEncoded();
        try { return PemEncoding.WriteString("PRIVATE KEY", bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    // Read the existing protected cache only. Never decrypt configuration on disk, change
    // machine key permissions or request a replacement certificate to obtain its key.
    internal static X509Certificate2? FindCachedCertificate(string thumbprint, string? cacheRoot = null)
    {
        var root = cacheRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "win-acme", "acme-v02.api.letsencrypt.org");
        if (!Directory.Exists(root)) return null;
        foreach (var path in Directory.EnumerateFiles(root, "*.renewal.json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                using var json = JsonDocument.Parse(File.ReadAllText(path));
                if (json.RootElement.ValueKind != JsonValueKind.Object ||
                    !json.RootElement.TryGetProperty("Id", out var id) || id.ValueKind != JsonValueKind.String ||
                    !json.RootElement.TryGetProperty("PfxPasswordProtected", out var secret) || secret.ValueKind != JsonValueKind.String)
                    continue;
                var directory = Path.Combine(root, "Certificates");
                if (!Directory.Exists(directory)) continue;
                var identifier = id.GetString();
                if (string.IsNullOrWhiteSpace(identifier)) continue;
                string password;
                try { password = DecodeCachePassword(secret.GetString()!); }
                catch (Exception error) when (error is CryptographicException or FormatException) { continue; }
                foreach (var archive in Directory.EnumerateFiles(directory, "*.pfx")
                    .Where(file => Path.GetFileName(file).StartsWith(identifier + "-", StringComparison.OrdinalIgnoreCase)))
                {
                    var collection = new X509Certificate2Collection();
                    try
                    {
                        collection.Import(archive, password, X509KeyStorageFlags.Exportable | X509KeyStorageFlags.EphemeralKeySet);
                        var match = collection.Cast<X509Certificate2>().FirstOrDefault(c => c.HasPrivateKey &&
                            string.Equals(c.Thumbprint, thumbprint, StringComparison.OrdinalIgnoreCase));
                        if (match is not null)
                        {
                            var bytes = match.Export(X509ContentType.Pfx);
                            try { return new X509Certificate2(bytes, "", X509KeyStorageFlags.Exportable | X509KeyStorageFlags.EphemeralKeySet); }
                            finally { CryptographicOperations.ZeroMemory(bytes); }
                        }
                    }
                    catch (Exception error) when (error is CryptographicException or IOException or UnauthorizedAccessException) { }
                    finally { foreach (var c in collection) c.Dispose(); }
                }
            }
            // One damaged, locked or concurrently replaced renewal must not hide a valid
            // cache entry for another site. Selection still requires the exact thumbprint.
            catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException) { }
        }
        return null;
    }

    internal static string DecodeCachePassword(string value)
    {
        if (value.StartsWith("clear-", StringComparison.Ordinal)) return value[6..];
        var encrypted = value.StartsWith("enc-", StringComparison.Ordinal);
        var bytes = Convert.FromBase64String(encrypted ? value[4..] : value);
        try
        {
            if (!encrypted) return Encoding.UTF8.GetString(bytes);
            var clear = ProtectedData.Unprotect(bytes, null, DataProtectionScope.LocalMachine);
            try { return Encoding.UTF8.GetString(clear); }
            finally { CryptographicOperations.ZeroMemory(clear); }
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}
