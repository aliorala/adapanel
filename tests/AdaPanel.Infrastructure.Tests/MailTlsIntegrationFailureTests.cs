using System.Net;
using System.Net.Sockets;
using System.Text;
using AdaPanel.Infrastructure.Mail;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class MailTlsIntegrationFailureTests
{
    [Theory]
    [InlineData(false, 421)]
    [InlineData(true, 454)]
    public async Task SmtpRejectionReturnsFailedEndpointWithoutLeakingServerReply(bool rejectStartTls, int status)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var tcp = await listener.AcceptTcpClientAsync(timeout.Token);
            using var stream = tcp.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true);
            using var writer = new StreamWriter(stream, Encoding.ASCII, 1024, true) { AutoFlush = true, NewLine = "\r\n" };
            if (rejectStartTls)
            {
                await writer.WriteLineAsync("220 localhost ESMTP");
                Assert.StartsWith("EHLO", await reader.ReadLineAsync(timeout.Token));
                await writer.WriteLineAsync("250-localhost\r\n250 STARTTLS");
                Assert.Equal("STARTTLS", await reader.ReadLineAsync(timeout.Token));
            }
            await writer.WriteLineAsync($"{status} private-server-diagnostic");
        }, timeout.Token);

        var result = await MailTlsProbe.ProbeCoreAsync("127.0.0.1", port, null, true, timeout.Token);
        await server;
        Assert.Equal("fail", result.Status);
        Assert.Equal(port, result.Port);
        Assert.Equal("STARTTLS", result.Protocol);
        Assert.Contains(status.ToString(), result.Detail);
        Assert.DoesNotContain("private-server-diagnostic", result.Detail);
        Assert.Empty(result.Thumbprint);
    }

    [Fact]
    public async Task CallerCancellationStillPropagates()
    {
        using var timeout = new CancellationTokenSource();
        timeout.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            MailTlsProbe.ProbeCoreAsync("127.0.0.1", 587, null, true, timeout.Token));
    }
}
