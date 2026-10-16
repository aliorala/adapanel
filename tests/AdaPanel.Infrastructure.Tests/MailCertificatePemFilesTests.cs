using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using AdaPanel.Core.Common;
using AdaPanel.Infrastructure.Mail;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class MailCertificatePemFilesTests
{
    [Fact]
    public void ReadDoesNotDenyTheRenewalWritersAccess()
    {
        using var f = new Fixture();
        using var certificate = f.Write("mail");
        using var writer = new FileStream(f.PathFor("mail-key.pem"), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        var pair = MailService.ReadPemPair(f.Directory);
        using var leaf = X509Certificate2.CreateFromPem(pair.CertificatePem, pair.PrivateKeyPem);
        Assert.Equal(certificate.Thumbprint, leaf.Thumbprint);
    }

    [Theory, InlineData("key"), InlineData("certificate"), InlineData("add-chain"), InlineData("new-group")]
    public void ChangesBetweenSnapshotsAreRejected(string change)
    {
        using var f = new Fixture();
        using var certificate = f.Write("mail");
        var error = Assert.Throws<AppException>(() => MailCertificatePemFiles.Read(f.Directory, () =>
        {
            if (change == "key") File.AppendAllText(f.PathFor("mail-key.pem"), "\n");
            if (change == "certificate") File.AppendAllText(f.PathFor("mail-crt.pem"), "\n");
            if (change == "add-chain") File.WriteAllText(f.PathFor("mail-chain-only.pem"), "\n");
            if (change == "new-group")
            {
                using var added = f.Write("new");
                File.SetLastWriteTimeUtc(f.PathFor("new-key.pem"), DateTime.UtcNow.AddMinutes(1));
            }
        }));
        Assert.Equal("mail.certificate_files_unreadable", error.MessageKey);
        Assert.Equal(409, error.HttpStatusCode);
    }

    [Fact]
    public void CertificateAndChainComeFromTheSelectedKeysFileGroup()
    {
        using var f = new Fixture();
        using var first = f.Write("first");
        using var second = f.Write("second");
        File.SetLastWriteTimeUtc(f.PathFor("first-key.pem"), DateTime.UtcNow.AddMinutes(1));
        File.SetLastWriteTimeUtc(f.PathFor("second-crt.pem"), DateTime.UtcNow.AddMinutes(2));
        File.WriteAllText(f.PathFor("second-chain.pem"), second.ExportCertificatePem());
        var pair = MailService.ReadPemPair(f.Directory);
        using var leaf = X509Certificate2.CreateFromPem(pair.CertificatePem, pair.PrivateKeyPem);
        Assert.Equal(first.Thumbprint, leaf.Thumbprint);
        Assert.Single(MailService.ExtractCertificateBlocks(pair.CertificatePem));
    }

    [Fact]
    public void UnrelatedChainOnlyFileIsNotIncluded()
    {
        using var f = new Fixture();
        using var leaf = f.Write("chosen");
        using var unrelated = f.Write("unrelated");
        File.SetLastWriteTimeUtc(f.PathFor("chosen-key.pem"), DateTime.UtcNow.AddMinutes(1));
        File.SetLastWriteTimeUtc(f.PathFor("chosen-crt.pem"), DateTime.UtcNow.AddMinutes(1));
        File.WriteAllText(f.PathFor("unrelated-chain-only.pem"), unrelated.ExportCertificatePem());
        var pair = MailService.ReadPemPair(f.Directory);
        Assert.Single(MailService.ExtractCertificateBlocks(pair.CertificatePem));
    }

    [Theory, InlineData("mismatched-key"), InlineData("partial-key"), InlineData("partial-crt"), InlineData("partial-chain"), InlineData("locked-key"),
        InlineData("missing-key"), InlineData("missing-crt"), InlineData("empty-crt")]
    public void IncompleteOrLockedGroupReturnsAnActionableErrorWithoutExposingPrivateMaterial(string problem)
    {
        using var f = new Fixture();
        using var certificate = f.Write("mail");
        var originalKey = File.ReadAllText(f.PathFor("mail-key.pem"));
        using var wrongKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        if (problem == "mismatched-key") File.WriteAllText(f.PathFor("mail-key.pem"), wrongKey.ExportPkcs8PrivateKeyPem());
        if (problem == "partial-key") File.WriteAllText(f.PathFor("mail-key.pem"), "-----BEGIN PRIVATE KEY-----\npartial-secret");
        if (problem == "partial-crt") File.WriteAllText(f.PathFor("mail-crt.pem"), "-----BEGIN CERTIFICATE-----\npartial");
        if (problem == "partial-chain") File.WriteAllText(f.PathFor("mail-chain.pem"), certificate.ExportCertificatePem() + "\n-----BEGIN CERTIFICATE-----\npartial");
        if (problem == "missing-key") File.Delete(f.PathFor("mail-key.pem"));
        if (problem == "missing-crt") File.Delete(f.PathFor("mail-crt.pem"));
        if (problem == "empty-crt") File.WriteAllText(f.PathFor("mail-crt.pem"), "");
        using var locked = problem == "locked-key" ? new FileStream(f.PathFor("mail-key.pem"), FileMode.Open, FileAccess.Read, FileShare.None) : null;
        var error = Assert.Throws<AppException>(() => MailService.ReadPemPair(f.Directory));
        Assert.Equal(409, error.HttpStatusCode);
        Assert.DoesNotContain(originalKey, error.ToString());
        Assert.DoesNotContain("partial-secret", error.ToString());
        Assert.DoesNotContain(f.Directory, error.ToString());
    }

    [Fact]
    public void IncompleteNewestGroupCannotBorrowAnOlderGroupsCertificate()
    {
        using var f = new Fixture();
        using var certificate = f.Write("old");
        File.Copy(f.PathFor("old-key.pem"), f.PathFor("new-key.pem"));
        File.SetLastWriteTimeUtc(f.PathFor("new-key.pem"), DateTime.UtcNow.AddMinutes(1));
        var error = Assert.Throws<AppException>(() => MailService.ReadPemPair(f.Directory));
        Assert.Equal("mail.certificate_files_unreadable", error.MessageKey);
    }

    [Theory, InlineData(false), InlineData(true)]
    public void FullAndIntermediateOnlyChainsRetainAllIntermediateCertificates(bool intermediateOnly)
    {
        using var f = new Fixture();
        using var chain = new SslImportTestChain("mail.example.test");
        using var key = chain.Leaf.GetECDsaPrivateKey();
        File.WriteAllText(f.PathFor("mail-key.pem"), key!.ExportPkcs8PrivateKeyPem());
        File.WriteAllText(f.PathFor("mail-crt.pem"), chain.Leaf.ExportCertificatePem());
        File.WriteAllText(f.PathFor(intermediateOnly ? "mail-chain-only.pem" : "mail-chain.pem"),
            (intermediateOnly ? "" : chain.Leaf.ExportCertificatePem() + "\n") + chain.Lower.ExportCertificatePem() + "\n" + chain.Upper.ExportCertificatePem());
        var pair = MailService.ReadPemPair(f.Directory);
        var parsed = new X509Certificate2Collection();
        try
        {
            parsed.ImportFromPem(pair.CertificatePem);
            Assert.Equal(new[] { chain.Leaf.Thumbprint, chain.Lower.Thumbprint, chain.Upper.Thumbprint }, parsed.Cast<X509Certificate2>().Select(c => c.Thumbprint));
        }
        finally { foreach (var certificate in parsed) certificate.Dispose(); }
    }

    [Theory, InlineData("leaf"), InlineData("full-chain"), InlineData("chain-only"), InlineData("duplicate-leaf")]
    public void SupportedPemLayoutsKeepTheLeafAndItsMatchingKey(string layout)
    {
        using var f = new Fixture();
        using var leaf = f.Write("custom-name");
        if (layout == "full-chain")
        {
            File.Move(f.PathFor("custom-name-crt.pem"), f.PathFor("custom-name-chain.pem"));
        }
        if (layout == "chain-only") File.WriteAllText(f.PathFor("custom-name-chain-only.pem"), "\n");
        if (layout == "duplicate-leaf") File.WriteAllText(f.PathFor("custom-name-chain.pem"), leaf.ExportCertificatePem().Replace("\n", "\r\n"));
        var pair = MailService.ReadPemPair(f.Directory);
        using var parsed = X509Certificate2.CreateFromPem(pair.CertificatePem, pair.PrivateKeyPem);
        Assert.Equal(leaf.Thumbprint, parsed.Thumbprint);
        Assert.Single(MailService.ExtractCertificateBlocks(pair.CertificatePem));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly BulutAlani area = new();
        internal string Directory { get; }
        internal Fixture() { Directory = area.DosyaDeposu.EnsureDirectory("pem"); }
        internal string PathFor(string name) => Path.Combine(Directory, name);
        internal X509Certificate2 Write(string prefix)
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request = new CertificateRequest("CN=mail.example.test", key, HashAlgorithmName.SHA256);
            var san = new SubjectAlternativeNameBuilder(); san.AddDnsName("mail.example.test"); request.CertificateExtensions.Add(san.Build());
            var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
            File.WriteAllText(PathFor(prefix + "-crt.pem"), certificate.ExportCertificatePem());
            File.WriteAllText(PathFor(prefix + "-key.pem"), key.ExportPkcs8PrivateKeyPem());
            return certificate;
        }
        public void Dispose() => area.Dispose();
    }
}
