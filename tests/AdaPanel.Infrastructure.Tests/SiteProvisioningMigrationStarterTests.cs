using System.Reflection;
using System.Security.Cryptography;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.AppRuntimes;
using AdaPanel.Core.Dtos.Asp;
using AdaPanel.Core.Dtos.Components;
using AdaPanel.Core.Dtos.Php;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Dtos.Waf;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Iis;
using AdaPanel.Infrastructure.Migration.Packages;
using AdaPanel.Infrastructure.Services;
using AdaPanel.Infrastructure.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>Exercises the actual public provisioning service; only IIS, component/runtime and OS permission boundaries are faked.</summary>
public sealed class SiteProvisioningMigrationStarterTests
{
    [Theory]
    [InlineData("auto", false)] [InlineData("auto", true)]
    [InlineData("static", false)] [InlineData("static", true)]
    [InlineData("php", false)] [InlineData("php", true)]
    [InlineData("aspnet", false)] [InlineData("aspnet", true)]
    [InlineData("classic-asp", false)] [InlineData("classic-asp", true)]
    [InlineData("mixed", false)] [InlineData("mixed", true)]
    [InlineData("node", false)] [InlineData("node", true)]
    [InlineData("python", false)] [InlineData("python", true)]
    public async Task Migration_public_provisioning_neither_creates_starters_nor_deletes_or_rewrites_imported_bytes(string profile, bool importedContent)
    {
        using var fixture = new Fixture(importedContent);
        var before = fixture.Fingerprint();
        var site = await fixture.Provision(profile, migration: true);
        Assert.Equal(fixture.Name, site.Name);
        Assert.Equal(before, fixture.Fingerprint());
        Assert.Equal(0, fixture.DefaultsCalls);
        Assert.Equal(0, fixture.UnexpectedCalls); Assert.Equal(0, fixture.DeleteCalls);
        Assert.Equal(1, fixture.PermissionCommands); Assert.Equal(1, fixture.WafCalls); Assert.Equal(1, fixture.PhpIsolationCalls);
        Assert.Equal(ExpectedHandler(profile), fixture.RuntimeCalls.SingleOrDefault() ?? "none");
    }

    [Theory]
    [InlineData("auto", "default.asp")]
    [InlineData("static", "")]
    [InlineData("php", "index.php")]
    [InlineData("aspnet", "Default.aspx")]
    [InlineData("classic-asp", "default.asp")]
    [InlineData("mixed", "default.asp")]
    [InlineData("node", "server.js")]
    [InlineData("python", "app.py")]
    public async Task Normal_hosting_keeps_its_runtime_starter_and_default_provisioning_behavior(string profile, string starter)
    {
        using var fixture = new Fixture(importedContent: false);
        await fixture.Provision(profile, migration: false);
        Assert.Equal(1, fixture.DefaultsCalls); Assert.Equal(0, fixture.UnexpectedCalls);
        if (starter.Length > 0) Assert.True(File.Exists(Path.Combine(fixture.SiteRoot, starter)), "Expected normal hosting starter: " + starter);
        if (profile is "auto" or "mixed") Assert.True(File.Exists(Path.Combine(fixture.SiteRoot, "php-info.php")));
        Assert.Equal(ExpectedHandler(profile), fixture.RuntimeCalls.SingleOrDefault() ?? "none");
    }

    [Fact]
    public async Task Migration_auto_without_selected_PHP_fails_with_original_runtime_error_and_never_falls_back_to_ASPNET()
    {
        using var fixture = new Fixture(importedContent: true) { PhpInstalled = false };
        var before = fixture.Fingerprint();
        var error = await Assert.ThrowsAsync<AppException>(() => fixture.Provision("auto", migration: true));
        Assert.Equal("php.version_not_installed", error.MessageKey);
        Assert.Empty(fixture.RuntimeCalls); Assert.Equal(0, fixture.DefaultsCalls);
        Assert.Equal(before, fixture.Fingerprint()); Assert.Equal(1, fixture.DeleteCalls); Assert.Null(fixture.Current);
        Assert.Equal(0, fixture.UnexpectedCalls);
    }

    [Fact]
    public async Task Normal_auto_without_PHP_still_uses_its_documented_ASPNET_fallback()
    {
        using var fixture = new Fixture(importedContent: false) { PhpInstalled = false };
        await fixture.Provision("auto", migration: false);
        Assert.Equal(new[] { "aspnet" }, fixture.RuntimeCalls); Assert.Equal(1, fixture.DefaultsCalls);
        Assert.True(File.Exists(Path.Combine(fixture.SiteRoot, "Default.aspx")));
        Assert.Equal(0, fixture.UnexpectedCalls); Assert.Equal(0, fixture.DeleteCalls);
    }

    private static string ExpectedHandler(string profile) => profile switch
    { "auto" or "mixed" => "mixed", "static" => "none", _ => profile };

    private sealed class Fixture : IDisposable
    {
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "ada-migration-starter-" + Guid.NewGuid().ToString("N"));
        internal string SiteRoot => Path.Combine(Root, "site");
        internal readonly string Name = "migration-" + Guid.NewGuid().ToString("N") + ".test";
        private readonly IConcurrencyLock gates = new ConcurrencyLock();
        internal bool PhpInstalled = true;
        internal SiteDto? Current;
        internal readonly List<string> RuntimeCalls = [];
        internal int DefaultsCalls, PermissionCommands, WafCalls, PhpIsolationCalls, DeleteCalls, UnexpectedCalls;
        private readonly SiteProvisioningService service;
        internal Fixture(bool importedContent)
        {
            Directory.CreateDirectory(SiteRoot);
            if (importedContent)
            {
                foreach (var name in new[] { "index.html", "index.php", "Default.aspx", "default.asp", "php-info.php", "web.config", "server.js", "app.py" })
                    File.WriteAllText(Path.Combine(SiteRoot, name), "<!-- " + StarterPage.Marker + " -->\nImported original: " + name);
                Directory.CreateDirectory(Path.Combine(SiteRoot, "assets"));
                File.WriteAllBytes(Path.Combine(SiteRoot, "assets", "source.bin"), Enumerable.Range(0, 256).Select(n => (byte)n).ToArray());
            }
            var sites = Fake<IIisSiteService>((method, _) => method.Name switch
            {
                nameof(IIisSiteService.CreateSiteAsync) => SiteMutationGuard.RunMutationAsync(gates, Name, creating: true, () =>
                {
                    Directory.CreateDirectory(SiteRoot);
                    Current = new SiteDto { Id = 42, Name = Name, PhysicalPath = SiteRoot, ApplicationRootPath = SiteRoot,
                        ApplicationPoolName = "AdaPanel." + Name, Bindings = [new() { Protocol = "http", HostName = Name, Port = 80 }] };
                    return Task.FromResult(Current);
                }, default),
                nameof(IIisSiteService.GetSiteAsync) => Task.FromResult(Current ?? throw new AppException("sites.not_found", 404, 404)),
                nameof(IIisSiteService.DeleteSiteAsync) => Delete(),
                _ => throw Unexpected(method.Name)
            });
            var components = Fake<IComponentBootstrapper>((method, _) => method.Name switch
            {
                nameof(IComponentBootstrapper.GetCatalogAsync) => Task.FromResult(new SoftwareCatalogDto
                {
                    Manifest = [new() { Id = "php-8.3", Version = "8.3" }],
                    Installed = new[] { "php-8.3", "nodejs-lts", "python-3", "http-platform-handler" }
                        .Where(id => id != "php-8.3" || PhpInstalled).Select(id => new InstalledComponentDto { ComponentId = id, Status = "Installed", Version = "8.3" }).ToArray()
                }),
                _ => throw Unexpected(method.Name)
            });
            var php = Fake<IPhpService>((method, args) => method.Name switch
            {
                nameof(IPhpService.SetSitePhpVersionAsync) => SetPhp((SetSitePhpVersionRequest)args![1]!),
                nameof(IPhpService.EnsureSiteIsolationAsync) => Isolate(),
                _ => throw Unexpected(method.Name)
            });
            var asp = Fake<IAspNetService>((method, args) => method.Name switch
            {
                nameof(IAspNetService.EnableMixedHandlersAsync) => Asp("mixed", args![1]!),
                nameof(IAspNetService.SetDotNetVersionAsync) => Asp("aspnet", args![1]!),
                nameof(IAspNetService.SetClassicAspAsync) => Asp("classic-asp", args![1]!),
                _ => throw Unexpected(method.Name)
            });
            var app = Fake<IAppRuntimeService>((method, args) => method.Name switch
            {
                nameof(IAppRuntimeService.HostNodeAppAsync) => App("node", args![1]!),
                nameof(IAppRuntimeService.HostPythonAppAsync) => App("python", args![1]!),
                _ => throw Unexpected(method.Name)
            });
            var defaults = Fake<ISiteDefaultsService>((method, _) => method.Name == nameof(ISiteDefaultsService.EnsureDefaultsAsync)
                ? Defaults() : throw Unexpected(method.Name));
            var waf = Fake<IWafService>((method, _) => method.Name == nameof(IWafService.ApplyFolderAclAsync) ? Waf() : throw Unexpected(method.Name));
            service = new SiteProvisioningService(sites, components, php, asp, app, defaults, waf, NullLogger<SiteProvisioningService>.Instance, gates)
            {
                RunPermissionCommand = (_, _, token) => { token.ThrowIfCancellationRequested(); PermissionCommands++; return Task.FromResult(new CommandResult { ExitCode = 0 }); },
                ProbePermission = (_, _) => new(FolderWriteAccessProbe.Access.Granted, "fixture measurement"),
                PublishPhpEntry = _ => throw Unexpected("PublishPhpEntry"),
                PublishFrameworkRewrite = _ => throw Unexpected("PublishFrameworkRewrite")
            };
        }
        internal Task<SiteDto> Provision(string profile, bool migration) => service.CreateAndProvisionAsync(new CreateSiteRequest
        {
            Name = Name, PhysicalPath = SiteRoot, RuntimeProfile = profile, InstallRuntimeIfMissing = false,
            MigrationFreshOnly = migration, AutoStart = !migration, ReplaceExisting = false
        }, null, null);
        private Task Delete() => SiteMutationGuard.RunMutationAsync(gates, Name, creating: false, () =>
        { DeleteCalls++; Current = null; return Task.FromResult(0); }, default);
        private Task<SitePhpAssignmentDto> SetPhp(SetSitePhpVersionRequest request)
        { Assert.Equal("php-8.3", request.ComponentId); RuntimeCalls.Add("php"); return Task.FromResult(new SitePhpAssignmentDto { SiteName = Name, ComponentId = request.ComponentId }); }
        private Task Isolate() { PhpIsolationCalls++; return Task.CompletedTask; }
        private Task<AspSiteConfigurationDto> Asp(string profile, object request)
        {
            if (request is EnableMixedHandlersRequest mixed)
            { Assert.Equal("php-8.3", mixed.PhpComponentId); Assert.True(mixed.EnableClassicAsp); Assert.Equal("v4.0", mixed.ManagedRuntimeVersion); }
            else if (request is UpdateDotNetRuntimeRequest managed) Assert.Equal("v4.0", managed.ManagedRuntimeVersion);
            else Assert.True(Assert.IsType<UpdateClassicAspRequest>(request).Enabled);
            RuntimeCalls.Add(profile);
            return Task.FromResult(new AspSiteConfigurationDto { SiteName = Name, ApplicationPoolName = "AdaPanel." + Name,
                ClassicAspEnabled = profile is "mixed" or "classic-asp", ManagedRuntimeVersion = profile == "classic-asp" ? "" : "v4.0",
                HasPhpHandler = profile == "mixed", PhpComponentId = profile == "mixed" ? "php-8.3" : "", IsMixedHandlersEnabled = profile == "mixed" });
        }
        private Task<AppRuntimeSiteConfigurationDto> App(string profile, object request)
        {
            var startup = profile == "node" ? Assert.IsType<HostNodeAppRequest>(request).StartupFile : Assert.IsType<HostPythonAppRequest>(request).StartupFile;
            Assert.Equal(profile == "node" ? "server.js" : "app.py", startup);
            RuntimeCalls.Add(profile);
            return Task.FromResult(new AppRuntimeSiteConfigurationDto { SiteName = Name, RuntimeType = profile,
                ComponentId = profile == "node" ? "nodejs-lts" : "python-3", StartupFile = startup,
                WorkingDirectory = SiteRoot, IsEnabled = true, HasHttpPlatformHandler = true, ManagedRuntimeDisabled = true });
        }
        private Task<SiteDefaultsResultDto> Defaults()
        { DefaultsCalls++; File.WriteAllText(Path.Combine(SiteRoot, "defaults.fixture"), "explicit normal defaults"); return Task.FromResult(new SiteDefaultsResultDto { SiteName = Name }); }
        private Task<SiteSecurityConfigurationDto> Waf() { WafCalls++; return Task.FromResult(new SiteSecurityConfigurationDto { SiteName = Name }); }
        private Exception Unexpected(string method) { UnexpectedCalls++; return new InvalidOperationException("Unexpected fixture boundary: " + method); }
        internal string[] Fingerprint() => Directory.EnumerateFiles(SiteRoot, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(path => Path.GetRelativePath(SiteRoot, path) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))).ToArray();
        public void Dispose() => PortableMigrationStorage.DeleteOwnedDirectory(Root);
    }
    private static T Fake<T>(Func<MethodInfo, object?[]?, object?> handler) where T : class
    {
        var proxy = DispatchProxy.Create<T, Proxy>(); ((Proxy)(object)proxy).Handler = handler; return proxy;
    }
    public class Proxy : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?> Handler = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => Handler(method!, args);
    }
}
