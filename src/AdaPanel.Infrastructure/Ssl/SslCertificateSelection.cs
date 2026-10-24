using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace AdaPanel.Infrastructure.Ssl;

internal static class SslCertificateSelection
{
    internal enum TrustOutcome { Trusted, Invalid, Unknown }
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

    // Reconciliation can change the expected IIS thumbprint. Discovery for issuance has
    // a different contract and must continue to expose expired/untrusted material.
    internal static X509Certificate2? SelectForReconciliation(IEnumerable<X509Certificate2> certificates, string host,
        Action<X509ChainPolicy>? configureTrust = null, Func<X509Certificate2, TrustOutcome>? trustAssessment = null) =>
        certificates.Where(certificate => (trustAssessment?.Invoke(certificate) ?? EvaluateTrust(certificate, host, configureTrust)) == TrustOutcome.Trusted)
            .OrderByDescending(certificate => certificate.NotAfter)
            .FirstOrDefault();

    internal static TrustOutcome EvaluateTrust(X509Certificate2 certificate, string host, Action<X509ChainPolicy>? configureTrust = null)
    {
        try
        {
            if (!certificate.HasPrivateKey || !MatchesHost(certificate, host)
                || certificate.NotBefore.ToUniversalTime() > DateTime.UtcNow || certificate.NotAfter.ToUniversalTime() <= DateTime.UtcNow) return TrustOutcome.Invalid;
            using var chain = new X509Chain();
            chain.ChainPolicy.RevocationMode = X509RevocationMode.Online;
            chain.ChainPolicy.UrlRetrievalTimeout = TimeSpan.FromSeconds(5);
            chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1"));
            configureTrust?.Invoke(chain.ChainPolicy);
            return ClassifyChain(chain.Build(certificate), chain.ChainStatus.Select(status => status.Status));
        }
        catch (Exception error) when (error is CryptographicException or UnauthorizedAccessException or System.Security.SecurityException) { return TrustOutcome.Unknown; }
    }

    internal static TrustOutcome ClassifyChain(bool built, IEnumerable<X509ChainStatusFlags> statuses)
    {
        var flags = statuses.Aggregate(X509ChainStatusFlags.NoError, (all, next) => all | next);
        // Missing intermediates can be a temporary AIA/download failure too. A known
        // invalid status still takes precedence when mixed with connectivity failures.
        const X509ChainStatusFlags unknown = X509ChainStatusFlags.OfflineRevocation
            | X509ChainStatusFlags.RevocationStatusUnknown | X509ChainStatusFlags.PartialChain;
        if ((flags & ~unknown) != X509ChainStatusFlags.NoError) return TrustOutcome.Invalid;
        if (!built || flags != X509ChainStatusFlags.NoError) return TrustOutcome.Unknown;
        return TrustOutcome.Trusted;
    }
}
