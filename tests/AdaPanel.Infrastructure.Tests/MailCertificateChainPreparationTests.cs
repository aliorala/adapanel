using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using AdaPanel.Core.Common;
using AdaPanel.Infrastructure.Mail;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class MailCertificateChainPreparationTests
{
    [Theory]
    [InlineData("ordered")]
    [InlineData("reversed-intermediates")]
    [InlineData("root-between")]
    [InlineData("root-last")]
    [InlineData("duplicate")]
    [InlineData("unrelated")]
    public void ValidatedPemContainsOnlyTheOrderedChain(string arrangement)
    {
        using var chain = new MailChainFixture();
        using var unrelated = new MailChainFixture();
        var certificates = arrangement switch
        {
            "reversed-intermediates" => new[] { chain.Leaf, chain.Upper, chain.Lower },
            "root-between" => [chain.Leaf, chain.Root, chain.Lower, chain.Upper],
            "root-last" => [chain.Leaf, chain.Lower, chain.Upper, chain.Root],
            "duplicate" => [chain.Leaf, chain.Lower, chain.Leaf, chain.Upper, chain.Lower],
            "unrelated" => [chain.Leaf, unrelated.Leaf, chain.Lower, chain.Upper],
            _ => [chain.Leaf, chain.Lower, chain.Upper]
        };
        var input = MailChainFixture.Pem(certificates);
        var output = MailCertificateValidation.Validate(MailChainFixture.Host, input, chain.Key, chain.Trust);
        Assert.Equal(chain.OrderedPem, output);
        Assert.Equal(MailChainFixture.Pem(certificates), input);
        Assert.Equal(output, MailCertificateValidation.Validate(MailChainFixture.Host, output, chain.Key, chain.Trust));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ALocallyKnownIntermediateStillMustBeSupplied(bool omitUpper)
    {
        using var chain = new MailChainFixture();
        var input = MailChainFixture.Pem(chain.Leaf, omitUpper ? chain.Lower : chain.Upper);
        var error = Assert.Throws<AppException>(() =>
            MailCertificateValidation.Validate(MailChainFixture.Host, input, chain.Key, chain.Trust));
        Assert.Equal("mail.certificate_invalid", error.MessageKey);
        Assert.DoesNotContain("PRIVATE KEY", error.ToString());
    }
}

internal sealed class MailChainFixture : IDisposable
{
    internal const string Host = "mail.example.test";
    internal readonly X509Certificate2 Root, Upper, Lower, Leaf;
    internal string Key => Leaf.GetECDsaPrivateKey() is { } key ? Export(key) : throw new InvalidOperationException();
    internal string OrderedPem => Pem(Leaf, Lower, Upper);

    internal MailChainFixture()
    {
        Root = Create("root-" + Guid.NewGuid().ToString("N"), null, true);
        Upper = Create("upper", Root, true);
        Lower = Create("lower", Upper, true);
        Leaf = Create(Host, Lower, false);
    }

    private static string Export(ECDsa key) { using (key) return key.ExportPkcs8PrivateKeyPem(); }
    internal static string Pem(params X509Certificate2[] certificates) =>
        string.Join("\n", certificates.Select(c => c.ExportCertificatePem())) + "\n";

    internal void Trust(X509ChainPolicy policy)
    {
        policy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        policy.CustomTrustStore.Add(Root);
        policy.ExtraStore.AddRange(new[] { Upper, Lower });
        policy.DisableCertificateDownloads = true;
        policy.RevocationMode = X509RevocationMode.NoCheck; // isolated test CA; no system store changes
    }

    private static X509Certificate2 Create(string name, X509Certificate2? issuer, bool ca)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=" + name, key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(ca, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(ca
            ? X509KeyUsageFlags.KeyCertSign : X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        if (!ca)
        {
            var san = new SubjectAlternativeNameBuilder(); san.AddDnsName(Host);
            request.CertificateExtensions.Add(san.Build());
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, false));
        }
        var before = DateTimeOffset.UtcNow.AddDays(-1);
        var after = DateTimeOffset.UtcNow.AddDays(20);
        if (issuer is null) return request.CreateSelfSigned(before.AddDays(-1), after.AddDays(1));
        using var issued = request.Create(issuer, issuer.NotBefore, issuer.NotAfter, RandomNumberGenerator.GetBytes(16));
        return issued.CopyWithPrivateKey(key);
    }

    public void Dispose() { Leaf.Dispose(); Lower.Dispose(); Upper.Dispose(); Root.Dispose(); }
}
