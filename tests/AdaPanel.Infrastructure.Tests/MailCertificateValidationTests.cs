using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using AdaPanel.Core.Common;
using AdaPanel.Infrastructure.Mail;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class MailCertificateValidationTests
{
    [Theory]
    [InlineData("valid")]
    [InlineData("wrong-host")]
    [InlineData("expired")]
    [InlineData("future")]
    [InlineData("wrong-key")]
    [InlineData("missing-intermediate")]
    [InlineData("untrusted-root")]
    [InlineData("client-only")]
    public void ReplacementIsValidatedBeforeAnyServerWrite(string scenario)
    {
        using var chain = new TestChain(scenario);
        var pem = chain.Leaf.ExportCertificatePem() + "\n";
        if (scenario != "missing-intermediate") pem += chain.Intermediate.ExportCertificatePem();
        var key = scenario == "wrong-key" ? chain.RootKey.ExportPkcs8PrivateKeyPem() : chain.LeafKey.ExportPkcs8PrivateKeyPem();
        void Trust(X509ChainPolicy policy)
        {
            // This test-only CA has no revocation endpoint; no machine certificates/settings change.
            policy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            policy.RevocationMode = X509RevocationMode.NoCheck;
            policy.DisableCertificateDownloads = true;
            if (scenario != "untrusted-root") policy.CustomTrustStore.Add(chain.Root);
            // Simulate Windows already knowing the intermediate, even when the PEM omits it.
            policy.ExtraStore.Add(chain.Intermediate);
        }
        void Validate() => MailCertificateValidation.Validate("mail.example.test", pem, key, Trust);
        if (scenario == "valid") Validate();
        else
        {
            var error = Assert.Throws<AppException>(Validate);
            Assert.Equal("mail.certificate_invalid", error.MessageKey);
            Assert.DoesNotContain("PRIVATE KEY", error.ToString());
        }
    }

    [Fact]
    public void SupplyingAnUntrustedRootDoesNotMakeItTrusted()
    {
        using var chain = new TestChain("valid");
        var pem = string.Join("\n", chain.Leaf.ExportCertificatePem(), chain.Intermediate.ExportCertificatePem(), chain.Root.ExportCertificatePem());
        var error = Assert.Throws<AppException>(() =>
            MailCertificateValidation.Validate("mail.example.test", pem, chain.LeafKey.ExportPkcs8PrivateKeyPem()));
        Assert.Equal("mail.certificate_invalid", error.MessageKey);
    }

    private sealed class TestChain : IDisposable
    {
        internal readonly RSA RootKey = RSA.Create(2048), LeafKey = RSA.Create(2048);
        private readonly RSA intermediateKey = RSA.Create(2048);
        internal readonly X509Certificate2 Root, Intermediate, Leaf;

        internal TestChain(string scenario)
        {
            var now = DateTimeOffset.UtcNow;
            var rootRequest = Request("CN=AdaPanel isolated test root " + Guid.NewGuid().ToString("N"), RootKey, true);
            Root = rootRequest.CreateSelfSigned(now.AddYears(-1), now.AddYears(2));
            var intermediateRequest = Request("CN=AdaPanel isolated intermediate", intermediateKey, true);
            using var intermediatePublic = intermediateRequest.Create(Root, now.AddMonths(-6), now.AddYears(1), RandomNumberGenerator.GetBytes(16));
            Intermediate = intermediatePublic.CopyWithPrivateKey(intermediateKey);
            var request = Request("CN=mail.example.test", LeafKey, false);
            var san = new SubjectAlternativeNameBuilder();
            san.AddDnsName(scenario == "wrong-host" ? "other.example.test" : "mail.example.test");
            request.CertificateExtensions.Add(san.Build());
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection
                { new(scenario == "client-only" ? "1.3.6.1.5.5.7.3.2" : "1.3.6.1.5.5.7.3.1") }, false));
            var before = scenario == "future" ? now.AddDays(1) : now.AddDays(-20);
            var after = scenario == "expired" ? now.AddDays(-1) : now.AddDays(30);
            using var leafPublic = request.Create(Intermediate, before, after, RandomNumberGenerator.GetBytes(16));
            Leaf = leafPublic.CopyWithPrivateKey(LeafKey);
        }

        private static CertificateRequest Request(string subject, RSA key, bool ca)
        {
            var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(ca, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(ca
                ? X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign : X509KeyUsageFlags.DigitalSignature, true));
            request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
            return request;
        }

        public void Dispose()
        {
            Leaf.Dispose(); Intermediate.Dispose(); Root.Dispose();
            LeafKey.Dispose(); intermediateKey.Dispose(); RootKey.Dispose();
        }
    }
}
