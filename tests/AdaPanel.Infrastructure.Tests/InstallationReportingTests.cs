using System.Net;
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
    public async Task DefaultOffDoesNotCreateIdOrCallNetwork()
    {
        using var f = new Fixture();
        Assert.False((await f.Service.GetAsync(default)).Enabled);
        await f.Service.ReportIfDueAsync(default);
        Assert.Equal(0, f.Handler.Calls);
        Assert.False(File.Exists(f.Store.ResolvePath(InstallationReportingService.StatePath)));
    }

    [Fact]
    public async Task DailyReportSurvivesServiceRestartAndDeduplicatesConcurrentTicks()
    {
        using var f = new Fixture(); await f.Enable();
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
    [InlineData("{\"accepted\":true,\"schemaVersion\":\"1\"}")]
    [InlineData("not-json")]
    public async Task InvalidAcknowledgementNeverReportsSuccess(string response)
    {
        using var f = new Fixture(); await f.Enable(); f.Handler.Response = response;
        await f.Service.ReportIfDueAsync(default);
        var state = await f.Service.GetAsync(default);
        Assert.Null(state.LastAcceptedAtUtc);
        Assert.Equal("invalid_response", state.LastError);
        await f.Service.ReportIfDueAsync(default); Assert.Equal(1, f.Handler.Calls);
    }

    [Fact]
    public async Task NetworkFailureDoesNotSendAgainUntilNextDay()
    {
        using var f = new Fixture(); await f.Enable(); f.Handler.Fail = true;
        await f.Service.ReportIfDueAsync(default);
        Assert.Equal("network", (await f.Service.GetAsync(default)).LastError);
        f.Now = f.Now.AddHours(23); await f.Service.ReportIfDueAsync(default); Assert.Equal(1, f.Handler.Calls);
        f.Now = f.Now.AddHours(1); f.Handler.Fail = false; await f.Service.ReportIfDueAsync(default);
        Assert.Equal(f.Now, (await f.Service.GetAsync(default)).LastAcceptedAtUtc);
    }

    [Fact]
    public async Task DisableStopsFutureReportsAndReenablePreservesIdentityAndDailyLimit()
    {
        using var f = new Fixture(); await f.Enable(); await f.Service.ReportIfDueAsync(default);
        var first = f.Handler.Body;
        var state = await f.Service.GetAsync(default);
        await f.Service.UpdateAsync(new() { Enabled = false, Revision = state.Revision }, default);
        await f.Service.ReportIfDueAsync(default); Assert.Equal(1, f.Handler.Calls);
        await f.Enable(); await f.Service.ReportIfDueAsync(default); Assert.Equal(1, f.Handler.Calls);
        f.Now = f.Now.AddHours(24); await f.Service.ReportIfDueAsync(default);
        Assert.Equal(first, f.Handler.Body);
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
        Assert.Equal(new[] { "schemaVersion", "installationToken", "panelVersion", "location", "datacenter" }, body.RootElement.EnumerateObject().Select(x => x.Name));
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

    private sealed class Fixture : IDisposable
    {
        public readonly string Root = Path.Combine(Path.GetTempPath(), "adapanel-reporting-" + Guid.NewGuid().ToString("N"));
        public JsonFileStore Store { get; }
        public Handler Handler { get; } = new();
        public Factory Factory { get; }
        public InstallationReportingService Service;
        public DateTimeOffset Now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
        public Fixture()
        {
            Store = new JsonFileStore(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = Root }));
            Factory = new Factory(Handler); Service = new(Store, Factory) { Clock = () => Now };
        }
        public async Task Enable() => await Service.UpdateAsync(new() { Enabled = true, Revision = (await Service.GetAsync(default)).Revision }, default);
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
    private sealed class Factory(Handler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) { Assert.Equal(InstallationReportingService.ClientName, name); return new HttpClient(handler, false); }
    }
    private sealed class Handler : HttpMessageHandler
    {
        public int Calls; public string? Body, Url; public bool Fail;
        public string Response = "{\"accepted\":true,\"schemaVersion\":1}";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++; Body = await request.Content!.ReadAsStringAsync(ct); Url = request.RequestUri!.ToString();
            if (Fail) throw new HttpRequestException("do not log this response");
            return new(HttpStatusCode.OK) { Content = new StringContent(Response) };
        }
    }
}
