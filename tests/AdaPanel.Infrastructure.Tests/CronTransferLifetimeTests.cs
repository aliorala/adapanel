using System.Net;
using System.Net.Sockets;
using System.Text;
using AdaPanel.Infrastructure.Scheduler;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class CronTransferLifetimeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Deadline_covers_body_and_output_after_headers(bool blockedOutput)
    {
        await using var server = new ResponseServer(stall: !blockedOutput);
        using var client = Client();
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var output = new ProbeWriter(blockedOutput);
        using var errors = new StringWriter();
        var run = UrlTaskRunner.TekDenemeAsync(client, server.Address, output, errors, watchdog.Token,
            TimeSpan.FromSeconds(2));
        await output.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var (code, retry) = await run;
        Assert.Equal(UrlTaskRunner.SureAsimiKod, code);
        Assert.True(retry, "The attempt deadline, not the caller watchdog, must end the transfer.");
        Assert.False(watchdog.IsCancellationRequested);
        Assert.Contains("sure asildi", errors.ToString());
    }

    [Fact]
    public async Task Caller_cancellation_during_body_does_not_retry()
    {
        await using var server = new ResponseServer(stall: true);
        using var client = Client();
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var output = new ProbeWriter(false);
        using var errors = new StringWriter();
        var run = UrlTaskRunner.TekDenemeAsync(client, server.Address, output, errors, cancel.Token);
        await output.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancel.Cancel();
        var (code, retry) = await run;
        Assert.Equal(UrlTaskRunner.SureAsimiKod, code);
        Assert.False(retry);
        Assert.Contains("iptal edildi", errors.ToString());
    }

    [Fact]
    public async Task Premature_body_close_returns_receive_error_instead_of_throwing()
    {
        await using var server = new ResponseServer(truncated: true);
        using var client = Client();
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var output = new StringWriter();
        using var errors = new StringWriter();
        var (code, retry) = await UrlTaskRunner.TekDenemeAsync(client, server.Address, output, errors, cancel.Token);
        Assert.Equal(56, code);
        Assert.True(retry);
        Assert.NotEmpty(errors.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Output_failure_returns_write_error_without_repeating_http_request(bool failOnFlush)
    {
        await using var server = new ResponseServer();
        using var client = Client();
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var output = new FailingWriter(failOnFlush);
        using var errors = new StringWriter();
        var (code, retry) = await UrlTaskRunner.TekDenemeAsync(client, server.Address, output, errors, cancel.Token);
        Assert.Equal(23, code);
        Assert.False(retry);
    }

    [Fact]
    public async Task Complete_body_keeps_success_and_content()
    {
        await using var server = new ResponseServer();
        using var client = Client();
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var output = new StringWriter();
        using var errors = new StringWriter();
        var (code, retry) = await UrlTaskRunner.TekDenemeAsync(client, server.Address, output, errors, cancel.Token);
        Assert.Equal(0, code);
        Assert.False(retry);
        Assert.Equal("x", output.ToString());
        Assert.Empty(errors.ToString());
    }

    private static HttpClient Client() => new(new SocketsHttpHandler { UseProxy = false })
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    private sealed class ProbeWriter(bool block) : StringWriter
    {
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async Task WriteAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            if (block) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            else await base.WriteAsync(buffer, cancellationToken);
        }
    }

    private sealed class FailingWriter(bool flush) : StringWriter
    {
        public override Task WriteAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default)
            => flush ? base.WriteAsync(buffer, cancellationToken) : throw new IOException("fixture output unavailable");
        public override Task FlushAsync(CancellationToken cancellationToken)
            => flush ? throw new IOException("fixture flush unavailable") : Task.CompletedTask;
    }

    private sealed class ResponseServer : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stop = new();
        private readonly Task serving;
        public Uri Address { get; }
        public ResponseServer(bool stall = false, bool truncated = false)
        {
            listener.Start();
            Address = new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/cron");
            serving = Serve(stall, truncated);
        }
        private async Task Serve(bool stall, bool truncated)
        {
            using var socket = await listener.AcceptTcpClientAsync(stop.Token);
            await using var stream = socket.GetStream();
            using var reader = new StreamReader(stream, leaveOpen: true);
            while (!string.IsNullOrEmpty(await reader.ReadLineAsync(stop.Token))) { }
            var length = stall || truncated ? 100 : 1;
            await stream.WriteAsync(Encoding.ASCII.GetBytes(
                $"HTTP/1.1 200 OK\r\nContent-Length: {length}\r\nConnection: close\r\n\r\nx"), stop.Token);
            await stream.FlushAsync(stop.Token);
            if (stall) await Task.Delay(Timeout.InfiniteTimeSpan, stop.Token);
        }
        public async ValueTask DisposeAsync()
        {
            stop.Cancel();
            listener.Stop();
            try { await serving; } catch (OperationCanceledException) { }
            finally { stop.Dispose(); }
        }
    }
}
