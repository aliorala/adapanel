using System.Net;
using System.Text.Json;
using AdaPanel.Core.Common;
using AdaPanel.Infrastructure.Ssl;
using AdaPanel.Infrastructure.Support;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class AcmeHttpPreflightTests
{
    [Fact]
    public async Task OnlyWinAcmeChildGetsInvariantEnvironment()
    {
        const string setting = "DOTNET_SYSTEM_GLOBALIZATION_INVARIANT";
        var parent = Environment.GetEnvironmentVariable(setting);
        var root = Directory.CreateTempSubdirectory("adapanel-culture-");
        try
        {
            var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            var child = Path.Combine(root.FullName, "WACS.exe");
            File.Copy(cmd, child);
            var env = new Dictionary<string, string> { [setting] = "0" };
            foreach (var (exe, value) in new[] { (child, "1"), (cmd, "0") })
            {
                var result = await ProcessRunner.RunAsync(exe, "/d /c echo %" + setting + "%", environmentVariables: env);
                Assert.True(result.Success, result.StandardError);
                Assert.Equal(value, result.StandardOutput.Trim());
            }
            Assert.Equal(parent, Environment.GetEnvironmentVariable(setting));
        }
        finally { root.Delete(true); }
    }

    private const string PathName = "/.well-known/acme-challenge/token";
    private const string Body = "independent-file-content";

    [Fact]
    public async Task HttpSysServesOnlyTheExactExtensionlessProbe()
    {
        using var port = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        port.Start();
        var endpoint = new Uri($"http://127.0.0.1:{((IPEndPoint)port.LocalEndpoint).Port}{AcmeHttpPreflight.ChallengePath}");
        port.Stop();
        using var server = new HttpListener();
        server.Prefixes.Add(endpoint.AbsoluteUri);
        server.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var serving = AcmeHttpPreflight.ServeAsync(server, endpoint.Host, PathName, Body, timeout.Token);
        try
        {
            using var client = new HttpClient(new HttpClientHandler { UseProxy = false });
            Assert.Equal(Body, await client.GetStringAsync(new Uri(endpoint, "token"), timeout.Token));
            using var wrong = await client.GetAsync(new Uri(endpoint, "token.txt"), timeout.Token);
            Assert.Equal(HttpStatusCode.NotFound, wrong.StatusCode);
        }
        finally { timeout.Cancel(); server.Close(); await serving; }
    }

    [Fact]
    public async Task ConcurrentPanelOperationsCannotTakeTheListenerBetweenProbeAndIssue()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = AcmeHttpPreflight.ExclusiveAsync(async () =>
        {
            entered.SetResult();
            await release.Task;
            return new CommandResult { ExitCode = 0 };
        }, default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var cancelled = new CancellationTokenSource();
        try
        {
            var next = AcmeHttpPreflight.ExclusiveAsync(() => throw new InvalidOperationException("Overlapping listener"), cancelled.Token);
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => next);
        }
        finally { release.SetResult(); await first; }
        Assert.True((await AcmeHttpPreflight.ExclusiveAsync(() => Task.FromResult(new CommandResult { ExitCode = 0 }), default)).Success);
    }

    [Theory]
    [InlineData(404, Body)]
    [InlineData(200, "default site")]
    [InlineData(200, "independent-file-content\n")]
    public async Task PublicStatusAndExactBodyMustBothMatch(int code, string content)
    {
        var handler = new MeasurementStub((_, _) => new { status = "finished", statusCode = code, rawBody = content });
        using var client = handler.Client();
        var error = await Assert.ThrowsAsync<AppException>(() => AcmeHttpPreflight.VerifyExternalAsync(client, "web.example.test", PathName, Body, default));
        Assert.Contains("web.example.test", error.Details);
        Assert.Contains($"HTTP {code}", error.Details);
    }

    [Fact]
    public async Task CheckFollowsPublicHttpsChallengeWithoutSendingExpectedValue()
    {
        var handler = new MeasurementStub((poll, _) => poll == 1 ? (object)new
        {
            status = "finished", statusCode = 301, headers = new { location = "https://web.example.test" + PathName }
        } : new { status = "finished", statusCode = 200, rawBody = Body });
        using var client = handler.Client();
        await AcmeHttpPreflight.VerifyExternalAsync(client, "web.example.test", PathName, Body, default);
        Assert.Equal(["HTTP", "HTTPS"], handler.Protocols);
    }

    [Fact]
    public async Task FailedProviderCannotReportSuccess()
    {
        using var client = new MeasurementStub((_, _) => new { status = "failed", statusCode = 200, rawBody = Body }).Client();
        await Assert.ThrowsAsync<AppException>(() => AcmeHttpPreflight.VerifyExternalAsync(client, "web.example.test", PathName, Body, default));
    }

    private sealed class MeasurementStub(Func<int, string, object> reply) : HttpMessageHandler
    {
        internal List<string> Protocols { get; } = [];
        internal HttpClient Client() => new(this) { BaseAddress = new Uri("https://probe.invalid/v1/") };
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.Method == HttpMethod.Post)
            {
                var json = await request.Content!.ReadAsStringAsync(token);
                Assert.DoesNotContain(Body, json);
                using var payload = JsonDocument.Parse(json);
                var options = payload.RootElement.GetProperty("measurementOptions");
                Assert.Equal(PathName, options.GetProperty("request").GetProperty("path").GetString());
                Assert.Equal("GET", options.GetProperty("request").GetProperty("method").GetString());
                Protocols.Add(options.GetProperty("protocol").GetString()!);
                return new(HttpStatusCode.Accepted) { Content = new StringContent("{\"id\":\"fixture-id\"}") };
            }
            Assert.Equal("/v1/measurements/fixture-id", request.RequestUri!.AbsolutePath);
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new
            {
                status = "finished", results = new[] { new { result = reply(Protocols.Count, PathName) } }
            })) };
        }
    }
}
