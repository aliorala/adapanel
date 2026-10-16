using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using AdaPanel.Core.Common;
using AdaPanel.Infrastructure.Mail;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class MailTlsIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealTlsHandshakeRejectsSelfSignedCertificates(bool startTls)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder(); san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(2));
        using var certificate = new X509Certificate2(generated.Export(X509ContentType.Pfx), "", X509KeyStorageFlags.Exportable);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        string serverFailure = "";
        var server = Task.Run(async () =>
        {
            using var tcp = await listener.AcceptTcpClientAsync(timeout.Token);
            var stream = tcp.GetStream();
            if (startTls)
            {
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true);
                using var writer = new StreamWriter(stream, Encoding.ASCII, 1024, true) { AutoFlush = true, NewLine = "\r\n" };
                await writer.WriteLineAsync("220 localhost ESMTP");
                Assert.StartsWith("EHLO", await reader.ReadLineAsync(timeout.Token));
                await writer.WriteLineAsync("250-localhost\r\n250 STARTTLS");
                Assert.Equal("STARTTLS", await reader.ReadLineAsync(timeout.Token));
                await writer.WriteLineAsync("220 Ready");
            }
            using var tls = new SslStream(stream);
            try { await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate }, timeout.Token); }
            catch (Exception error) when (error is IOException or AuthenticationException) { serverFailure = error.Message; }
        }, timeout.Token);
        var result = await MailTlsProbe.ProbeCoreAsync("127.0.0.1", port, certificate.Thumbprint, startTls, timeout.Token);
        Assert.Equal("fail", result.Status);
        await server;
        Assert.True(certificate.Thumbprint == result.Thumbprint, result.Detail + " | " + serverFailure);
        Assert.Contains("RemoteCertificateChainErrors", result.Detail);
    }

    [Fact]
    public void SharedOrDuplicateSniRecordsNeverSelectAnArbitraryCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=mail.example.test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder(); san.AddDnsName("mail.example.test");
        request.CertificateExtensions.Add(san.Build());
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(2));
        using var shared = JsonDocument.Parse("{\"id\":\"shared\",\"subjectAlternativeNames\":{\"mail.example.test\":true,\"other.example.test\":true}}");
        Assert.Throws<AppException>(() => MailService.SelectCertificateId([shared.RootElement], "mail.example.test", certificate));
        using var single = JsonDocument.Parse("{\"id\":\"target\",\"subjectAlternativeNames\":[\"mail.example.test\"]}");
        Assert.Equal("target", MailService.SelectCertificateId([single.RootElement], "mail.example.test", certificate));
        Assert.Throws<AppException>(() => MailService.SelectCertificateId([single.RootElement, single.RootElement], "mail.example.test", certificate));
    }

    [Fact]
    public void ExistingWinAcmeCacheSecretsAreDecodedWithoutRewritingConfiguration()
    {
        const string password = "fixture-pfx-password";
        Assert.Equal(password, MailCertificateStore.DecodeCachePassword("clear-" + password));
        Assert.Equal(password, MailCertificateStore.DecodeCachePassword(Convert.ToBase64String(Encoding.UTF8.GetBytes(password))));
        var protectedBytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(password), null, DataProtectionScope.LocalMachine);
        Assert.Equal(password, MailCertificateStore.DecodeCachePassword("enc-" + Convert.ToBase64String(protectedBytes)));
        Assert.Throws<FormatException>(() => MailCertificateStore.DecodeCachePassword("vault://unsupported"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PfxPrivateKeyIsExportedWithoutChangingTheInstalledWindowsKeyPolicy(bool elliptic)
    {
        using var rsa = RSA.Create(2048);
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = elliptic ? new CertificateRequest("CN=mail.example.test", ec, HashAlgorithmName.SHA256)
            : new CertificateRequest("CN=mail.example.test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(2));
        var archive = certificate.Export(X509ContentType.Pfx, "");
        try
        {
            var key = MailCertificateStore.ExportArchiveKey(archive);
            using var pair = X509Certificate2.CreateFromPem(certificate.ExportCertificatePem(), key);
            Assert.True(pair.HasPrivateKey);
            Assert.Equal(certificate.Thumbprint, pair.Thumbprint);
        }
        finally { CryptographicOperations.ZeroMemory(archive); }
    }
}
