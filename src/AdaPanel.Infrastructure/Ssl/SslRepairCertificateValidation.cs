using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using AdaPanel.Core.Dtos.Ssl;

namespace AdaPanel.Infrastructure.Ssl;

internal static class SslRepairCertificateValidation
{
    internal sealed record ChainResult(bool Valid, X509ChainStatusFlags Flags);

    internal static SslRepairCertificate Describe(X509Certificate2 certificate, string host,
        DateTime now, Func<X509Certificate2, ChainResult>? validateChain = null)
    {
        var issues = new List<string>();
        void Add(string reason) => issues.Add("ssl.repair.reason." + reason);
        if (!certificate.HasPrivateKey) Add("private_key_missing");
        if (!SslCertificateSelection.MatchesHost(certificate, host)) Add("hostname_mismatch");
        if (certificate.NotBefore.ToUniversalTime() > now) Add("not_yet_valid");
        if (certificate.NotAfter.ToUniversalTime() <= now) Add("expired");
        // A failed prerequisite already blocks application; do not perform unnecessary network lookups.
        if (issues.Count == 0)
        {
            ChainResult result;
            try { result = (validateChain ?? ValidateChain)(certificate); }
            catch (CryptographicException) { result = new(false, X509ChainStatusFlags.NoError); }
            if (!result.Valid)
            {
                var flags = result.Flags;
                if (flags.HasFlag(X509ChainStatusFlags.UntrustedRoot)) Add("untrusted_root");
                if (flags.HasFlag(X509ChainStatusFlags.PartialChain)) Add("incomplete_chain");
                if (flags.HasFlag(X509ChainStatusFlags.Revoked)) Add("revoked");
                if ((flags & (X509ChainStatusFlags.RevocationStatusUnknown | X509ChainStatusFlags.OfflineRevocation)) != 0) Add("revocation_unavailable");
                if (flags.HasFlag(X509ChainStatusFlags.NotTimeValid)) Add("chain_time_invalid");
                if (flags.HasFlag(X509ChainStatusFlags.NotValidForUsage)) Add("wrong_usage");
                if (issues.Count == 0) Add("chain_failed");
            }
        }
        return new(certificate.Thumbprint, certificate.Subject, certificate.NotAfter.ToUniversalTime(), issues.Count == 0, issues);
    }

    private static ChainResult ValidateChain(X509Certificate2 certificate)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.Online;
        chain.ChainPolicy.UrlRetrievalTimeout = TimeSpan.FromSeconds(10);
        chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1"));
        var valid = chain.Build(certificate);
        return new(valid, chain.ChainStatus.Aggregate(X509ChainStatusFlags.NoError, (flags, status) => flags | status.Status));
    }
}
