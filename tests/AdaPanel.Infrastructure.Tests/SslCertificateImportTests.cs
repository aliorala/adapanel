using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Runtime.InteropServices;
using AdaPanel.Infrastructure.Ssl;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class SslCertificateImportTests
{
    [Fact]
    public void NativeMemoryStoreRepairsKeylessCertificateAndKeepsItsWorkingKey()
    {
        using var chain = new SslImportTestChain();
        using var publicOnly = new X509Certificate2(chain.Leaf.RawData);
        // A temporary user key (deleted on disposal), and a memory-only certificate store.
        // No machine store or trusted root is changed by this native integration check.
        using var keyed = new X509Certificate2(chain.Leaf.Export(X509ContentType.Pfx), "",
            X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
        var handle = CertOpenStore(new IntPtr(2), 0, IntPtr.Zero, 0, IntPtr.Zero);
        Assert.NotEqual(IntPtr.Zero, handle);
        try
        {
            using var store = new X509Store(handle);
            store.Add(publicOnly);
            SslCertificateImport.PersistToStore(store, keyed);
            SslCertificateImport.PersistToStore(store, publicOnly);
            var certificates = store.Certificates;
            try
            {
                var installed = Assert.Single(certificates.Cast<X509Certificate2>());
                Assert.True(installed.HasPrivateKey);
                using var key = installed.GetECDsaPrivateKey();
                using var publicKey = chain.Leaf.GetECDsaPublicKey();
                var data = RandomNumberGenerator.GetBytes(32);
                Assert.True(publicKey!.VerifyData(data, key!.SignData(data, HashAlgorithmName.SHA256), HashAlgorithmName.SHA256));
            }
            finally { foreach (var certificate in certificates) certificate.Dispose(); }
        }
        finally { CertCloseStore(handle, 0); }
    }

    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern IntPtr CertOpenStore(IntPtr provider, uint encoding, IntPtr cryptProvider, uint flags, IntPtr parameter);

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CertCloseStore(IntPtr store, uint flags);

    [Fact]
    public void ImportsOnlySignedIntermediatesWithoutRootsUnrelatedCertificatesOrPrivateKeys()
    {
        using var chain = new SslImportTestChain();
        using var unrelated = chain.AlternateIssuer("CN=unrelated");
        using var impostorKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var impostor = chain.AlternateIssuer(chain.Lower.Subject, key: impostorKey);
        var pfx = new X509Certificate2Collection(new[] { chain.Root, unrelated, impostor, chain.Upper, chain.Leaf, chain.Lower })
            .Export(X509ContentType.Pfx, "password")!;
        var result = SslCertificateImport.ReadIntermediates(pfx, "password", chain.Leaf);
        Assert.Equal(2, result.Count);
        using var lower = new X509Certificate2(result[0]);
        using var upper = new X509Certificate2(result[1]);
        Assert.Equal(chain.Lower.Thumbprint, lower.Thumbprint);
        Assert.Equal(chain.Upper.Thumbprint, upper.Thumbprint);
        Assert.False(lower.HasPrivateKey); Assert.False(upper.HasPrivateKey);
    }

    [Theory, InlineData(false, true), InlineData(true, false)]
    public void MatchingIssuerKeyAndNameStillRequireCaAndCertificateSigningUsage(bool ca, bool signing)
    {
        using var chain = new SslImportTestChain();
        using var wrongPurpose = chain.AlternateIssuer(chain.Lower.Subject, ca, signing);
        var pfx = new X509Certificate2Collection(new[] { chain.Leaf, wrongPurpose }).Export(X509ContentType.Pfx, "password")!;
        Assert.Empty(SslCertificateImport.ReadIntermediates(pfx, "password", chain.Leaf));
    }

    [Fact]
    public void MissingUpperChainDoesNotInventACertificateOrTrustAnchor()
    {
        using var chain = new SslImportTestChain();
        var pfx = new X509Certificate2Collection(new[] { chain.Leaf, chain.Lower }).Export(X509ContentType.Pfx, "password")!;
        using var selected = new X509Certificate2(Assert.Single(SslCertificateImport.ReadIntermediates(pfx, "password", chain.Leaf)));
        Assert.Equal(chain.Lower.Thumbprint, selected.Thumbprint);
    }

    [Fact]
    public void SelfSignedLeafDoesNotBecomeAnIntermediate()
    {
        using var chain = new SslImportTestChain();
        Assert.Empty(SslCertificateImport.ReadIntermediates(chain.Root.Export(X509ContentType.Pfx, "password"), "password", chain.Root));
    }

    [Theory, InlineData("wrong-password"), InlineData("damaged")]
    public void InvalidBundleFailsWithoutExposingPassword(string scenario)
    {
        using var chain = new SslImportTestChain();
        var raw = scenario == "damaged" ? new byte[] { 1, 2, 3 } : chain.Export();
        var error = Assert.ThrowsAny<CryptographicException>(() => SslCertificateImport.ReadIntermediates(raw, "secret-wrong-password", chain.Leaf));
        Assert.DoesNotContain("secret-wrong-password", error.ToString());
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public void StoreDecisionRepairsKeylessCopyButNeverDowngradesAKeyedCopy(bool incomingKey, bool storedKey, bool write)
    {
        using var chain = new SslImportTestChain();
        using var publicOnly = new X509Certificate2(chain.Leaf.RawData);
        Assert.Equal(write, SslCertificateImport.NeedsStoreWrite(incomingKey ? chain.Leaf : publicOnly,
            [storedKey ? chain.Leaf : publicOnly]));
        Assert.True(SslCertificateImport.NeedsStoreWrite(incomingKey ? chain.Leaf : publicOnly, [chain.Root]));
        Assert.True(SslCertificateImport.NeedsStoreWrite(incomingKey ? chain.Leaf : publicOnly, []));
    }
}

internal sealed class SslImportTestChain : IDisposable
{
    private readonly ECDsa rootKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly ECDsa upperKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly ECDsa lowerKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly ECDsa leafKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    internal X509Certificate2 Root { get; }
    internal X509Certificate2 Upper { get; }
    internal X509Certificate2 Lower { get; }
    internal X509Certificate2 Leaf { get; }

    internal SslImportTestChain(string host = "import.example.test")
    {
        var now = DateTimeOffset.UtcNow;
        Root = Request("CN=isolated root " + Guid.NewGuid().ToString("N"), rootKey, true, true)
            .CreateSelfSigned(now.AddDays(-2), now.AddDays(90));
        Upper = Issue(Request("CN=upper intermediate", upperKey, true, true), Root, upperKey);
        Lower = Issue(Request("CN=lower intermediate", lowerKey, true, true), Upper, lowerKey);
        var request = Request("CN=" + host, leafKey, false, false);
        var san = new SubjectAlternativeNameBuilder(); san.AddDnsName(host); request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, false));
        Leaf = Issue(request, Lower, leafKey);
    }

    internal X509Certificate2 AlternateIssuer(string subject, bool ca = true, bool signing = true, ECDsa? key = null) =>
        Issue(Request(subject, key ?? lowerKey, ca, signing), Root, key ?? lowerKey);

    internal byte[] Export()
    {
        using var root = new X509Certificate2(Root.RawData);
        using var upper = new X509Certificate2(Upper.RawData);
        using var lower = new X509Certificate2(Lower.RawData);
        return new X509Certificate2Collection(new[] { root, upper, lower, Leaf }).Export(X509ContentType.Pfx, "secret-pfx-password")!;
    }

    private static CertificateRequest Request(string subject, ECDsa key, bool ca, bool signing)
    {
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(ca, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(signing ? X509KeyUsageFlags.KeyCertSign : X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        return request;
    }

    private static X509Certificate2 Issue(CertificateRequest request, X509Certificate2 issuer, ECDsa key)
    {
        using var certificate = request.Create(issuer, DateTimeOffset.UtcNow.AddDays(-1), issuer.NotAfter.ToUniversalTime().AddDays(-1), RandomNumberGenerator.GetBytes(16));
        return certificate.CopyWithPrivateKey(key);
    }

    public void Dispose()
    {
        Leaf.Dispose(); Lower.Dispose(); Upper.Dispose(); Root.Dispose();
        leafKey.Dispose(); lowerKey.Dispose(); upperKey.Dispose(); rootKey.Dispose();
    }
}
