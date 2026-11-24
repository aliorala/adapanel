using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using AdaPanel.Infrastructure.Support;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class CertificateHostMatcherTests
{
    private static X509Certificate2 Sertifika(params string[] adlar)
    {
        using var anahtar = RSA.Create(2048);
        var istek = new CertificateRequest($"CN={adlar[0]}", anahtar, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        foreach (var ad in adlar) san.AddDnsName(ad);
        istek.CertificateExtensions.Add(san.Build());
        return istek.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
    }

    /// <summary>Kullanıcı Unicode yazar, sertifikada punycode vardır (ve tersi): aynı addır.</summary>
    [Theory]
    [InlineData("xn--bcher-kva.example", "bücher.example")]
    [InlineData("bücher.example", "xn--bcher-kva.example")]
    [InlineData("BÜCHER.example", "xn--bcher-kva.example")]
    [InlineData("*.xn--bcher-kva.example", "shop.bücher.example")]
    public void Unicode_ad_punycode_SAN_ile_eslesir(string san, string host)
    {
        using var sertifika = Sertifika(san);
        Assert.True(CertificateHostMatcher.Matches(sertifika, host));
    }

    [Theory]
    [InlineData("xn--bcher-kva.example", "bucher.example")]
    [InlineData("*.xn--bcher-kva.example", "a.b.bücher.example")]
    public void Farkli_ad_eslesmez(string san, string host)
    {
        using var sertifika = Sertifika(san);
        Assert.False(CertificateHostMatcher.Matches(sertifika, host));
    }
}
