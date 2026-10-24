using System.Net;
using System.Reflection;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Firewall;
using AdaPanel.Core.Dtos.PanelAccess;
using AdaPanel.Core.Dtos.Proxy;
using AdaPanel.Core.Dtos.Settings;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Dtos.Ssl;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Health;
using AdaPanel.Infrastructure.Iis;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class PanelAccessRecoveryTests
{
    [Theory]
    [InlineData("iis.unavailable", 503)]
    [InlineData("sites.site_not_found", 500)]
    [InlineData("access.denied", 403)]
    [InlineData("component.not_found", 404)]
    public async Task ReadFailureCannotRecreateAnExistingSite(string key, int status)
    {
        using var f = new Fixture { ReadError = new AppException(key, status, status) };
        var result = await f.Service.EnsureSiteAsync(Fixture.Host, f.Area.Kok, f.Token);
        Assert.Equal("fail", result.Step.Status);
        Assert.Equal(new[] { "read" }, f.Calls);
        Assert.False(Directory.Exists(Path.Combine(f.Area.Kok, "adapanel-panel-access")));
    }

    [Fact]
    public async Task ConfirmedAbsenceCanCreateSiteWithoutProvisioningTenantServices()
    {
        using var f = new Fixture { ReadError = new AppException("sites.site_not_found", 404, 404) };
        f.ClearErrorOnCreate = true;
        var result = await f.Service.EnsureSiteAsync(Fixture.Host, f.Area.Kok, f.Token);
        Assert.Equal("ok", result.Step.Status);
        Assert.Equal(new[] { "read", "create", "read" }, f.Calls);
        Assert.False(f.Created!.ProvisionDefaultServices);
        Assert.Equal(80, f.Created.Port);
    }

    [Theory]
    [InlineData(8080)]
    [InlineData(0)]
    public async Task NewHttpBindingPreservesWorkingOldHttps(int currentHttpPort)
    {
        using var f = new Fixture();
        f.Site = Fixture.SiteWith(currentHttpPort, "old.example.test", 443);
        var old = f.Site.Bindings.Last();
        var result = await f.Service.EnsureSiteAsync(Fixture.Host, f.Area.Kok, f.Token);
        Assert.Equal("ok", result.Step.Status);
        Assert.Contains(old, result.Site!.Bindings);
        Assert.Contains(result.Site.Bindings, b => b.HostName == Fixture.Host && b.Port == 80);
        Assert.Equal(new[] { "read", "add", "read" }, f.Calls);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task UnappliedBindingOrStartCannotBeSuccessful(bool persistBinding, bool persistStart)
    {
        using var f = new Fixture { PersistBinding = persistBinding, PersistStart = persistStart };
        f.Site = Fixture.SiteWith(8080, "old.example.test", 443, "Stopped");
        var result = await f.Service.EnsureSiteAsync(Fixture.Host, f.Area.Kok, f.Token);
        Assert.Equal("fail", result.Step.Status);
    }

    [Fact]
    public async Task StoppedExistingSiteIsStartedAndReadBack()
    {
        using var f = new Fixture();
        f.Site = Fixture.SiteWith(80, Fixture.Host, 443, "Stopped");
        var result = await f.Service.EnsureSiteAsync(Fixture.Host, f.Area.Kok, f.Token);
        Assert.Equal("ok", result.Step.Status);
        Assert.Equal(new[] { "read", "start", "read" }, f.Calls);
    }

    [Theory]
    [InlineData("old.example.test", 443, "warn")]
    [InlineData("new.example.test", 8443, "warn")]
    [InlineData("new.example.test", 443, "ok")]
    public async Task HttpsMustMatchBothHostAndPort(string httpsHost, int port, string expected)
    {
        using var f = new Fixture { Site = Fixture.SiteWith(80, httpsHost, port) };
        Assert.Equal(expected, (await f.Service.CheckSiteAsync(Fixture.Host, f.Token)).Step.Status);
    }

    [Theory]
    [InlineData("renewal")]
    [InlineData("certificate")]
    [InlineData("proxy")]
    [InlineData("missing-rule")]
    [InlineData("wrong-destination")]
    public async Task FailedPrerequisiteStopsDependentWorkAndKeepsSavedHost(string failure)
    {
        using var f = new Fixture { Failure = failure };
        await f.Area.DosyaDeposu.WriteAsync("config/panel-preferences.json", new PanelPreferencesDto { PanelHostName = "old.example.test" });
        var result = await f.Setup();
        Assert.Equal("fail", result.OverallStatus);
        Assert.Equal(6, result.Steps.Count);
        Assert.Equal("unknown", result.Steps.Single(s => s.Key == "external").Status);
        Assert.DoesNotContain("http", f.Calls);
        if (failure is "renewal" or "missing-rule") Assert.DoesNotContain("certificate", f.Calls);
        if (failure is "renewal" or "missing-rule" or "certificate") Assert.DoesNotContain("proxy", f.Calls);
        if (f.Calls.Contains("certificate")) Assert.True(f.Calls.IndexOf("renewal") < f.Calls.IndexOf("certificate"));
        var saved = await f.Area.DosyaDeposu.ReadAsync("config/panel-preferences.json", new PanelPreferencesDto());
        Assert.Equal("old.example.test", saved.PanelHostName);
    }

    [Theory]
    [InlineData("read")]
    [InlineData("firewall")]
    [InlineData("renewal")]
    [InlineData("certificate")]
    [InlineData("proxy")]
    public async Task CancellationStopsAtTheInterruptedStage(string stage)
    {
        using var f = new Fixture { CancelAt = stage };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Setup());
        Assert.Equal(stage, f.Calls.Last());
    }

    [Theory]
    [InlineData("ok", "ok", "ok", true)]
    [InlineData("warn", "ok", "ok", true)]
    [InlineData("ok", "warn", "ok", true)]
    [InlineData("ok", "fail", "ok", false)]
    [InlineData("warn", "unknown", "ok", false)]
    [InlineData("ok", "ok", "fail", false)]
    public async Task SavedHostFollowsLiveResultAndPreservesOtherPreferences(
        string dns, string certificate, string external, bool save)
    {
        using var f = new Fixture();
        await f.Area.DosyaDeposu.WriteAsync("config/panel-preferences.json", new PanelPreferencesDto
            { PanelHostName = "old.example.test", PanelName = "Keep this name", DefaultLanguage = "en" });
        var report = new PanelAccessStatusDto { HostName = Fixture.Host, Steps =
        [new() { Key = "dns", Status = dns }, new() { Key = "site", Status = "ok" },
         new() { Key = "certificate", Status = "ok" }, new() { Key = "renewal", Status = "ok" },
         new() { Key = "proxy", Status = "ok" }, new() { Key = "external", Status = external }] };
        var live = new PanelAccessStepDto { Key = "certificate", Status = certificate, Detail = "Live result" };
        var result = await f.Service.CompleteSetupAsync(report, live,
            new PanelPublicAccessService.CertificateInfo { Source = "manual", DaysLeft = 60 }, f.Token);
        Assert.Same(live, result.Steps.Single(step => step.Key == "certificate"));
        if (certificate == "fail" || external == "fail") Assert.Equal("fail", result.OverallStatus);
        var stored = await f.Area.DosyaDeposu.ReadAsync("config/panel-preferences.json", new PanelPreferencesDto());
        Assert.Equal(save ? Fixture.Host : "old.example.test", stored.PanelHostName);
        Assert.Equal("Keep this name", stored.PanelName);
        Assert.Equal("en", stored.DefaultLanguage);
    }

    [Theory]
    [InlineData(200, "ok")]
    [InlineData(204, "ok")]
    [InlineData(302, "fail")]
    [InlineData(401, "fail")]
    [InlineData(403, "fail")]
    [InlineData(404, "fail")]
    [InlineData(500, "fail")]
    public async Task ExternalHttpErrorCannotBeHealthy(int code, string expected)
    {
        using var f = new Fixture { HttpCode = code };
        Assert.Equal(expected, (await f.Service.ProbeExternalAsync(Fixture.Host, f.Token)).Status);
    }

    [Fact]
    public async Task ExternalProbePropagatesCallerCancellation()
    {
        using var f = new Fixture { CancelAt = "http" };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Service.ProbeExternalAsync(Fixture.Host, f.Token));
    }

    [Fact]
    public async Task ConcurrentSetupWaitCanBeCancelledWithoutStartingAnotherSetup()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstPreferences = Fake<IPanelPreferencesService>((_, args) => Hold((CancellationToken)args[0]!));
        async Task<PanelPreferencesDto> Hold(CancellationToken token)
        { entered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); return new(); }
        var secondReads = 0;
        var secondPreferences = Fake<IPanelPreferencesService>((_, _) =>
        { secondReads++; throw new InvalidOperationException("Second setup must not enter."); });
        PanelPublicAccessService Service(IPanelPreferencesService preferences) => new(null!, null!, null!, preferences,
            null!, null!, null!, null!, null!, NullLogger<PanelPublicAccessService>.Instance);
        using var firstCancel = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        var first = Service(firstPreferences).SetupAsync(new() { HostName = Fixture.Host }, firstCancel.Token);
        try
        {
            await entered.Task.WaitAsync(deadline.Token);
            using var secondCancel = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            var second = Service(secondPreferences).SetupAsync(new() { HostName = Fixture.Host }, secondCancel.Token);
            Assert.False(second.IsCompleted);
            secondCancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
            Assert.Equal(0, secondReads);
        }
        finally { firstCancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first); }
        // An interrupted owner must release the gate for later requests.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Service(secondPreferences).SetupAsync(new() { HostName = Fixture.Host }, deadline.Token));
        Assert.Equal(1, secondReads);
    }

    private sealed class Fixture : IDisposable
    {
        public const string Host = "new.example.test";
        public readonly BulutAlani Area = new();
        private readonly CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(30));
        public CancellationToken Token => cancellation.Token;
        public readonly List<string> Calls = [];
        public Exception? ReadError;
        public bool ClearErrorOnCreate, PersistBinding = true, PersistStart = true;
        public string Failure = "", CancelAt = "";
        public int HttpCode = 200;
        public CreateSiteRequest? Created;
        public SiteDto Site = SiteWith(80, Host, 443);
        public readonly PanelPublicAccessService Service;
        private readonly HttpClient client;
        public Fixture()
        {
            client = new HttpClient(new Handler(this));
            var sites = Fake<IIisSiteService>((method, args) =>
            {
                switch (method.Name)
                {
                    case nameof(IIisSiteService.GetSiteAsync):
                        Enter("read"); if (ReadError is not null) throw ReadError; return Task.FromResult(Site);
                    case nameof(IIisSiteService.CreateSiteAsync):
                        Enter("create"); Created = (CreateSiteRequest)args[0]!;
                        // Exercise the real IIS name guard, not a permissive fixture substitute.
                        typeof(WebAdministrationSiteService).GetMethod("EnsureSiteNameIsNotReserved", BindingFlags.Static | BindingFlags.NonPublic)!
                            .Invoke(null, [Created.Name, Created.IsManagedInfrastructure]);
                        if (ClearErrorOnCreate) ReadError = null;
                        return Task.FromResult(Site);
                    case nameof(IIisSiteService.AddBindingAsync):
                        Enter("add"); var binding = (SiteBindingRequest)args[1]!;
                        if (PersistBinding) Site = Copy(Site.State, Site.Bindings.Append(new SiteBindingDto
                            { HostName = binding.HostName, Protocol = binding.Protocol, Port = binding.Port }).ToArray());
                        return Task.FromResult(Site);
                    case nameof(IIisSiteService.StartSiteAsync):
                        Enter("start"); if (PersistStart) Site = Copy("Started", Site.Bindings); return Task.FromResult(Site);
                    default: throw new InvalidOperationException("Unexpected site mutation: " + method.Name);
                }
            });
            var proxy = Fake<IReverseProxyService>((method, args) =>
            {
                if (method.Name == nameof(IReverseProxyService.AddRewriteRuleAsync))
                {
                    Enter("renewal"); var rule = (AddRewriteRuleRequest)args[1]!;
                    return Task.FromResult(new ReverseProxySiteConfigurationDto { RewriteRules = Failure == "missing-rule" ? [] :
                        [new RewriteRuleDto { Name = rule.Name, MatchPattern = rule.MatchPattern, RewriteUrl = rule.RewriteUrl, StopProcessing = rule.StopProcessing }] });
                }
                Assert.Equal(nameof(IReverseProxyService.EnableProxyAsync), method.Name); Enter("proxy");
                var request = (EnableReverseProxyRequest)args[1]!;
                return Task.FromResult(new ReverseProxySiteConfigurationDto { IsArrEnabled = true, IsProxyEnabled = true,
                    DestinationUrl = Failure == "wrong-destination" ? "http://localhost:1" : request.DestinationUrl });
            });
            var ssl = Fake<ISslService>((_, _) => { Enter("certificate"); return Task.FromResult(new SslCertificateDto
                { Source = "letsencrypt", NotAfterUtc = DateTime.UtcNow.AddDays(60) }); });
            var firewall = Fake<IFirewallService>((_, _) => { Enter("firewall"); return Task.FromResult(new FirewallRuleDto()); });
            var preferences = Fake<IPanelPreferencesService>((_, _) => Task.FromResult(new PanelPreferencesDto()));
            Service = new PanelPublicAccessService(sites, proxy, ssl, preferences, Area.DosyaDeposu, firewall,
                new Factory(client), null!, null!, NullLogger<PanelPublicAccessService>.Instance);
        }
        private SiteDto Copy(string state, IReadOnlyCollection<SiteBindingDto> bindings) => new()
            { Name = Site.Name, State = state, PhysicalPath = Area.SiteKoku, Bindings = bindings };
        public static SiteDto SiteWith(int httpPort, string httpsHost, int httpsPort, string state = "Started") => new()
        {
            Name = "AdaPanel.PanelAccess", State = state, Bindings =
            [new() { HostName = Host, Protocol = "http", Port = httpPort }, new() { HostName = httpsHost, Protocol = "https", Port = httpsPort }]
        };
        private void Enter(string step)
        {
            Calls.Add(step);
            if (CancelAt == step) { cancellation.Cancel(); throw new OperationCanceledException(Token); }
            if (Failure == step) throw new IOException("Fixture step failed: " + step);
        }
        public Task<PanelAccessStatusDto> Setup() => Service.SetupAfterDnsAsync(
            new SetupPanelAccessRequest { HostName = Host, ContactEmail = "admin@example.test" },
            new PanelPreferencesDto { DefaultSiteRoot = Area.Kok },
            new PanelAccessStatusDto { HostName = Host, BackendUrl = "https://localhost:8890", Steps = [new() { Key = "dns", Status = "ok" }] }, Token);
        public void Dispose() { client.Dispose(); cancellation.Dispose(); Area.Dispose(); }
        private sealed class Handler(Fixture fixture) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            { fixture.Enter("http"); Assert.Equal($"https://{Host}/", request.RequestUri!.AbsoluteUri); return Task.FromResult(new HttpResponseMessage((HttpStatusCode)fixture.HttpCode)); }
        }
        private sealed class Factory(HttpClient client) : IHttpClientFactory
        { public HttpClient CreateClient(string name) { Assert.Equal("AdaPanel.LocalHealth", name); return client; } }
    }
    private static T Fake<T>(Func<MethodInfo, object?[], object?> handler) where T : class
    { var result = DispatchProxy.Create<T, Proxy>(); ((Proxy)(object)result).Handler = handler; return result; }
    public class Proxy : DispatchProxy
    { public Func<MethodInfo, object?[], object?> Handler = null!; protected override object? Invoke(MethodInfo? method, object?[]? args) => Handler(method!, args ?? []); }
}
