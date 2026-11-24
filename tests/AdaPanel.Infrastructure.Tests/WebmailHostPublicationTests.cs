using System.Net;
using System.Text.Json;
using AdaPanel.Core.Dtos.Mail;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Mail;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class WebmailHostPublicationTests
{
    [Fact]
    public async Task TwoServicesFinishingTlsKeepBothHostsAndLatestPreferences()
    {
        using var f = new WebmailPublicationFixture();
        await f.Initialize();
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Secure = async host =>
        {
            if (host == "webmail.first.test") { firstEntered.SetResult(); await releaseFirst.Task; }
            return true;
        };
        var first = f.Service().SecureWebmailForDomainAsync("first.test", "");
        try
        {
            await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await f.Service().SecureWebmailForDomainAsync("second.test", "").WaitAsync(TimeSpan.FromSeconds(10));
            await f.Store.UpdateAsync("config/webmail.json", new Dictionary<string, object>(), latest =>
            { latest["customSetting"] = "updated"; latest["port"] = 9900; return latest; });
        }
        finally
        {
            releaseFirst.TrySetResult();
            await first.WaitAsync(TimeSpan.FromSeconds(10));
        }
        using var state = await f.State();
        Assert.Equal(new[] { "webmail.first.test", "webmail.original.test", "webmail.second.test" }, Hosts(state));
        Assert.Equal("updated", state.RootElement.GetProperty("customSetting").GetString());
        Assert.Equal(9900, state.RootElement.GetProperty("port").GetInt32());
    }

    [Fact]
    public async Task DomainOpenAndSslCallbackKeepBothSuccessfulHosts()
    {
        using var f = new WebmailPublicationFixture();
        await f.Initialize();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Secure = async host => { entered.SetResult(); await release.Task; return true; };
        var callback = f.Service().SecureWebmailForDomainAsync("first.test", "");
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            f.Bindings.Add(new() { Protocol = "https", Port = 443, HostName = "webmail.second.test" });
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var opened = await f.Service().OpenAsync("panel.test", new WebmailOpenRequest { Domain = "second.test" }, timeout.Token);
            Assert.Equal("https://webmail.second.test/", opened.Url);
        }
        finally
        {
            release.TrySetResult();
            await callback.WaitAsync(TimeSpan.FromSeconds(10));
        }
        using var state = await f.State();
        Assert.Equal(new[] { "webmail.first.test", "webmail.original.test", "webmail.second.test" }, Hosts(state));
    }

    [Fact]
    public async Task FailedTlsNeverPublishesHost()
    {
        using var f = new WebmailPublicationFixture();
        await f.Initialize();
        f.Secure = _ => Task.FromResult(false);
        await f.Service().SecureWebmailForDomainAsync("failed.test", "");
        using var state = await f.State();
        Assert.Equal(new[] { "webmail.original.test" }, Hosts(state));
    }

    private static string[] Hosts(JsonDocument state) => state.RootElement.GetProperty("securedHosts")
        .EnumerateArray().Select(v => v.GetString()!).OrderBy(v => v, StringComparer.Ordinal).ToArray();
}

internal sealed class WebmailPublicationFixture : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ada-webmail-publication-" + Guid.NewGuid().ToString("N"));
    public JsonFileStore Store { get; }
    public List<SiteBindingDto> Bindings { get; } = [new() { Protocol = "http", Port = 8899 }];
    public SiteDnsZoneDto Zone { get; } = new() { ZoneName = "alias.test", IsWindowsZonePresent = true, IsWindowsDnsAvailable = true };
    public List<UpsertDnsRecordRequest> DnsWrites { get; } = [];
    public List<string> SslCalls { get; } = [];
    public Func<string, Task<bool>> Secure = _ => Task.FromResult(true);
    private bool secured;
    public WebmailPublicationFixture() => Store = new(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = root }));
    private SiteDto Site() => new() { Name = "AdaPanel.Webmail", State = "Started", PhysicalPath = Path.Combine(root, "roundcube", "public_html"), Bindings = Bindings.ToArray() };
    public WebmailService Service()
    {
        var iis = WebmailDomainRoutingTests.InterfaceProxy.Make<IIisSiteService>((method, args) => method.Name switch
        {
            "GetSiteAsync" => Task.FromResult(Site()),
            "GetSitesAsync" => Task.FromResult<IReadOnlyCollection<SiteDto>>([new() { Name = "owner", Bindings = [new() { HostName = "alias.test" }] }]),
            "AddBindingAsync" => Add((SiteBindingRequest)args![1]!),
            _ => throw new InvalidOperationException(method.Name)
        });
        var ssl = WebmailDomainRoutingTests.InterfaceProxy.Make<ISslService>((method, args) => Complete((string)args![1]!));
        var dns = WebmailDomainRoutingTests.InterfaceProxy.Make<IDnsService>((method, args) =>
        {
            if (method.Name == "GetZoneAsync") return Task.FromResult(Zone);
            if (method.Name != "EnsureMissingRecordsAsync") throw new InvalidOperationException(method.Name);
            DnsWrites.AddRange((IReadOnlyCollection<UpsertDnsRecordRequest>)args![1]!);
            return Task.FromResult(Zone);
        });
        return new(null!, null!, iis, null!, new HealthFactory(), Store, ssl, NullLogger<WebmailService>.Instance, dnsService: dns)
        {
            HostBindingCommand = (_, _) => Task.FromResult(new CommandResult { ExitCode = 0 }),
            DomainHttpsProbe = (host, _) => Task.FromResult(secured || Bindings.Any(b => b.Protocol == "https" && b.HostName == host))
        };
    }
    private Task<SiteDto> Add(SiteBindingRequest binding)
    {
        Bindings.Add(new() { Protocol = binding.Protocol, Port = binding.Port, HostName = binding.HostName });
        return Task.FromResult(Site());
    }
    private async Task<bool> Complete(string host)
    {
        SslCalls.Add(host);
        var result = await Secure(host);
        if (result) { secured = true; Bindings.Add(new() { Protocol = "https", Port = 443, HostName = host }); }
        return result;
    }
    public Task Initialize() => Store.WriteAsync("config/webmail.json", new
    { siteName = "AdaPanel.Webmail", port = 8899, entryRoot = Path.Combine(root, "roundcube", "public_html"),
        phpComponentId = "php-8.3", securedHosts = new[] { "webmail.original.test" }, customSetting = "keep" });
    public async Task<JsonDocument> State() => JsonDocument.Parse(await File.ReadAllTextAsync(Store.ResolvePath("config/webmail.json")));
    public void Dispose()
    {
        var resolved = Path.GetFullPath(root);
        if (!resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(resolved).StartsWith("ada-webmail-publication-", StringComparison.Ordinal)) throw new IOException();
        if (Directory.Exists(resolved)) Directory.Delete(resolved, true);
    }
    private sealed class HealthFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Handler());
        private sealed class Handler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("AdaPanel-Webmail-OK") });
        }
    }
}
