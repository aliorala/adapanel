using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using AdaPanel.Core.Dtos.Ssl;
using AdaPanel.Infrastructure.Health;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class SiteHealthTlsProbeTests
{
    [Theory]
    [InlineData("localhost", -1, 60, false)]
    [InlineData("wrong.example.test", -1, 60, false)]
    [InlineData("localhost", 1, 60, false)]
    [InlineData("localhost", -2, -1, false)]
    [InlineData("localhost", -1, 60, true)]
    [InlineData("wrong.example.test", -1, 60, true)]
    [InlineData("localhost", 1, 60, true)]
    [InlineData("localhost", -2, -1, true)]
    public async Task LiveRejectedCertificateCannotBeGreenOrReplacedByStoredCertificate(
        string certificateHost, int startsInDays, int expiresInDays, bool panelAccess)
    {
        using var area = new BulutAlani();
        await area.DosyaDeposu.WriteAsync("config/ssl.json", new[]
        {
            new SslCertificateDto { SiteName = "site", HostName = "localhost", NotAfterUtc = DateTime.UtcNow.AddDays(200) }
        });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={certificateHost}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(certificateHost);
        request.CertificateExtensions.Add(san.Build());
        using var issuerKey = RSA.Create(2048);
        var issuerRequest = new CertificateRequest("CN=AdaPanel Test CA", issuerKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        issuerRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using var issuer = issuerRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-10), DateTimeOffset.UtcNow.AddDays(300));
        using var leaf = request.Create(issuer, DateTimeOffset.UtcNow.AddDays(startsInDays), DateTimeOffset.UtcNow.AddDays(expiresInDays), RandomNumberGenerator.GetBytes(16));
        // The panel used to reject self-signed leaves but accept an untrusted issuer.
        using var generated = panelAccess ? leaf.CopyWithPrivateKey(key)
            : request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(startsInDays), DateTimeOffset.UtcNow.AddDays(expiresInDays));
        using var certificate = new X509Certificate2(generated.Export(X509ContentType.Pfx), "", X509KeyStorageFlags.Exportable);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var tcp = await listener.AcceptTcpClientAsync(timeout.Token);
            using var tls = new SslStream(tcp.GetStream());
            try
            {
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = certificate,
                    EnabledSslProtocols = SslProtocols.Tls12
                }, timeout.Token);
                return true;
            }
            catch (Exception error) when (error is IOException or AuthenticationException) { return false; }
        }, timeout.Token);

        string status, detail;
        int? daysLeft;
        if (panelAccess)
        {
            var service = new PanelPublicAccessService(null!, null!, null!, null!, null!, null!, null!, null!, null!, null!);
            var result = await service.ProbeCertificateAsync("localhost", timeout.Token, port);
            status = result.Step.Status;
            detail = result.Step.Detail;
            daysLeft = result.Info.DaysLeft;
            Assert.False(result.Info.IsSelfSigned);
            Assert.Equal("manual", result.Info.Source);
        }
        else
        {
            var service = new SiteHealthService(null!, null!, null!, area.DosyaDeposu, null!);
            var result = await service.ProbeHttpsAsync("site", "localhost", "127.0.0.1", timeout.Token, port);
            status = result.Check.Status;
            detail = result.Check.Detail;
            daysLeft = result.DaysLeft;
            Assert.Contains(certificate.Subject, detail);
            Assert.DoesNotContain("panel kaydı", detail);
        }
        // Schannel may finish the server handshake before the client's validation callback.
        // The client must reject authentication, regardless of when the server sees the alert.
        await server;
        Assert.Equal("fail", status);
        Assert.Contains("RemoteCertificateChainErrors", detail);
        Assert.Contains(nameof(AuthenticationException), detail);
        Assert.NotNull(daysLeft);
        Assert.InRange(daysLeft!.Value, expiresInDays - 1, expiresInDays);
    }

    [Fact]
    public async Task CancellationDuringTlsHandshakeIsNotReportedAsBadCertificate()
    {
        using var area = new BulutAlani();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var accept = listener.AcceptTcpClientAsync(deadline.Token).AsTask();
        var service = new SiteHealthService(null!, null!, null!, area.DosyaDeposu, null!);
        var probe = service.ProbeHttpsAsync("site", "localhost", "127.0.0.1", cancelled.Token,
            ((IPEndPoint)listener.LocalEndpoint).Port);
        using var tcp = await accept;
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probe);
    }

    [Fact]
    public async Task HttpCallerCancellationPropagatesWhileIndependentTimeoutIsFailure()
    {
        using var cancelled = new CancellationTokenSource();
        using var handler = new CancelHandler(cancelled);
        using var client = new HttpClient(handler);
        var service = new SiteHealthService(null!, null!, null!, null!, new ClientFactory(client));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.ProbeHttpAsync("example.test", "127.0.0.1", cancelled.Token));

        handler.CancelCaller = false;
        var timeoutResult = await service.ProbeHttpAsync("example.test", "127.0.0.1", CancellationToken.None);
        Assert.Equal("fail", timeoutResult.Status);
    }

    private sealed class CancelHandler(CancellationTokenSource caller) : HttpMessageHandler
    {
        public bool CancelCaller { get; set; } = true;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (CancelCaller) caller.Cancel();
            return Task.FromException<HttpResponseMessage>(new OperationCanceledException(token));
        }
    }

    private sealed class ClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
}
