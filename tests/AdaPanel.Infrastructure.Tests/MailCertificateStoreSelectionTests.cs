using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using AdaPanel.Infrastructure.Mail;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class MailCertificateStoreSelectionTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void MalformedSanCandidateDoesNotHideAValidCertificate(bool malformedFirst, bool sameSubject)
    {
        using var chain = new TestChain();
        using var working = chain.Leaf("1.3.6.1.5.5.7.3.1", 10);
        using var malformed = chain.Leaf("1.3.6.1.5.5.7.3.1", 20,
            host: sameSubject ? TestChain.Host : "other.example.test", malformedSan: true);
        var before = malformed.RawData;
        var material = MailCertificateStore.FindInCandidates(TestChain.Host,
            malformedFirst ? [malformed, working] : [working, malformed], chain.Trust);
        Assert.Equal(working.Thumbprint, material?.Thumbprint);
        MailCertificateValidation.Validate(TestChain.Host, material!.CertificatePem, material.PrivateKeyPem, chain.Trust);
        Assert.Equal(before, malformed.RawData);
        Assert.True(malformed.HasPrivateKey);
    }

    [Fact]
    public void MalformedSanIsNeverAcceptedThroughItsMatchingCommonName()
    {
        using var chain = new TestChain();
        using var malformed = chain.Leaf("1.3.6.1.5.5.7.3.1", 20, malformedSan: true);
        Assert.Null(MailCertificateStore.FindInCandidates(TestChain.Host, [malformed], chain.Trust));
    }

    [Fact]
    public void ValidCandidatesStillPreferTheLaterExpiry()
    {
        using var chain = new TestChain();
        using var old = chain.Leaf("1.3.6.1.5.5.7.3.1", 10);
        using var renewed = chain.Leaf("1.3.6.1.5.5.7.3.1", 20);
        Assert.Equal(renewed.Thumbprint, MailCertificateStore.FindInCandidates(TestChain.Host, [old, renewed], chain.Trust)?.Thumbprint);
    }

    [Fact]
    public void IntermediatePurposeRestrictionIsAlsoEnforced()
    {
        using var chain = new TestChain(issuerPurpose: "1.3.6.1.5.5.7.3.2");
        using var server = chain.Leaf("1.3.6.1.5.5.7.3.1", 10);
        Assert.Null(MailCertificateStore.FindInCandidates(TestChain.Host, [server], chain.Trust));
    }

    [Fact]
    public void NewerClientOnlyCertificateDoesNotHideUsableServerCertificate()
    {
        using var chain = new TestChain();
        using var server = chain.Leaf("1.3.6.1.5.5.7.3.1", 10);
        using var client = chain.Leaf("1.3.6.1.5.5.7.3.2", 20);
        var material = MailCertificateStore.FindInCandidates(TestChain.Host, [client, server], chain.Trust);
        Assert.NotNull(material);
        Assert.Equal(server.Thumbprint, material.Thumbprint);
        MailCertificateValidation.Validate(TestChain.Host, material.CertificatePem, material.PrivateKeyPem, chain.Trust);
    }

    [Fact]
    public void SameSubjectAndIssuerIntermediateIsRetainedWhenSignedByTheOldCaKey()
    {
        using var chain = new TestChain(rollover: true);
        using var server = chain.Leaf("1.3.6.1.5.5.7.3.1", 10);
        var material = MailCertificateStore.FindInCandidates(TestChain.Host, [server], chain.Trust);
        Assert.NotNull(material);
        var supplied = new X509Certificate2Collection();
        try
        {
            supplied.ImportFromPem(material.CertificatePem);
            Assert.Equal(new[] { server.Thumbprint, chain.Intermediate.Thumbprint },
                supplied.Cast<X509Certificate2>().Select(certificate => certificate.Thumbprint));
            Assert.Equal(chain.Intermediate.Subject, chain.Intermediate.Issuer);
            Assert.Equal(material.CertificatePem,
                MailCertificateValidation.Validate(TestChain.Host, material.CertificatePem, material.PrivateKeyPem, chain.Trust));
        }
        finally { foreach (var certificate in supplied) certificate.Dispose(); }
    }

    [Theory]
    [InlineData("1.3.6.1.5.5.7.3.1", true)]
    [InlineData("1.3.6.1.5.5.7.3.2", false)]
    [InlineData("1.3.6.1.5.5.7.3.3", false)]
    [InlineData(null, true)]
    public void CertificatePurposeIsCheckedBeforeExport(string? purpose, bool eligible)
    {
        using var chain = new TestChain();
        using var certificate = chain.Leaf(purpose, 10);
        var material = MailCertificateStore.FindInCandidates(TestChain.Host, [certificate], chain.Trust);
        Assert.Equal(eligible, material is not null);
        if (material is not null)
            MailCertificateValidation.Validate(TestChain.Host, material.CertificatePem, material.PrivateKeyPem, chain.Trust);
    }

    [Theory, InlineData("untrusted"), InlineData("expired"), InlineData("future"), InlineData("wrong-host"), InlineData("no-key")]
    public void IneligibleNewerCandidateCannotDisplaceTheWorkingCertificate(string scenario)
    {
        using var chain = new TestChain();
        using var other = new TestChain();
        using var working = chain.Leaf("1.3.6.1.5.5.7.3.1", 10);
        using var wrong = scenario == "no-key" ? new X509Certificate2(working.RawData)
            : scenario == "untrusted" ? other.Leaf("1.3.6.1.5.5.7.3.1", 20)
            : chain.Leaf("1.3.6.1.5.5.7.3.1", scenario == "expired" ? -1 : 20,
                scenario == "future" ? 1 : -2, scenario == "wrong-host" ? "other.example.test" : TestChain.Host);
        var material = MailCertificateStore.FindInCandidates(TestChain.Host, [wrong, working], chain.Trust);
        Assert.Equal(working.Thumbprint, material?.Thumbprint);
    }

    private sealed class TestChain : IDisposable
    {
        internal const string Host = "mail.example.test";
        private readonly ECDsa rootKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        private readonly ECDsa intermediateKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        internal X509Certificate2 Root { get; }
        internal X509Certificate2 Intermediate { get; }

        internal TestChain(bool rollover = false, string? issuerPurpose = null)
        {
            var rootName = "CN=isolated root " + Guid.NewGuid().ToString("N");
            Root = Request(rootName, rootKey, true).CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-5), DateTimeOffset.UtcNow.AddDays(90));
            var request = Request(rollover ? rootName : "CN=isolated intermediate", intermediateKey, true);
            if (issuerPurpose is not null) request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new(issuerPurpose) }, false));
            AddAuthorityKey(request, Root);
            using var issued = request.Create(Root, DateTimeOffset.UtcNow.AddDays(-4), DateTimeOffset.UtcNow.AddDays(60), RandomNumberGenerator.GetBytes(16));
            Intermediate = issued.CopyWithPrivateKey(intermediateKey);
        }

        internal X509Certificate2 Leaf(string? purpose, int days, int starts = -2, string host = Host, bool malformedSan = false)
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request = Request("CN=" + host, key, false);
            if (malformedSan) request.CertificateExtensions.Add(new X509Extension("2.5.29.17", [0x30, 0x01, 0xff], false));
            else
            {
                var san = new SubjectAlternativeNameBuilder(); san.AddDnsName(host); request.CertificateExtensions.Add(san.Build());
            }
            if (purpose is not null) request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new(purpose) }, false));
            AddAuthorityKey(request, Intermediate);
            using var certificate = request.Create(Intermediate, DateTimeOffset.UtcNow.AddDays(starts), DateTimeOffset.UtcNow.AddDays(days), RandomNumberGenerator.GetBytes(16));
            return certificate.CopyWithPrivateKey(key);
        }

        internal void Trust(X509ChainPolicy policy)
        {
            policy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            policy.CustomTrustStore.Add(Root);
            policy.ExtraStore.Add(Intermediate);
            // Isolated test certificates have no revocation endpoint. No live policy changes.
            policy.RevocationMode = X509RevocationMode.NoCheck;
            policy.DisableCertificateDownloads = true;
        }

        private static CertificateRequest Request(string subject, ECDsa key, bool ca)
        {
            var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(ca, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(ca ? X509KeyUsageFlags.KeyCertSign : X509KeyUsageFlags.DigitalSignature, true));
            request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
            return request;
        }

        private static void AddAuthorityKey(CertificateRequest request, X509Certificate2 issuer)
        {
            var identifier = issuer.Extensions.OfType<X509SubjectKeyIdentifierExtension>().Single().SubjectKeyIdentifier!;
            var writer = new AsnWriter(AsnEncodingRules.DER);
            writer.PushSequence(); writer.WriteOctetString(Convert.FromHexString(identifier), new Asn1Tag(TagClass.ContextSpecific, 0)); writer.PopSequence();
            request.CertificateExtensions.Add(new X509Extension("2.5.29.35", writer.Encode(), false));
        }

        public void Dispose() { Intermediate.Dispose(); Root.Dispose(); intermediateKey.Dispose(); rootKey.Dispose(); }
    }
}
