using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using AdaPanel.Infrastructure.Ssl;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class SslRepairCertificateValidationTests
{
    private static readonly DateTime Now = new(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc);

    private static X509Certificate2 Certificate(DateTime? start = null, DateTime? end = null)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=example.test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder(); san.AddDnsName("example.test");
        request.CertificateExtensions.Add(san.Build());
        return request.CreateSelfSigned(start ?? Now.AddDays(-1), end ?? Now.AddDays(90));
    }

    [Theory]
    [InlineData(X509ChainStatusFlags.UntrustedRoot, "untrusted_root")]
    [InlineData(X509ChainStatusFlags.PartialChain, "incomplete_chain")]
    [InlineData(X509ChainStatusFlags.Revoked, "revoked")]
    [InlineData(X509ChainStatusFlags.RevocationStatusUnknown, "revocation_unavailable")]
    [InlineData(X509ChainStatusFlags.OfflineRevocation, "revocation_unavailable")]
    [InlineData(X509ChainStatusFlags.NotTimeValid, "chain_time_invalid")]
    [InlineData(X509ChainStatusFlags.NotValidForUsage, "wrong_usage")]
    [InlineData(X509ChainStatusFlags.NotSignatureValid, "chain_failed")]
    [InlineData(X509ChainStatusFlags.NoError, "chain_failed")]
    public void Failed_chain_exposes_specific_reason_and_never_reports_valid(X509ChainStatusFlags flags, string reason)
    {
        using var certificate = Certificate();
        var result = SslRepairCertificateValidation.Describe(certificate, "example.test", Now, _ => new(false, flags));
        Assert.False(result.Valid);
        Assert.Equal(["ssl.repair.reason." + reason], result.ValidationIssues);
        Assert.Equal(certificate.Thumbprint, result.Thumbprint);
    }

    [Fact]
    public void Multiple_chain_failures_are_retained_without_duplicate_revocation_reason()
    {
        using var certificate = Certificate();
        var result = SslRepairCertificateValidation.Describe(certificate, "example.test", Now,
            _ => new(false, X509ChainStatusFlags.UntrustedRoot | X509ChainStatusFlags.OfflineRevocation | X509ChainStatusFlags.RevocationStatusUnknown));
        Assert.False(result.Valid);
        Assert.Equal(["ssl.repair.reason.untrusted_root", "ssl.repair.reason.revocation_unavailable"], result.ValidationIssues);
    }

    [Fact]
    public void Verified_chain_has_no_failure_reason()
    {
        using var certificate = Certificate();
        var result = SslRepairCertificateValidation.Describe(certificate, "example.test", Now, _ => new(true, X509ChainStatusFlags.NoError));
        Assert.True(result.Valid); Assert.Empty(result.ValidationIssues!);
    }

    [Theory]
    [InlineData("hostname_mismatch")]
    [InlineData("private_key_missing")]
    [InlineData("expired")]
    [InlineData("not_yet_valid")]
    public void Invalid_prerequisite_is_reported_without_network_lookup(string reason)
    {
        using var keyed = Certificate(reason == "not_yet_valid" ? Now.AddDays(1) : Now.AddDays(-2),
            reason == "expired" ? Now.AddDays(-1) : Now.AddDays(90));
        using var certificate = reason == "private_key_missing" ? new X509Certificate2(keyed.RawData) : new X509Certificate2(keyed);
        var result = SslRepairCertificateValidation.Describe(certificate, reason == "hostname_mismatch" ? "other.test" : "example.test", Now,
            _ => throw new InvalidOperationException("A failed prerequisite must not trigger network lookup."));
        Assert.False(result.Valid); Assert.Contains("ssl.repair.reason." + reason, result.ValidationIssues!);
    }

    [Fact]
    public void Actual_untrusted_self_signed_certificate_is_blocked_by_windows_chain_validation()
    {
        using var certificate = Certificate(DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(1));
        var result = SslRepairCertificateValidation.Describe(certificate, "example.test", DateTime.UtcNow);
        Assert.False(result.Valid);
        Assert.Contains("ssl.repair.reason.untrusted_root", result.ValidationIssues!);
    }
}
