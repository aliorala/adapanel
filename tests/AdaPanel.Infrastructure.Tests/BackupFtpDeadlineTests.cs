using System.Net;
using System.Net.Sockets;
using AdaPanel.Infrastructure.Backup;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class BackupFtpDeadlineTests
{
    [Fact]
    public async Task Asynchronous_request_is_aborted_when_server_never_sends_greeting()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
#pragma warning disable SYSLIB0014
        var request = (FtpWebRequest)WebRequest.Create($"ftp://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/backup");
#pragma warning restore SYSLIB0014
        request.Method = WebRequestMethods.Ftp.MakeDirectory;
        var pending = request.GetResponseAsync();
        using var connection = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(10));
        using var deadline = new FtpRequestDeadline(request, TimeSpan.FromMilliseconds(100), CancellationToken.None);
        await Assert.ThrowsAsync<WebException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        var error = Assert.Throws<WebException>(deadline.ThrowIfExpired);
        Assert.Equal(WebExceptionStatus.Timeout, error.Status);
    }

    [Fact]
    public async Task Cancellation_during_retry_wait_does_not_start_another_attempt()
    {
        using var cancellation = new CancellationTokenSource();
        var attempts = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BackupFtpRetry.RunAsync(_ =>
        {
            attempts++;
            cancellation.Cancel();
            throw new WebException("Connection lost", WebExceptionStatus.ConnectionClosed);
        }, cancellation.Token, TimeSpan.Zero));
        Assert.Equal(1, attempts);
    }
}
