using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace AdaPanel.Infrastructure.Ssl;

internal static class SslCertificateSelection
{
    internal static bool MatchesHost(X509Certificate2 certificate, string host)
    {
        try
        {
            // Wildcard issuance looks up the literal SAN; concrete hosts use standard
            // one-label wildcard matching. A CN substring is never a hostname match.
            return host.StartsWith("*.", StringComparison.Ordinal)
                ? certificate.Extensions.OfType<X509SubjectAlternativeNameExtension>()
                    .SelectMany(extension => extension.EnumerateDnsNames()).Contains(host, StringComparer.OrdinalIgnoreCase)
                : certificate.MatchesHostname(host, allowWildcards: true, allowCommonName: false);
        }
        catch (Exception error) when (error is CryptographicException or ArgumentException) { return false; }
    }

    internal static X509Certificate2? Select(IEnumerable<X509Certificate2> certificates, string host) =>
        certificates.Where(certificate => MatchesHost(certificate, host))
            .OrderByDescending(certificate => certificate.HasPrivateKey)
            .ThenByDescending(certificate => certificate.NotBefore.ToUniversalTime() <= DateTime.UtcNow
                && certificate.NotAfter.ToUniversalTime() > DateTime.UtcNow)
            .ThenByDescending(certificate => certificate.NotAfter)
            .FirstOrDefault();
}
