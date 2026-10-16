using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using AdaPanel.Infrastructure.Ssl;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class SslCertificateSelectionTests
{
    [Theory]
    [InlineData("unrelated.test", "mail.example.test", "mail.example.test", true)]
    [InlineData("mail.example.test.attacker.test", null, "mail.example.test", false)]
    [InlineData("mail.example.test", null, "mail.example.test", false)]
    [InlineData("mail.example.test", "other.example.test", "mail.example.test", false)]
    [InlineData("unrelated.test", "*.example.test", "mail.example.test", true)]
    [InlineData("unrelated.test", "*.example.test", "example.test", false)]
    [InlineData("unrelated.test", "*.example.test", "nested.mail.example.test", false)]
    [InlineData("unrelated.test", "*.example.test", "*.example.test", true)]
    [InlineData("unrelated.test", "mail.example.test", "*.example.test", false)]
    public void StoreSelectionUsesSanIdentityInsteadOfCnSubstrings(string cn, string? san, string host, bool expected)
    {
        using var certificate = Certificate(cn, san, -1, 30);
        Assert.Equal(expected, SslCertificateSelection.MatchesHost(certificate, host));
    }

    [Fact]
    public void SelectionConsidersAllStoresAndPrefersUsableCurrentCertificates()
    {
        using var old = Certificate("old", "mail.example.test", -1, 2);
        using var renewed = Certificate("new", "mail.example.test", -1, 90);
        using var future = Certificate("future", "mail.example.test", 2, 180);
        using var expired = Certificate("expired", "mail.example.test", -30, -1);
        using var publicOnly = new X509Certificate2(renewed.RawData);
        Assert.Same(renewed, SslCertificateSelection.Select([old, expired, publicOnly, future, renewed], "mail.example.test"));
        Assert.Same(old, SslCertificateSelection.Select([publicOnly, old], "mail.example.test"));
        // Expired material is still discoverable for status reporting; selection is not a trust verdict.
        Assert.Same(expired, SslCertificateSelection.Select([expired], "mail.example.test"));
        Assert.Null(SslCertificateSelection.Select([renewed], "other.example.test"));
    }

    private static X509Certificate2 Certificate(string cn, string? san, int before, int after)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=" + cn, key, HashAlgorithmName.SHA256);
        if (san is not null)
        {
            var names = new SubjectAlternativeNameBuilder();
            names.AddDnsName(san);
            request.CertificateExtensions.Add(names.Build());
        }
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(before), DateTimeOffset.UtcNow.AddDays(after));
    }
}
