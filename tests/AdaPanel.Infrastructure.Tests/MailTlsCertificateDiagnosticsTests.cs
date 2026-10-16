using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using AdaPanel.Core.Dtos.Mail;
using AdaPanel.Infrastructure.Mail;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class MailTlsCertificateDiagnosticsTests
{
    [Theory]
    [InlineData("cn-only", "Certificate SAN does not match")]
    [InlineData("wrong-san", "Certificate SAN does not match")]
    [InlineData("expired", "Certificate has expired")]
    [InlineData("future", "Certificate is not yet valid")]
    [InlineData("broken-san", "Certificate SAN could not be read")]
    public void AdditionalCertificateChecksExplainRejectionEvenWithoutPlatformErrors(string scenario, string reason)
    {
        using var certificate = CreateCertificate(scenario);
        var result = new MailTlsEndpointDto();
        Assert.False(MailTlsProbe.ValidateCertificate(result, "mail.example.test", null, certificate, null, SslPolicyErrors.None));
        Assert.Equal("fail", result.Status);
        Assert.Contains(reason, result.Detail);
        Assert.DoesNotContain("TLS validation: None", result.Detail);
        Assert.Equal(certificate.Thumbprint, result.Thumbprint);
    }

    [Fact]
    public void MissingCertificateHasAnExplicitFailure()
    {
        var result = new MailTlsEndpointDto();
        Assert.False(MailTlsProbe.ValidateCertificate(result, "mail.example.test", null, null, null, SslPolicyErrors.RemoteCertificateNotAvailable));
        Assert.Equal("fail", result.Status);
        Assert.Contains("No certificate was presented", result.Detail);
    }

    [Fact]
    public void PlatformTrustErrorsCannotBeOverriddenByMatchingSanAndThumbprint()
    {
        using var certificate = CreateCertificate("valid");
        var result = new MailTlsEndpointDto();
        Assert.False(MailTlsProbe.ValidateCertificate(result, "mail.example.test", certificate.Thumbprint, certificate, null, SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.Equal("fail", result.Status);
        Assert.Contains("RemoteCertificateChainErrors", result.Detail);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ExpectedCertificateIsRequiredAfterPlatformValidation(bool matching)
    {
        using var certificate = CreateCertificate("valid");
        var result = new MailTlsEndpointDto();
        // This unit test supplies the platform outcome; it does not establish system trust.
        Assert.Equal(matching, MailTlsProbe.ValidateCertificate(result, "mail.example.test", matching ? certificate.Thumbprint.ToLowerInvariant() : "different", certificate, null, SslPolicyErrors.None));
        Assert.Equal(matching ? "ok" : "fail", result.Status);
        if (matching) Assert.Empty(result.Detail);
        else Assert.Contains("different certificate", result.Detail);
    }

    [Fact]
    public void ReportsAllIndependentFailures()
    {
        using var certificate = CreateCertificate("expired");
        var result = new MailTlsEndpointDto();
        Assert.False(MailTlsProbe.ValidateCertificate(result, "other.example.test", "different", certificate, null, SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.Contains("RemoteCertificateChainErrors", result.Detail);
        Assert.Contains("Certificate SAN does not match", result.Detail);
        Assert.Contains("Certificate has expired", result.Detail);
        Assert.Contains("different certificate", result.Detail);
    }

    private static X509Certificate2 CreateCertificate(string scenario)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=mail.example.test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        if (scenario == "broken-san") request.CertificateExtensions.Add(new X509Extension("2.5.29.17", [0x30, 0x01, 0xff], false));
        else if (scenario != "cn-only")
        {
            var san = new SubjectAlternativeNameBuilder();
            san.AddDnsName(scenario == "wrong-san" ? "other.example.test" : "mail.example.test");
            request.CertificateExtensions.Add(san.Build());
        }
        var start = DateTimeOffset.UtcNow.AddDays(scenario == "future" ? 1 : -2);
        var end = DateTimeOffset.UtcNow.AddDays(scenario == "expired" ? -1 : 2);
        using var generated = request.CreateSelfSigned(start, end);
        return new X509Certificate2(generated.Export(X509ContentType.Cert));
    }
}
