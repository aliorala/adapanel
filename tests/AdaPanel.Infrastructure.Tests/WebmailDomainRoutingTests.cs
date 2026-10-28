using System.Net;
using System.Reflection;
using System.Text.Json;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Mail;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Mail;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class WebmailDomainRoutingTests
{
    [Fact]
    public async Task SelectedDomainDoesNotUseTheFirstSecuredHostOrRemoveExistingBindings()
    {
        using var f = new Fixture();
        await f.Initialize();
        var result = await f.Service.OpenAsync("192.168.1.80", new WebmailOpenRequest { Domain = "second.example" });
        Assert.Equal("https://webmail.second.example/", result.Url);
        Assert.Equal(["webmail.second.example"], f.Probes);
        Assert.Empty(f.Mutations);
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(f.Store.ResolvePath("config/webmail.json")));
        Assert.Equal("keep", json.RootElement.GetProperty("customSetting").GetString());
        Assert.Equal(2, json.RootElement.GetProperty("securedHosts").GetArrayLength());
    }

    [Fact]
    public async Task FailedTlsDoesNotFallBackToAnotherDomainsValidWebmail()
    {
        using var f = new Fixture { TlsValid = false };
        await f.Initialize();
        var error = await Assert.ThrowsAsync<AppException>(() => f.Service.OpenAsync("panel.example",
            new WebmailOpenRequest { Domain = "second.example" }));
        Assert.Equal("mail.webmail_https_required", error.MessageKey);
        Assert.Contains("secure:webmail.second.example", f.Mutations);
        Assert.DoesNotContain(f.Mutations, value => value.Contains("first.example"));
    }

    [Theory]
    [InlineData("*.example.test")]
    [InlineData("webmail.example.test")]
    [InlineData("example.test:443")]
    [InlineData("https://example.test")]
    [InlineData("example.test' ; anything")]
    public void UntrustedDomainInputCannotSelectABinding(string domain) =>
        Assert.Throws<AppException>(() => WebmailService.RequestedWebmailHost(domain));

    [Fact]
    public async Task AddressWithoutAnExplicitDomainCannotBecomeAGlobalSignIn()
    {
        using var f = new Fixture();
        var error = await Assert.ThrowsAsync<AppException>(() => f.Service.OpenAsync("panel.example",
            new WebmailOpenRequest { Address = "one@second.example" }));
        Assert.Equal("mail.webmail_domain_invalid", error.MessageKey);
        Assert.Empty(f.Probes);
    }

    [Theory]
    [InlineData(1, "ADAPANEL-WEBMAIL-ACL-OK")]
    [InlineData(0, "unverified")]
    public async Task SsoCannotProceedWhenPrivateAclWasNotPositivelyVerified(int exitCode, string output)
    {
        using var f = new Fixture { AclExitCode = exitCode, AclOutput = output };
        await f.Initialize();
        var error = await Assert.ThrowsAsync<AppException>(() => f.Service.OpenAsync("panel.example",
            new WebmailOpenRequest { Domain = "second.example", Address = "one@second.example" }));
        Assert.Equal("mail.webmail_sso_unavailable", error.MessageKey);
        Assert.Equal(1, f.AclCalls);
    }

    [Fact]
    public async Task SsoVerifiesHttpsAgainAfterTheDedicatedPoolPermissionsChange()
    {
        using var f = new Fixture();
        await f.Initialize();
        var result = await f.Service.OpenAsync("panel.example",
            new WebmailOpenRequest { Domain = "second.example", Address = "one@second.example" });
        Assert.Equal("https://webmail.second.example/", result.Url);
        Assert.Equal(1, f.AclCalls);
        Assert.Equal(2, f.Probes.Count);
    }

    public class InterfaceProxy : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?> Handler = (_, _) => throw new NotSupportedException();
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handler(targetMethod!, args);
        public static T Make<T>(Func<MethodInfo, object?[]?, object?> handler) where T : class
        {
            var instance = Create<T, InterfaceProxy>();
            ((InterfaceProxy)(object)instance).Handler = handler;
            return instance;
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "ada-webmail-domain-test-" + Guid.NewGuid().ToString("N"));
        public JsonFileStore Store { get; }
        public WebmailService Service { get; }
        public List<string> Probes { get; } = [];
        public List<string> Mutations { get; } = [];
        public bool TlsValid = true;
        public int AclExitCode;
        public string AclOutput = "ADAPANEL-WEBMAIL-ACL-OK";
        public int AclCalls;
        public Fixture()
        {
            Store = new(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = root }));
            var site = new SiteDto { Name = "AdaPanel.Webmail", State = "Started",
                ApplicationPoolName = "AdaPanel.AdaPanel.Webmail", PhysicalPath = Path.Combine(root, "roundcube", "public_html"),
                Bindings = [new() { Protocol = "http", Port = 8899 },
                    new() { Protocol = "http", Port = 80, HostName = "webmail.second.example" },
                    new() { Protocol = "https", Port = 443, HostName = "webmail.first.example" },
                    new() { Protocol = "https", Port = 443, HostName = "webmail.second.example" }] };
            var iis = InterfaceProxy.Make<IIisSiteService>((method, _) => method.Name == "GetSiteAsync" ? Task.FromResult(site)
                : throw new InvalidOperationException("Unexpected IIS mutation: " + method.Name));
            var ssl = InterfaceProxy.Make<ISslService>((method, args) =>
            {
                if (method.Name != "TrySecureHostAsync") throw new InvalidOperationException(method.Name);
                Mutations.Add("secure:" + args![1]);
                return Task.FromResult(false);
            });
            Service = new(null!, null!, iis, null!, new HealthFactory(), Store, ssl, NullLogger<WebmailService>.Instance)
            {
                DomainHttpsProbe = (host, _) => { Probes.Add(host); return Task.FromResult(TlsValid); },
                SsoAclCommand = (_, _) => { AclCalls++; return Task.FromResult(new CommandResult { ExitCode = AclExitCode, StandardOutput = AclOutput }); }
            };
        }
        public async Task Initialize()
        {
            foreach (var directory in new[] { "public_html", "program", "config" }) Directory.CreateDirectory(Path.Combine(root, "roundcube", directory));
            await Store.WriteAsync("config/webmail.json", new { siteName = "AdaPanel.Webmail", port = 8899,
                entryRoot = Path.Combine(root, "roundcube", "public_html"), phpComponentId = "php-8.3",
                securedHosts = new[] { "webmail.first.example" }, customSetting = "keep" });
        }
        public void Dispose()
        {
            var resolved = Path.GetFullPath(root);
            if (!resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(resolved).StartsWith("ada-webmail-domain-test-", StringComparison.Ordinal)) throw new IOException();
            if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
        }
    }
    private sealed class HealthFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new HealthHandler());
        private sealed class HealthHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
                => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("AdaPanel-Webmail-OK") });
        }
    }
}
