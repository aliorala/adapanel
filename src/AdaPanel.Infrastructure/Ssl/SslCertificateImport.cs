using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.X509;
using BcCertificate = Org.BouncyCastle.X509.X509Certificate;

namespace AdaPanel.Infrastructure.Ssl;

internal static class SslCertificateImport
{
    // This selects supplied issuer certificates; it does not establish trust. In particular,
    // no supplied root becomes trusted, and mail/TLS validation still uses system trust.
    internal static IReadOnlyList<byte[]> ReadIntermediates(byte[] pfx, string password, X509Certificate2 leaf)
    {
        var bundle = new X509Certificate2Collection();
        try
        {
            bundle.Import(pfx, password, X509KeyStorageFlags.EphemeralKeySet);
            var parser = new X509CertificateParser();
            var candidates = bundle.Cast<X509Certificate2>()
                .Select(certificate => parser.ReadCertificate(certificate.RawData)).ToArray();
            var pending = new Queue<BcCertificate>();
            pending.Enqueue(parser.ReadCertificate(leaf.RawData));
            var seen = new HashSet<string>(StringComparer.Ordinal) { Convert.ToHexString(leaf.RawData) };
            var result = new List<byte[]>();
            while (pending.TryDequeue(out var child))
            {
                foreach (var parent in candidates)
                {
                    var der = parent.GetEncoded();
                    if (seen.Contains(Convert.ToHexString(der)) || parent.GetBasicConstraints() < 0
                        || parent.GetKeyUsage() is { } usage && !usage[5]
                        || !SignedBy(child, parent) || SignedBy(parent, parent)) continue;
                    seen.Add(Convert.ToHexString(der));
                    result.Add(der); // DER contains no private key, even if the PFX included a CA key.
                    pending.Enqueue(parent);
                }
            }
            return result;
        }
        catch (Exception error) when (error is GeneralSecurityException or IOException or ArgumentException)
        {
            throw new CryptographicException("Invalid certificate bundle.");
        }
        finally
        {
            foreach (var certificate in bundle) certificate.Dispose();
        }
    }

    private static bool SignedBy(BcCertificate child, BcCertificate parent)
    {
        if (!child.IssuerDN.Equivalent(parent.SubjectDN)) return false;
        try { child.Verify(parent.GetPublicKey()); return true; }
        catch (Exception error) when (error is GeneralSecurityException or ArgumentException) { return false; }
    }

    internal static bool NeedsStoreWrite(X509Certificate2 incoming, IEnumerable<X509Certificate2> existing) =>
        !existing.Any(current => string.Equals(current.Thumbprint, incoming.Thumbprint, StringComparison.OrdinalIgnoreCase)
            && (!incoming.HasPrivateKey || current.HasPrivateKey));

    internal static void PersistIntermediate(X509Certificate2 certificate)
    {
        using var store = new X509Store(StoreName.CertificateAuthority, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadWrite);
        PersistToStore(store, certificate);
    }

    internal static void PersistToStore(X509Store store, X509Certificate2 certificate)
    {
        var existing = store.Certificates;
        try
        {
            if (NeedsStoreWrite(certificate, existing.Cast<X509Certificate2>())) store.Add(certificate);
        }
        finally
        {
            foreach (var current in existing) current.Dispose();
        }
    }
}
