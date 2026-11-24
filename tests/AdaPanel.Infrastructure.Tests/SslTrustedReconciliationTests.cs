using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using AdaPanel.Core.Dtos.Ssl;
using AdaPanel.Infrastructure.Ssl;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class SslTrustedReconciliationTests
{
    private const string Host = "video.example.test";

    [Theory]
    [InlineData("offline")]
    [InlineData("revocation-unknown")]
    [InlineData("partial-chain")]
    [InlineData("crypto-error")]
    public void Unknown_recorded_chain_preserves_its_thumbprint_even_when_another_candidate_is_trusted(string reason)
    {
        using var recorded = Certificate(-1, 90);
        using var other = Certificate(-1, 30);
        var record = Record(recorded);
        var outcome = reason switch
        {
            "offline" => SslCertificateSelection.ClassifyChain(false, [X509ChainStatusFlags.OfflineRevocation]),
            "revocation-unknown" => SslCertificateSelection.ClassifyChain(false, [X509ChainStatusFlags.RevocationStatusUnknown]),
            "partial-chain" => SslCertificateSelection.ClassifyChain(false, [X509ChainStatusFlags.PartialChain]),
            _ => SslCertificateSelection.EvaluateTrust(recorded, Host, _ => throw new CryptographicException("fixture provider unavailable"))
        };
        Assert.Equal(SslCertificateSelection.TrustOutcome.Unknown, outcome);
        Assert.Equal(SslCertificateSelection.TrustOutcome.Trusted, SslCertificateSelection.EvaluateTrust(other, Host, Trust(other)));
        var probe = WinAcmeSslService.BuildReconciliationProbe(record, [recorded, other], true,
            trustAssessment: c => ReferenceEquals(c, recorded) ? outcome : SslCertificateSelection.EvaluateTrust(c, Host, Trust(other)));
        SslStateReconciler.Reconcile([record], _ => probe, DateTime.UtcNow);
        Assert.Equal(recorded.Thumbprint, record.Thumbprint);
        Assert.Equal(recorded.NotAfter.ToUniversalTime(), record.NotAfterUtc);
        Assert.True(record.IsPresentInStore);
    }

    [Fact]
    public void Recorded_duplicate_thumbprints_prefer_the_keyed_copy_before_classifying_unknown_trust()
    {
        using var recorded = Certificate(-1, 90);
        using var publicOnly = new X509Certificate2(recorded.RawData);
        using var other = Certificate(-1, 30);
        var probe = WinAcmeSslService.BuildReconciliationProbe(Record(recorded), [publicOnly, recorded, other], true,
            trustAssessment: c => c.Thumbprint == recorded.Thumbprint
                ? c.HasPrivateKey ? SslCertificateSelection.TrustOutcome.Unknown : SslCertificateSelection.TrustOutcome.Invalid
                : SslCertificateSelection.EvaluateTrust(c, Host, Trust(other)));
        Assert.Equal(recorded.Thumbprint, probe.Thumbprint);
    }

    [Theory]
    [InlineData(X509ChainStatusFlags.UntrustedRoot)]
    [InlineData(X509ChainStatusFlags.Revoked)]
    [InlineData(X509ChainStatusFlags.NotValidForUsage)]
    [InlineData(X509ChainStatusFlags.NotTimeValid)]
    [InlineData(X509ChainStatusFlags.Revoked | X509ChainStatusFlags.OfflineRevocation)]
    public void Definite_invalid_chain_status_takes_precedence_over_unknown_status(X509ChainStatusFlags flags)
    {
        Assert.Equal(SslCertificateSelection.TrustOutcome.Invalid, SslCertificateSelection.ClassifyChain(false, [flags]));
    }

    [Fact]
    public void Longer_untrusted_certificate_does_not_replace_a_trusted_bound_certificate()
    {
        using var bound = Certificate(-1, 30);
        using var untrusted = Certificate(-1, 300);
        var record = Record(bound);
        // Keep the existing discovery/issuance contract: it is not a trust verdict.
        Assert.Same(untrusted, SslCertificateSelection.Select([bound, untrusted], Host));
        var probe = WinAcmeSslService.BuildReconciliationProbe(record, [bound, untrusted], true, Trust(bound));
        Assert.Equal(bound.Thumbprint, probe.Thumbprint);
        SslStateReconciler.Reconcile([record], _ => probe, DateTime.UtcNow);
        Assert.Equal(bound.Thumbprint, record.Thumbprint);
        Assert.True(SslBindingVerification.Olc([Binding(bound)], "site", Host, 443, record.Thumbprint).CertificateBound);
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("future")]
    [InlineData("keyless")]
    [InlineData("wrong-host")]
    [InlineData("client-only")]
    [InlineData("untrusted")]
    public void Reconciliation_requires_private_key_hostname_time_ServerAuth_and_chain(string reason)
    {
        using var good = Certificate(-1, 30);
        using var other = Certificate(reason == "future" ? 1 : -30, reason == "expired" ? -1 : 300,
            reason == "wrong-host" ? "other.example.test" : Host, reason == "client-only");
        using var publicOnly = new X509Certificate2(other.RawData);
        var rejected = reason == "keyless" ? publicOnly : other;
        Assert.Same(good, SslCertificateSelection.SelectForReconciliation([rejected, good], Host,
            reason == "untrusted" ? Trust(good) : Trust(good, other)));
    }

    [Fact]
    public void A_trusted_renewed_certificate_is_discovered_and_the_real_old_binding_remains_drift()
    {
        using var old = Certificate(-1, 10);
        using var renewed = Certificate(-1, 90);
        var record = Record(old);
        var probe = WinAcmeSslService.BuildReconciliationProbe(record, [old, renewed], true, Trust(old, renewed));
        SslStateReconciler.Reconcile([record], _ => probe, DateTime.UtcNow);
        Assert.Equal(renewed.Thumbprint, record.Thumbprint);
        Assert.Equal(renewed.NotAfter.ToUniversalTime(), record.NotAfterUtc);
        Assert.False(SslBindingVerification.Olc([Binding(old)], "site", Host, 443, record.Thumbprint).CertificateBound);
    }

    [Fact]
    public void Invalid_expected_certificate_is_replaced_by_the_actual_trusted_bound_candidate()
    {
        using var invalid = Certificate(-1, 300);
        using var bound = Certificate(-1, 30);
        var record = Record(invalid);
        var probe = WinAcmeSslService.BuildReconciliationProbe(record, [invalid, bound], true, Trust(bound));
        SslStateReconciler.Reconcile([record], _ => probe, DateTime.UtcNow);
        Assert.Equal(bound.Thumbprint, record.Thumbprint);
        Assert.True(SslBindingVerification.Olc([Binding(bound)], "site", Host, 443, record.Thumbprint).CertificateBound);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void No_trusted_candidate_preserves_and_measures_the_recorded_thumbprint_metadata(bool chainUnavailable)
    {
        using var existing = Certificate(-10, -1);
        using var longer = Certificate(-1, 300);
        var record = Record(existing); record.NotAfterUtc = DateTime.UtcNow.AddYears(2);
        var probe = WinAcmeSslService.BuildReconciliationProbe(record, [existing, longer], true,
            chainUnavailable ? _ => throw new CryptographicException("fixture chain unavailable") : Trust());
        Assert.Equal(SslStoreProbeOutcome.Found, probe.Outcome);
        Assert.Equal(existing.Thumbprint, probe.Thumbprint);
        SslStateReconciler.Reconcile([record], _ => probe, DateTime.UtcNow);
        Assert.Equal(existing.Thumbprint, record.Thumbprint);
        Assert.Equal(existing.NotAfter.ToUniversalTime(), record.NotAfterUtc);
        Assert.True(record.IsPresentInStore);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Incomplete_store_reads_never_claim_absence_or_switch_to_another_candidate(bool existingVisible)
    {
        using var existing = Certificate(-1, 15);
        using var other = Certificate(-1, 300);
        var record = Record(existing);
        var probe = WinAcmeSslService.ProbeReconciliationCertificate(record, name =>
        {
            if (name == "WebHosting") throw new CryptographicException("fixture access denied");
            return existingVisible ? [new X509Certificate2(existing), new X509Certificate2(other)] : [new X509Certificate2(other)];
        }, Trust(existing, other));
        Assert.Equal(existingVisible ? SslStoreProbeOutcome.Found : SslStoreProbeOutcome.Unmeasured, probe.Outcome);
        SslStateReconciler.Reconcile([record], _ => probe, DateTime.UtcNow);
        Assert.Equal(existing.Thumbprint, record.Thumbprint);
        Assert.NotEqual(false, record.IsPresentInStore);
    }

    [Fact]
    public void All_unreadable_stores_are_unknown_and_all_read_empty_stores_are_absent()
    {
        using var existing = Certificate(-1, 30);
        var record = Record(existing);
        Assert.Equal(SslStoreProbeOutcome.Unmeasured, WinAcmeSslService.ProbeReconciliationCertificate(record,
            _ => throw new UnauthorizedAccessException("fixture denied")).Outcome);
        Assert.Equal(SslStoreProbeOutcome.NotFound, WinAcmeSslService.ProbeReconciliationCertificate(record, _ => []).Outcome);
    }

    [Fact]
    public void An_absent_recorded_thumbprint_is_not_replaced_by_another_untrusted_certificate()
    {
        using var absent = Certificate(-1, 30);
        using var other = Certificate(-1, 300);
        var record = Record(absent);
        var probe = WinAcmeSslService.BuildReconciliationProbe(record, [other], true, Trust());
        Assert.Equal(SslStoreProbeOutcome.NotFound, probe.Outcome);
        SslStateReconciler.Reconcile([record], _ => probe, DateTime.UtcNow);
        Assert.Equal(absent.Thumbprint, record.Thumbprint);
        Assert.False(record.IsPresentInStore);
    }

    [Fact]
    public void A_known_absent_optional_store_does_not_hide_a_trusted_renewal()
    {
        using var old = Certificate(-1, 10);
        using var renewed = Certificate(-1, 90);
        var probe = WinAcmeSslService.ProbeReconciliationCertificate(Record(old), name =>
            name == "WebHosting" ? throw new CryptographicException(unchecked((int)0x80092004)) : [new X509Certificate2(renewed)], Trust(renewed));
        Assert.Equal(SslStoreProbeOutcome.Found, probe.Outcome);
        Assert.Equal(renewed.Thumbprint, probe.Thumbprint);
    }

    private static Action<X509ChainPolicy> Trust(params X509Certificate2[] roots) => policy =>
    {
        Assert.Equal(X509RevocationMode.Online, policy.RevocationMode);
        Assert.Contains(policy.ApplicationPolicy.Cast<Oid>(), oid => oid.Value == "1.3.6.1.5.5.7.3.1");
        // In-memory, offline fixture trust; production keeps normal machine trust and online revocation.
        policy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        policy.CustomTrustStore.AddRange(roots);
        policy.RevocationMode = X509RevocationMode.NoCheck;
    };
    private static SslCertificateDto Record(X509Certificate2 certificate) => new()
    { SiteName="site", HostName=Host, Port=443, Thumbprint=certificate.Thumbprint, NotAfterUtc=certificate.NotAfter.ToUniversalTime() };
    private static IisHttpsBinding Binding(X509Certificate2 certificate) => new()
    { SiteName="site", BindingInformation=$"*:443:{Host}", CertificateReadable=true, Thumbprint=certificate.Thumbprint, StoreName="My" };
    private static X509Certificate2 Certificate(int before, int after, string host=Host, bool clientOnly=false)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=" + host, key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder(); san.AddDnsName(host);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new(clientOnly ? "1.3.6.1.5.5.7.3.2" : "1.3.6.1.5.5.7.3.1") }, false));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(before), DateTimeOffset.UtcNow.AddDays(after));
    }
}
