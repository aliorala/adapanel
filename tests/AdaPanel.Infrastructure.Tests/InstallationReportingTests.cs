using System.Net;
using System.Text;
using System.Text.Json;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Diagnostics;
using AdaPanel.Infrastructure.Diagnostics;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Support;
using Microsoft.Extensions.Options;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class InstallationReportingTests
{
    [Fact]
    public async Task FreshInstallationDefaultsOnAndCreatesStableIdOnlyWhenSending()
    {
        using var f = new Fixture();
        Assert.True((await f.Service.GetAsync(default)).Enabled);
        Assert.False(File.Exists(f.Store.ResolvePath(InstallationReportingService.StatePath)));
        await f.Service.ReportIfDueAsync(default);
        Assert.Equal(1, f.Handler.Calls);
        using var body = JsonDocument.Parse(f.Handler.Body!);
        Assert.Equal(64, body.RootElement.GetProperty("installationToken").GetString()!.Length);
        Assert.Null((await f.Service.GetAsync(default)).ConsentAtUtc);
        Assert.Equal(f.Now, (await f.Service.GetAsync(default)).LastAcceptedAtUtc);
        f.Service = new(f.Store, f.Factory) { Clock = () => f.Now };
        await f.Service.ReportIfDueAsync(default);
        Assert.Equal(1, f.Handler.Calls);
    }

    [Fact]
    public async Task ExistingDisabledDetailsPreferenceSurvivesUpgradeAndSendsOnlyVersion()
    {
        using var f = new Fixture();
        await f.Store.WriteAsync(InstallationReportingService.StatePath, new { enabled = false, token = new string('a', 64), revision = "previous" });
        f.Service = new(f.Store, f.Factory) { Clock = () => f.Now };
        Assert.False((await f.Service.GetAsync(default)).Enabled);
        await f.Service.ReportIfDueAsync(default);
        Assert.Equal(1, f.Handler.Calls);
        using var report = JsonDocument.Parse(f.Handler.Body!);
        Assert.False(report.RootElement.GetProperty("shareDetails").GetBoolean());
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{")]
    public async Task MalformedExistingStateCannotBecomeDefaultOn(string contents)
    {
        using var f = new Fixture();
        var path = f.Store.ResolvePath(InstallationReportingService.StatePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, contents);
        await Assert.ThrowsAnyAsync<JsonException>(() => f.Service.ReportIfDueAsync(default));
        Assert.Equal(0, f.Handler.Calls);
        Assert.Equal(contents, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task LegacyEnabledIdentityIsKeptButLocationAndDatacenterAreNotSent()
    {
        using var f = new Fixture();
        var token = new string('b', 64);
        await f.Store.WriteAsync(InstallationReportingService.StatePath, new {
            enabled = true, token, location = "old country/city", datacenter = "old provider",
            consentAtUtc = f.Now, revision = "legacy"
        });
        await f.Service.ReportIfDueAsync(default);
        using var body = JsonDocument.Parse(f.Handler.Body!);
        Assert.Equal(token, body.RootElement.GetProperty("installationToken").GetString());
        Assert.DoesNotContain("old", f.Handler.Body!);
        Assert.Empty((await f.Service.GetAsync(default)).Location);
        Assert.Empty((await f.Service.GetAsync(default)).Datacenter);
    }

    [Fact]
    public async Task DailyReportSurvivesServiceRestartAndDeduplicatesConcurrentTicks()
    {
        using var f = new Fixture();
        await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => f.Service.ReportIfDueAsync(default)));
        Assert.Equal(1, f.Handler.Calls);
        var token = JsonDocument.Parse(f.Handler.Body!).RootElement.GetProperty("installationToken").GetString();
        f.Service = new(f.Store, f.Factory) { Clock = () => f.Now };
        f.Now = f.Now.AddHours(23); await f.Service.ReportIfDueAsync(default);
        Assert.Equal(1, f.Handler.Calls);
        f.Now = f.Now.AddHours(1); await f.Service.ReportIfDueAsync(default);
        Assert.Equal(2, f.Handler.Calls);
        Assert.Equal(token, JsonDocument.Parse(f.Handler.Body!).RootElement.GetProperty("installationToken").GetString());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{\"accepted\":true,\"schemaVersion\":1}")]
    [InlineData("{\"accepted\":true,\"schemaVersion\":\"1\"}")]
    [InlineData("{\"accepted\":false,\"schemaVersion\":2}")]
    [InlineData("not-json")]
    public async Task InvalidAcknowledgementNeverReportsSuccess(string response)
    {
        using var f = new Fixture(); f.Handler.Response = response;
        await f.Service.ReportIfDueAsync(default);
        var state = await f.Service.GetAsync(default);
        Assert.Null(state.LastAcceptedAtUtc);
        Assert.Equal("invalid_response", state.LastError);
        await f.Service.ReportIfDueAsync(default); Assert.Equal(1, f.Handler.Calls);
    }

    [Fact]
    public async Task NetworkFailureUsesPersistedBoundedRetryThenReturnsToDailySuccess()
    {
        using var f = new Fixture(); f.Handler.Fail = true;
        foreach (var minutes in new[] { 5, 30, 120, 360, 360 })
        {
            var calls = f.Handler.Calls;
            await f.Service.ReportIfDueAsync(default);
            var state = await f.Service.GetAsync(default);
            Assert.Equal("network", state.LastError);
            Assert.Null(state.LastAcceptedAtUtc);
            Assert.Equal(f.Now.AddMinutes(minutes), state.NextAttemptAtUtc);
            f.Restart();
            f.Now = state.NextAttemptAtUtc!.Value.AddTicks(-1);
            await f.Service.ReportIfDueAsync(default);
            Assert.Equal(calls + 1, f.Handler.Calls);
            f.Now = state.NextAttemptAtUtc.Value;
        }
        f.Handler.Fail = false;
        await f.Service.ReportIfDueAsync(default);
        var accepted = await f.Service.GetAsync(default);
        Assert.Equal(f.Now, accepted.LastAcceptedAtUtc);
        Assert.Equal(f.Now.AddHours(24), accepted.NextAttemptAtUtc);
        Assert.Empty(accepted.LastError);
        // The next failure starts at five minutes again after a successful report.
        f.Now = f.Now.AddHours(24); f.Handler.Fail = true;
        await f.Service.ReportIfDueAsync(default);
        Assert.Equal(f.Now.AddMinutes(5), (await f.Service.GetAsync(default)).NextAttemptAtUtc);
    }

    [Fact]
    public async Task DetailsPreferenceChangesDoNotBypassDailyLimitAndPreserveIdentity()
    {
        using var f = new Fixture(); await f.Enable(); await f.Service.ReportIfDueAsync(default);
        var token = JsonDocument.Parse(f.Handler.Body!).RootElement.GetProperty("installationToken").GetString();
        var state = await f.Service.GetAsync(default);
        await f.Service.UpdateAsync(new() { Enabled = false, Revision = state.Revision }, default);
        await f.Service.ReportIfDueAsync(default); Assert.Equal(1, f.Handler.Calls);
        f.Now = f.Now.AddHours(24); await f.Service.ReportIfDueAsync(default);
        Assert.Equal(2, f.Handler.Calls);
        using var minimal = JsonDocument.Parse(f.Handler.Body!);
        Assert.False(minimal.RootElement.GetProperty("shareDetails").GetBoolean());
        Assert.Equal(token, minimal.RootElement.GetProperty("installationToken").GetString());
        await f.Enable(); await f.Service.ReportIfDueAsync(default); Assert.Equal(2, f.Handler.Calls);
        f.Now = f.Now.AddHours(24); await f.Service.ReportIfDueAsync(default);
        Assert.Equal(3, f.Handler.Calls);
        using var detailed = JsonDocument.Parse(f.Handler.Body!);
        Assert.True(detailed.RootElement.GetProperty("shareDetails").GetBoolean());
        Assert.Equal(token, detailed.RootElement.GetProperty("installationToken").GetString());
    }

    [Fact]
    public async Task StaleAdminRequestCannotReenableReporting()
    {
        using var f = new Fixture(); var initial = await f.Service.GetAsync(default); await f.Enable();
        var ex = await Assert.ThrowsAsync<AppException>(() => f.Service.UpdateAsync(new() { Enabled = true, Revision = initial.Revision }, default));
        Assert.Equal("installationReporting.changed", ex.Message);
    }

    [Fact]
    public async Task PayloadContainsOnlyDisclosedFieldsAndSettingsNeverExposeToken()
    {
        using var f = new Fixture(); await f.Enable(); await f.Service.ReportIfDueAsync(default);
        using var body = JsonDocument.Parse(f.Handler.Body!);
        Assert.Equal(new[] { "schemaVersion", "installationToken", "panelVersion", "shareDetails" }, body.RootElement.EnumerateObject().Select(x => x.Name));
        Assert.Equal(2, body.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.DoesNotContain("Token", JsonSerializer.Serialize(await f.Service.GetAsync(default)));
        Assert.Equal(InstallationReportingService.Endpoint, f.Handler.Url);
    }

    [Fact]
    public async Task CancellationDoesNotOverwriteLastSuccess()
    {
        using var f = new Fixture(); await f.Enable(); await f.Service.ReportIfDueAsync(default);
        var successful = f.Now; f.Now = f.Now.AddDays(1);
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Service.ReportIfDueAsync(cts.Token));
        Assert.Equal(successful, (await f.Service.GetAsync(default)).LastAcceptedAtUtc);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SavingPreferenceImmediatelyAttemptsDueReportAndReturnsDeliveryState(bool fail)
    {
        using var f = new Fixture(); f.Handler.Fail = fail;
        var state = await f.Service.UpdateAsync(new() { Enabled = false, Revision = "initial" }, default);
        Assert.Equal(1, f.Handler.Calls);
        Assert.False(state.Enabled);
        Assert.Equal(f.Now, state.LastAttemptAtUtc);
        Assert.Equal(fail ? null : f.Now, state.LastAcceptedAtUtc);
        Assert.Equal(fail ? "network" : "", state.LastError);
        Assert.Equal(f.Now.AddMinutes(fail ? 5 : 1440), state.NextAttemptAtUtc);
        using var report = JsonDocument.Parse(f.Handler.Body!);
        Assert.False(report.RootElement.GetProperty("shareDetails").GetBoolean());
    }

    [Fact]
    public async Task PreferenceChangesCannotBypassRetryAndKeepIdentity()
    {
        using var f = new Fixture(); f.Handler.Fail = true;
        await f.Enable();
        var token = JsonDocument.Parse(f.Handler.Body!).RootElement.GetProperty("installationToken").GetString();
        var next = (await f.Service.GetAsync(default)).NextAttemptAtUtc;
        for (var i = 0; i < 4; i++)
        {
            var state = await f.Service.GetAsync(default);
            await f.Service.UpdateAsync(new() { Enabled = !state.Enabled, Revision = state.Revision }, default);
        }
        Assert.Equal(1, f.Handler.Calls);
        Assert.Equal(next, (await f.Service.GetAsync(default)).NextAttemptAtUtc);
        f.Now = next!.Value; f.Handler.Fail = false;
        await f.Service.ReportIfDueAsync(default);
        Assert.Equal(token, JsonDocument.Parse(f.Handler.Body!).RootElement.GetProperty("installationToken").GetString());
    }

    [Fact]
    public async Task InterruptedNetworkAttemptPersistsRetryAndKeepsLastSuccess()
    {
        using var f = new Fixture(); await f.Enable(); var accepted = f.Now;
        f.Now = f.Now.AddDays(1);
        using var cts = new CancellationTokenSource();
        f.Handler.OnRequest = () => cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Service.ReportIfDueAsync(cts.Token));
        f.Restart();
        var state = await f.Service.GetAsync(default);
        Assert.Equal(accepted, state.LastAcceptedAtUtc);
        Assert.Equal("pending", state.LastError);
        Assert.Equal(f.Now.AddMinutes(5), state.NextAttemptAtUtc);
        await f.Service.ReportIfDueAsync(default);
        Assert.Equal(2, f.Handler.Calls);
    }

    [Fact]
    public async Task IndependentServiceInstancesShareDeliveryGate()
    {
        using var f = new Fixture();
        var other = new InstallationReportingService(f.Store, f.Factory) { Clock = () => f.Now };
        await Task.WhenAll(f.Service.ReportIfDueAsync(default), other.ReportIfDueAsync(default));
        Assert.Equal(1, f.Handler.Calls);
    }

    [Fact]
    public async Task LegacyFailedAttemptRetriesAfterFiveMinutesRatherThanBeingLockedForADay()
    {
        using var f = new Fixture();
        await f.Store.WriteAsync(InstallationReportingService.StatePath, new {
            enabled = false, token = new string('c', 64), revision = "legacy",
            lastAttemptAtUtc = f.Now.AddMinutes(-5), lastError = "http_400"
        });
        await f.Service.ReportIfDueAsync(default);
        Assert.Equal(1, f.Handler.Calls);
        Assert.Equal(f.Now, (await f.Service.GetAsync(default)).LastAcceptedAtUtc);
    }

    [Theory]
    [InlineData("short")]
    [InlineData("gggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggg")]
    public async Task InvalidExistingIdentityIsNeverSilentlyReplacedOrTransmitted(string token)
    {
        using var f = new Fixture();
        await f.Store.WriteAsync(InstallationReportingService.StatePath, new { enabled = false, token, revision = "existing" });
        await f.Service.ReportIfDueAsync(default);
        Assert.Equal(0, f.Handler.Calls);
        Assert.Equal("invalid_identity", (await f.Service.GetAsync(default)).LastError);
        using var stored = JsonDocument.Parse(await File.ReadAllTextAsync(f.Store.ResolvePath(InstallationReportingService.StatePath)));
        Assert.Equal(token, stored.RootElement.GetProperty("token").GetString());
    }

    [Fact]
    public async Task HttpFailureCannotBecomeAcceptedEvenWithAnAcceptedResponseBody()
    {
        using var f = new Fixture(); f.Handler.Status = HttpStatusCode.ServiceUnavailable;
        var state = await f.Service.UpdateAsync(new() { Enabled = true, Revision = "initial" }, default);
        Assert.Equal("http_503", state.LastError);
        Assert.Null(state.LastAcceptedAtUtc);
        Assert.Equal(f.Now.AddMinutes(5), state.NextAttemptAtUtc);
    }

    [Fact]
    public async Task ReportHasExactUtf8ContentLengthBeforeTransportRatherThanChunkedJson()
    {
        using var f = new Fixture();
        await f.Service.ReportIfDueAsync(default);
        Assert.Equal(1, f.Handler.Calls);
        Assert.Equal("application/json; charset=utf-8", f.Handler.ContentType);
        Assert.Equal((long)Encoding.UTF8.GetByteCount(f.Handler.Body!), f.Handler.ContentLength);
        Assert.True(f.Handler.ContentLength > 0);
        Assert.False(f.Handler.Chunked);
        using var report = JsonDocument.Parse(f.Handler.Body!);
        Assert.Equal(2, report.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.True(report.RootElement.GetProperty("shareDetails").GetBoolean());
        Assert.Equal(64, report.RootElement.GetProperty("installationToken").GetString()!.Length);
    }

    [Fact]
    public async Task DetailsOffSkipsProviderAndOmitsPublicIp()
    {
        using var f = new Fixture();
        await f.Store.WriteAsync(InstallationReportingService.StatePath, new { enabled = false, token = new string('a',64), revision = "old" });
        await f.Service.ReportIfDueAsync(default);
        Assert.Equal(0, f.Provider.Calls);
        using var body = JsonDocument.Parse(f.Handler.Body!);
        Assert.Equal(4, body.RootElement.EnumerateObject().Count());
        Assert.False(body.RootElement.TryGetProperty("publicIp", out _));
    }

    [Fact]
    public async Task PublicIpIsAutomaticOptionalAndStillUsesExactBufferedLength()
    {
        using var f = new Fixture(); f.Provider.Response = "{\"success\":true,\"ip\":\"8.8.8.8\"}";
        await f.Service.ReportIfDueAsync(default);
        using var body = JsonDocument.Parse(f.Handler.Body!);
        Assert.Equal("8.8.8.8", body.RootElement.GetProperty("publicIp").GetString());
        Assert.Equal(InstallationReportingService.PublicIpEndpoint, f.Provider.Url);
        Assert.Equal((long)Encoding.UTF8.GetByteCount(f.Handler.Body!), f.Handler.ContentLength);
        Assert.Equal(f.Now, (await f.Service.GetAsync(default)).LastAcceptedAtUtc);
        await f.Enable(); await f.Service.ReportIfDueAsync(default);
        Assert.Equal(1, f.Provider.Calls);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("192.168.1.10")]
    [InlineData("100.64.0.1")]
    [InlineData("2001:db8::1")]
    [InlineData("2002:7f00:1::")]
    [InlineData("2002:a9fe:a9fe::")]
    [InlineData("::ffff:8.8.8.8")]
    [InlineData("8.8.8.8/path")]
    [InlineData("8.8.8.8%1")]
    public async Task InvalidOrPrivateProviderIpIsOmittedWithoutBlockingHeartbeat(string ip)
    {
        using var f = new Fixture(); f.Provider.Response = JsonSerializer.Serialize(new { success=true, ip });
        await f.Service.ReportIfDueAsync(default);
        using var body = JsonDocument.Parse(f.Handler.Body!);
        Assert.False(body.RootElement.TryGetProperty("publicIp",out _));
        Assert.Equal(f.Now,(await f.Service.GetAsync(default)).LastAcceptedAtUtc);
    }

    [Theory]
    [InlineData("network")]
    [InlineData("invalid")]
    [InlineData("oversized")]
    [InlineData("timeout")]
    public async Task ProviderFailureNeverBlocksHeartbeat(string mode)
    {
        using var f = new Fixture(); f.Provider.Mode = mode;
        await f.Service.ReportIfDueAsync(default);
        Assert.Equal(1,f.Handler.Calls);
        Assert.Equal(f.Now,(await f.Service.GetAsync(default)).LastAcceptedAtUtc);
    }

    private sealed class Fixture : IDisposable
    {
        public readonly string Root = Path.Combine(Path.GetTempPath(), "adapanel-reporting-" + Guid.NewGuid().ToString("N"));
        public JsonFileStore Store { get; }
        public Handler Handler { get; } = new();
        public ProviderHandler Provider { get; } = new();
        public Factory Factory { get; }
        public InstallationReportingService Service;
        public DateTimeOffset Now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
        public Fixture()
        {
            Store = new JsonFileStore(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = Root }));
            Factory = new Factory(Handler, Provider); Service = new(Store, Factory) { Clock = () => Now };
        }
        public async Task Enable() => await Service.UpdateAsync(new() { Enabled = true, Revision = (await Service.GetAsync(default)).Revision }, default);
        public void Restart() => Service = new(Store, Factory) { Clock = () => Now };
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
    private sealed class Factory(Handler handler, ProviderHandler provider) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) {
            if (name == InstallationReportingService.PublicIpClientName) return new HttpClient(provider, false);
            Assert.Equal(InstallationReportingService.ClientName, name); return new HttpClient(handler, false);
        }
    }
    private sealed class ProviderHandler : HttpMessageHandler
    {
        public int Calls; public string? Url; public string Mode="";
        public string Response="{\"success\":false}";
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++; Url=request.RequestUri!.ToString();
            if (Mode=="network") throw new HttpRequestException("provider unavailable");
            if (Mode=="timeout") throw new TaskCanceledException("provider timeout");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content=new StringContent(Mode=="invalid"?"{":Mode=="oversized"?new string('x',4097):Response) });
        }
    }
    private sealed class Handler : HttpMessageHandler
    {
        public int Calls; public string? Body, Url; public bool Fail;
        public long? ContentLength;
        public string? ContentType;
        public bool Chunked;
        public string Response = "{\"accepted\":true,\"schemaVersion\":2}";
        public HttpStatusCode Status = HttpStatusCode.OK;
        public Action? OnRequest;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            // Capture framing before reading/serializing the body, just as a real transport does.
            ContentLength = request.Content!.Headers.ContentLength;
            ContentType = request.Content.Headers.ContentType?.ToString();
            Chunked = request.Headers.TransferEncodingChunked == true;
            Calls++; Body = await request.Content.ReadAsStringAsync(ct); Url = request.RequestUri!.ToString();
            OnRequest?.Invoke(); ct.ThrowIfCancellationRequested();
            if (Fail) throw new HttpRequestException("do not log this response");
            return new(Status) { Content = new StringContent(Response) };
        }
    }
}
