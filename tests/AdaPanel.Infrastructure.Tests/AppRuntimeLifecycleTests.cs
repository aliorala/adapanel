using System.Reflection;
using System.Security.Claims;
using System.Xml.Linq;
using AdaPanel.Api.Controllers;
using AdaPanel.Api.Security;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.AppRuntimes;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Components;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Entities;
using AdaPanel.Core.Enums;
using AdaPanel.Core.Interfaces;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.AppRuntimes;
using AdaPanel.Infrastructure.Iis;
using AdaPanel.Infrastructure.Services;
using AdaPanel.Infrastructure.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using MemorySite = AdaPanel.Infrastructure.Tests.SiteNativeLifecycleRecoveryTests.MemorySite;
using SiteCollection = AdaPanel.Infrastructure.Tests.SiteNativeLifecycleRecoveryTests.SiteCollection;
using PoolCollection = AdaPanel.Infrastructure.Tests.SiteNativeLifecycleRecoveryTests.PoolCollection;

namespace AdaPanel.Infrastructure.Tests;

public sealed partial class AppRuntimeLifecycleTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task<Exception?> Capture(Func<Task> action)
    { try { await action(); return null; } catch (Exception error) { return error; } }

    public static IEnumerable<object[]> ActorChanges() =>
        from runtime in new[] { "node", "python" }
        from change in new[] { "disabled", "deleted", "password", "role", "scope" }
        select new object[] { runtime, change };

    [Theory]
    [MemberData(nameof(ActorChanges))]
    public async Task RuntimeWritesRevalidateTheActorAfterCatalogWait(string runtime, string change)
    {
        using var f = await Fixture.Create();
        var entered = Signal(); var release = Signal();
        f.BeforeCatalog = async _ => { entered.TrySetResult(); await release.Task.WaitAsync(Deadline); };
        var action = Capture(() => f.Invoke(runtime));
        await entered.Task.WaitAsync(Deadline);
        try { await f.ChangeActor(change); }
        finally { release.TrySetResult(); }
        var error = Assert.IsType<AppException>(await action.WaitAsync(Deadline));
        Assert.True(error.HttpStatusCode is 401 or 403);
        f.AssertUnchanged();
    }

    public static IEnumerable<object[]> DependencyActorChanges() =>
        from runtime in new[] { "node", "python" }
        from change in new[] { "disabled", "password", "scope" }
        select new object[] { runtime, change };

    [Theory]
    [MemberData(nameof(DependencyActorChanges))]
    public async Task LostAuthorityCannotStartTheGlobalDependencyInstallation(string runtime, string change)
    {
        using var f = await Fixture.Create(); f.HandlerInstalled = false;
        var entered = Signal(); var release = Signal();
        f.BeforeCatalog = async _ => { entered.TrySetResult(); await release.Task.WaitAsync(Deadline); };
        var action = Capture(() => f.Invoke(runtime)); await entered.Task.WaitAsync(Deadline);
        try { await f.ChangeActor(change); }
        finally { release.TrySetResult(); }
        Assert.IsType<AppException>(await action.WaitAsync(Deadline));
        Assert.Equal(0, f.InstallCalls); f.AssertUnchanged();
    }

    [Theory]
    [InlineData("node")]
    [InlineData("python")]
    public async Task AuthorityLostDuringDependencyInstallStopsConfigurationAndSuccessAudit(string runtime)
    {
        using var f = await Fixture.Create(); f.HandlerInstalled = false;
        f.BeforeInstall = _ => f.ChangeActor("disabled");
        Assert.IsType<AppException>(await Capture(() => f.Invoke(runtime)));
        Assert.Equal(1, f.InstallCalls); f.AssertUnchanged();
    }

    public static IEnumerable<object[]> SiteChanges() =>
        from runtime in new[] { "node", "python" }
        from change in new[] { "id", "path", "same-id" }
        select new object[] { runtime, change };

    [Theory]
    [MemberData(nameof(SiteChanges))]
    public async Task RuntimeCannotAdoptAReplacementWhileCatalogIsPending(string runtime, string change)
    {
        using var f = await Fixture.Create();
        var entered = Signal(); var release = Signal();
        f.BeforeCatalog = async _ => { entered.TrySetResult(); await release.Task.WaitAsync(Deadline); };
        var action = Capture(() => f.Invoke(runtime)); await entered.Task.WaitAsync(Deadline);
        try
        {
            if (change == "id") f.Current.Id += 100;
            else if (change == "path") await f.Native.UpdatePhysicalPathAsync(f.Name, f.OtherPath);
            else
            {
                var id = f.Current.Id;
                await f.Native.CreateSiteAsync(f.Request());
                f.Current.Id = id;
            }
        }
        finally { release.TrySetResult(); }
        Assert.Equal("sites.target_changed", Assert.IsType<AppException>(await action.WaitAsync(Deadline)).MessageKey);
        f.AssertUnchanged();
        Assert.False(File.Exists(Path.Combine(f.OtherPath, "web.config")));
    }

    public static IEnumerable<object[]> PoolFailures() =>
        from runtime in new[] { "node", "python" }
        from failure in new[] { "throw", "missing", "ignored" }
        select new object[] { runtime, failure };

    [Theory]
    [MemberData(nameof(PoolFailures))]
    public async Task UnverifiedPoolChangesCannotPublishSuccessOrReplaceWorkingConfig(string runtime, string failure)
    {
        using var f = await Fixture.Create(); f.PoolFailure = failure;
        Assert.IsType<AppException>(await Capture(() => f.Invoke(runtime)));
        Assert.Equal(Fixture.OriginalXml, File.ReadAllText(f.WebConfig));
        Assert.Empty(f.Audits);
        Assert.Equal("v4.0", f.Pools.Single().ManagedRuntimeVersion);
    }

    [Theory]
    [InlineData("node")]
    [InlineData("python")]
    public async Task CancellationAtPoolCommitDoesNotContinueWithXmlOrSuccessAudit(string runtime)
    {
        using var f = await Fixture.Create(); using var cancellation = new CancellationTokenSource(Deadline);
        f.AfterPoolCommit = cancellation.Cancel;
        Assert.IsAssignableFrom<OperationCanceledException>(await Capture(() => f.Invoke(runtime, cancellation.Token)));
        Assert.Equal(Fixture.OriginalXml, File.ReadAllText(f.WebConfig)); Assert.Empty(f.Audits);
        Assert.Equal("v4.0", f.Pools.Single().ManagedRuntimeVersion);
        await f.Native.UpdatePhysicalPathAsync(f.Name, f.OtherPath);
    }

    [Theory]
    [InlineData("node")]
    [InlineData("python")]
    public async Task SiteStaysPinnedWhileDependencyInstallationRunsAndIsReleasedAfterwards(string runtime)
    {
        using var f = await Fixture.Create(); f.HandlerInstalled = false;
        var entered = Signal(); var release = Signal();
        f.BeforeInstall = async _ => { entered.TrySetResult(); await release.Task.WaitAsync(Deadline); };
        var action = f.Invoke(runtime); await entered.Task.WaitAsync(Deadline);
        Exception? mutation;
        try { mutation = await Capture(() => f.Native.UpdatePhysicalPathAsync(f.Name, f.OtherPath)); }
        finally { release.TrySetResult(); }
        await action.WaitAsync(Deadline);
        Assert.Equal("sites.target_changed", Assert.IsType<AppException>(mutation).MessageKey);
        Assert.Equal(f.SitePath, f.Current.PhysicalPath);
        await f.Native.UpdatePhysicalPathAsync(f.Name, f.OtherPath);
    }

    [Theory]
    [InlineData("node")]
    [InlineData("python")]
    public async Task CatalogCancellationCannotInstallOrWriteAndDoesNotLeakASitePin(string runtime)
    {
        using var f = await Fixture.Create(); using var cancellation = new CancellationTokenSource(Deadline);
        f.HandlerInstalled = false;
        f.BeforeCatalog = _ => { cancellation.Cancel(); return Task.CompletedTask; };
        Assert.IsAssignableFrom<OperationCanceledException>(await Capture(() => f.Invoke(runtime, cancellation.Token)));
        Assert.Equal(0, f.InstallCalls); f.AssertUnchanged();
        await f.Native.UpdatePhysicalPathAsync(f.Name, f.OtherPath);
    }

    [Theory]
    [InlineData("node", UserRole.Administrator)]
    [InlineData("node", UserRole.Reseller)]
    [InlineData("python", UserRole.Administrator)]
    [InlineData("python", UserRole.Reseller)]
    public async Task AuthorizedRuntimeConfigurationUsesMeasuredExecutableAndPool(string runtime, UserRole role)
    {
        using var f = await Fixture.Create(role: role);
        var result = await f.Invoke(runtime);
        Assert.True(result.ManagedRuntimeDisabled); Assert.True(result.HasHttpPlatformHandler);
        Assert.Equal(runtime, result.RuntimeType); Assert.Equal(Path.Combine(f.RuntimePath, runtime + ".exe"), result.ProcessPath);
        Assert.Equal("", f.Pools.Single().ManagedRuntimeVersion); Assert.Single(f.Audits);
        var xml = XDocument.Load(f.WebConfig);
        Assert.Equal("keep", (string?)xml.Descendants("appSettings").Elements("add").Single().Attribute("value"));
        Assert.Equal(result.ProcessPath, (string?)xml.Descendants("httpPlatform").Single().Attribute("processPath"));
        await f.Native.UpdatePhysicalPathAsync(f.Name, f.OtherPath);
    }

    [Theory]
    [InlineData("node")]
    [InlineData("python")]
    public async Task ActualProvisioningCanConfigureItsOwnPrivateRuntimeGeneration(string runtime)
    {
        using var f = await Fixture.Create(existingSite: false);
        var result = await f.Provision(runtime).WaitAsync(Deadline);
        Assert.Equal(f.Name, result.Name); Assert.Equal(1, f.RuntimeCommits);
        Assert.Equal("", f.Pools.Single().ManagedRuntimeVersion);
        Assert.True(File.Exists(Path.Combine(f.SitePath, runtime == "node" ? "server.js" : "app.py")));
        Assert.Single(XDocument.Load(f.WebConfig).Descendants("httpPlatform"));
        using var target = await SiteMutationGuard.CaptureAsync(f.Native, f.Gates, f.Name, default);
        using var use = await target.ReserveAsync(default);
    }

    [Theory]
    [InlineData("node")]
    [InlineData("python")]
    public async Task StartupPathsOutsideTheSiteNeverChangeItsConfiguration(string runtime)
    {
        using var f = await Fixture.Create(); f.StartupFile = Path.Combine(f.OtherPath, "outside.js");
        Assert.Equal("appruntime.path_outside_site_root", Assert.IsType<AppException>(await Capture(() => f.Invoke(runtime))).MessageKey);
        f.AssertUnchanged();
    }

    [Theory]
    [InlineData("node")]
    [InlineData("python")]
    public async Task FirstCaptureCannotAdoptAReplacementAlreadyQueuedForIis(string runtime)
    {
        using var f = await Fixture.Create();
        var entered = Signal(); var release = Signal();
        var blocker = f.Gates.RunExclusiveAsync(ConcurrencyKeys.Iis, async () =>
        { entered.TrySetResult(); await release.Task.WaitAsync(Deadline); });
        await entered.Task.WaitAsync(Deadline);
        var replacement = f.Native.CreateSiteAsync(f.Request());
        var action = Capture(() => f.Invoke(runtime));
        release.TrySetResult();
        await blocker.WaitAsync(Deadline); await replacement.WaitAsync(Deadline);
        Assert.Equal("sites.target_changed", Assert.IsType<AppException>(await action.WaitAsync(Deadline)).MessageKey);
        f.AssertUnchanged();
    }

    [Theory]
    [InlineData("node", false)]
    [InlineData("python", false)]
    [InlineData("node", true)]
    [InlineData("python", true)]
    public async Task FinalIisQueueRechecksActorAndCancellationBeforeWriting(string runtime, bool cancel)
    {
        using var f = await Fixture.Create(); using var cancellation = new CancellationTokenSource(Deadline);
        f.HandlerInstalled = false;
        var entered = Signal(); var release = Signal(); Task blocker = Task.CompletedTask;
        f.BeforeInstall = async _ =>
        {
            blocker = f.Gates.RunExclusiveAsync(ConcurrencyKeys.Iis, async () =>
            { entered.TrySetResult(); await release.Task.WaitAsync(Deadline); });
            await entered.Task.WaitAsync(Deadline);
        };
        var action = Capture(() => f.Invoke(runtime, cancellation.Token));
        await entered.Task.WaitAsync(Deadline);
        try { if (cancel) cancellation.Cancel(); else await f.ChangeActor("disabled"); }
        finally { release.TrySetResult(); }
        await blocker.WaitAsync(Deadline);
        var error = await action.WaitAsync(Deadline);
        if (cancel) Assert.IsAssignableFrom<OperationCanceledException>(error);
        else Assert.IsType<AppException>(error);
        Assert.Equal(1, f.InstallCalls); f.AssertUnchanged();
        await f.Native.UpdatePhysicalPathAsync(f.Name, f.OtherPath);
    }

    [Theory]
    [InlineData("node")]
    [InlineData("python")]
    public async Task RuntimeFailureDuringActualProvisioningRollsBackOnlyItsOwnSite(string runtime)
    {
        using var f = await Fixture.Create(existingSite: false); f.PoolFailure = "throw";
        Assert.IsType<AppException>(await Capture(() => f.Provision(runtime)).WaitAsync(Deadline));
        Assert.Empty(f.Sites); Assert.False(Directory.Exists(f.SitePath));
        f.PoolFailure = "";
        await f.Provision(runtime).WaitAsync(Deadline);
        Assert.Single(f.Sites); Assert.True(File.Exists(f.WebConfig));
    }

    [Theory]
    [InlineData("node")]
    [InlineData("python")]
    public async Task XmlWriteDeniedAfterPoolCommitRestoresThePreviouslyWorkingPool(string runtime)
    {
        using var f = await Fixture.Create();
        FileStream? reader = null;
        // A valid existing configuration remains readable, but a competing reader can
        // refuse writes after pool commit. This exercises the actual XDocument save.
        f.AfterPoolCommit = () =>
        {
            f.AfterPoolCommit = null;
            reader = new FileStream(f.WebConfig, FileMode.Open, FileAccess.Read, FileShare.Read);
        };
        Exception? error;
        try { error = await Capture(() => f.Invoke(runtime)); }
        finally { reader?.Dispose(); }
        Assert.IsType<AppException>(error);
        Assert.Equal(Fixture.OriginalXml, File.ReadAllText(f.WebConfig));
        Assert.Equal("v4.0", f.Pools.Single().ManagedRuntimeVersion);
        Assert.Empty(f.Audits);
        await f.Native.UpdatePhysicalPathAsync(f.Name, f.OtherPath);
    }

    [Theory]
    [InlineData("node")]
    [InlineData("python")]
    public async Task DirectoryFailureAfterPoolCommitRestoresThePreviousRuntime(string runtime)
    {
        using var f = await Fixture.Create();
        f.AfterPoolCommit = () =>
        {
            f.AfterPoolCommit = null;
            File.WriteAllText(Path.Combine(f.SitePath, "App_Data"), "a file prevents creation of the logs directory");
        };
        Assert.IsType<AppException>(await Capture(() => f.Invoke(runtime)));
        Assert.Equal("v4.0", f.Pools.Single().ManagedRuntimeVersion);
        Assert.Equal(Fixture.OriginalXml, File.ReadAllText(f.WebConfig)); Assert.Empty(f.Audits);
        await f.Native.UpdatePhysicalPathAsync(f.Name, f.OtherPath);
    }

    [Theory]
    [InlineData("removed")]
    [InlineData("different-pool")]
    [InlineData("different-value")]
    public async Task CompensationDoesNotRecreateOrOverwriteAForeignPoolState(string change)
    {
        using var f = await Fixture.Create();
        f.AfterPoolCommit = () =>
        {
            f.AfterPoolCommit = null;
            if (change == "removed") f.Pools.Clear();
            else if (change == "different-pool")
            {
                f.Pools.Clear();
                f.Pools.Add("foreign").ManagedRuntimeVersion = "v2.0";
                f.Current.Applications[0].ApplicationPoolName = "foreign";
            }
            else f.Pools.Single().ManagedRuntimeVersion = "v2.0";
            throw new IOException("pool state was changed externally before commit returned");
        };
        Assert.IsType<AppException>(await Capture(() => f.Invoke("node")));
        if (change == "removed") Assert.Empty(f.Pools);
        else Assert.Equal("v2.0", f.Pools.Single().ManagedRuntimeVersion);
        Assert.Equal(1, f.RuntimeCommits);
        Assert.Equal(Fixture.OriginalXml, File.ReadAllText(f.WebConfig)); Assert.Empty(f.Audits);
    }

    private sealed partial class Fixture : IDisposable
    {
        public const string OriginalXml = "<configuration><appSettings><add key=\"fixture\" value=\"keep\" /></appSettings></configuration>";
        private readonly string root = Path.Combine(Path.GetTempPath(), "AdaPanel.AppRuntime." + Guid.NewGuid().ToString("N"));
        private readonly SqliteConnection keeper = new($"Data Source=AppRuntime-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Pooling=False");
        private readonly List<AdaPanelDbContext> contexts = [];
        private ServiceProvider services = null!;
        private AppRuntimeController controller = null!;
        private readonly Guid actor = Guid.NewGuid();
        private bool scopeAllowed = true;
        private long nextId = 100;
        public string Name { get; } = "runtime-" + Guid.NewGuid().ToString("N") + ".test";
        public string SitePath => Path.Combine(root, "site");
        public string OtherPath => Path.Combine(root, "other");
        public string RuntimePath => Path.Combine(root, "runtime");
        public string WebConfig => Path.Combine(SitePath, "web.config");
        public string StartupFile = "";
        public IConcurrencyLock Gates { get; } = new ConcurrencyLock();
        public MemorySite Current => Sites.Single();
        public SiteCollection Sites { get; private set; } = [];
        public PoolCollection Pools { get; private set; } = [];
        public WebAdministrationSiteService Native { get; }
        public AppRuntimeService Runtime { get; private set; } = null!;
        private IComponentBootstrapper bootstrapper = null!;
        public string PoolFailure = "";
        public Action? AfterPoolCommit;
        public Func<CancellationToken, Task>? BeforeCatalog, BeforeInstall;
        public bool HandlerInstalled = true;
        public int InstallCalls, RuntimeCommits;
        public List<string> Audits { get; } = [];

        private Fixture()
        {
            Native = new WebAdministrationSiteService(Gates, NullLogger<WebAdministrationSiteService>.Instance,
                () => new NativeManager(this), token => { token.ThrowIfCancellationRequested(); return Task.CompletedTask; },
                (_, _, _) => Task.FromResult(new CommandResult()));
        }
        public static async Task<Fixture> Create(bool existingSite = true, UserRole role = UserRole.Reseller)
        {
            var f = new Fixture(); await f.keeper.OpenAsync();
            Directory.CreateDirectory(f.RuntimePath); Directory.CreateDirectory(f.OtherPath);
            var db = f.Context(); await db.Database.EnsureCreatedAsync();
            db.Users.Add(new PanelUser { Id = f.actor, UserName = "actor", Email = "actor@example.test", Role = role, IsActive = true, PasswordHash = "original-hash" });
            foreach (var (id, executable) in new[] { ("nodejs-lts", "node.exe"), ("python-3", "python.exe") })
            {
                File.WriteAllText(Path.Combine(f.RuntimePath, executable), "fixture only, never executed");
                db.InstalledComponents.Add(new InstalledComponent { ComponentId = id, InstallDir = f.RuntimePath, Status = ComponentInstallStatus.Installed });
            }
            await db.SaveChangesAsync();
            var scopes = Fake<ICustomerScopeService>((method, _) => method.Name switch
            {
                "GetResellerSitesAsync" or "GetAssignedSitesAsync" => Task.FromResult<IReadOnlyCollection<string>>(f.scopeAllowed ? [f.Name] : []),
                "GetParentSubscriptionAsync" or "GetSiteOwnerUserIdAsync" => Task.FromResult<Guid?>(null),
                _ => throw new InvalidOperationException("Unexpected scope: " + method.Name)
            });
            f.bootstrapper = Fake<IComponentBootstrapper>((method, args) => method.Name switch
            {
                "GetCatalogAsync" => f.ReadCatalog((CancellationToken)args[0]!),
                "InstallAsync" => f.Install((CancellationToken)args[4]!),
                _ => throw new InvalidOperationException("Unexpected bootstrapper: " + method.Name)
            });
            f.Runtime = new AppRuntimeService(f.Context(), f.Native, f.bootstrapper, f.Gates, NullLogger<AppRuntimeService>.Instance)
                { ManagerFactory = () => new RuntimeManager(f), ConfigurationStream = stream => f.ConfigureXmlStream?.Invoke(stream) ?? stream };
            f.services = new ServiceCollection().AddSingleton(f.Context()).AddSingleton<IIisSiteService>(f.Native)
                .AddSingleton(f.Gates).AddSingleton(scopes).BuildServiceProvider();
            f.controller = new AppRuntimeController(f.Runtime, scopes, Fake<IAuditService>((method, _) => { f.Audits.Add(method.Name); return Task.CompletedTask; }))
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext
                {
                    RequestServices = f.services,
                    User = new ClaimsPrincipal(new ClaimsIdentity([
                        new Claim(ClaimTypes.NameIdentifier, f.actor.ToString()), new Claim(ClaimTypes.Role, role.ToString()),
                        new Claim(CookieSessionValidator.CredentialStampClaim, CookieSessionValidator.ComputeStamp("original-hash"))], "fixture"))
                } }
            };
            if (existingSite)
            {
                await f.Native.CreateSiteAsync(f.Request());
                f.Pools.Single().ManagedRuntimeVersion = "v4.0";
                File.WriteAllText(f.WebConfig, OriginalXml);
            }
            return f;
        }
        private AdaPanelDbContext Context()
        { var db = new AdaPanelDbContext(new DbContextOptionsBuilder<AdaPanelDbContext>().UseSqlite(keeper.ConnectionString).Options); contexts.Add(db); return db; }
        public CreateSiteRequest Request() => new() { Name = Name, HostName = Name, PhysicalPath = SitePath, Port = 8080 };
        private async Task<SoftwareCatalogDto> ReadCatalog(CancellationToken token)
        {
            if (BeforeCatalog is not null) await BeforeCatalog(token);
            return new SoftwareCatalogDto { Installed = new[] { "nodejs-lts", "python-3" }
                .Concat(HandlerInstalled ? ["http-platform-handler"] : Array.Empty<string>())
                .Select(id => new InstalledComponentDto { ComponentId = id, Status = "Installed", InstallDir = RuntimePath }).ToArray() };
        }
        private async Task<ComponentInstallResultDto> Install(CancellationToken token)
        {
            InstallCalls++;
            if (BeforeInstall is not null) await BeforeInstall(token);
            HandlerInstalled = true;
            return new ComponentInstallResultDto { ComponentId = "http-platform-handler", Status = "Installed" };
        }
        public async Task<AppRuntimeSiteConfigurationDto> Invoke(string runtime, CancellationToken token = default)
        {
            var result = runtime == "node"
                ? await controller.HostNodeAppAsync(Name, new HostNodeAppRequest { StartupFile = StartupFile.Length == 0 ? "server.js" : StartupFile }, token)
                : await controller.HostPythonAppAsync(Name, new HostPythonAppRequest { StartupFile = StartupFile.Length == 0 ? "app.py" : StartupFile }, token);
            return Assert.IsType<ApiResponse<AppRuntimeSiteConfigurationDto>>(Assert.IsType<OkObjectResult>(result.Result).Value).Data!;
        }
        public Task<SiteDto> Provision(string runtime)
        {
            var provisioning = new SiteProvisioningService(Native, bootstrapper, Fake<IPhpService>(), Fake<IAspNetService>(), Runtime,
                Fake<ISiteDefaultsService>((_, args) => Task.FromResult(new SiteDefaultsResultDto { SiteName = ((SiteDto)args[0]!).Name })),
                Fake<IWafService>(), NullLogger<SiteProvisioningService>.Instance, Gates)
                { RunPermissionCommand = (_, _, _) => Task.FromResult(new CommandResult()) };
            return provisioning.CreateAndProvisionAsync(new CreateSiteRequest { Name = Name, HostName = Name, PhysicalPath = SitePath, RuntimeProfile = runtime, Port = 8080 }, actor, null);
        }
        public async Task ChangeActor(string change)
        {
            if (change == "scope") { scopeAllowed = false; return; }
            var db = Context(); var current = await db.Users.SingleAsync(user => user.Id == actor);
            if (change == "disabled") current.IsActive = false;
            else if (change == "deleted") db.Users.Remove(current);
            else if (change == "role") current.Role = UserRole.Administrator;
            else current.PasswordHash = "changed-hash";
            await db.SaveChangesAsync();
        }
        public void AssertUnchanged()
        { Assert.Equal(OriginalXml, File.ReadAllText(WebConfig)); Assert.Equal(0, RuntimeCommits); Assert.Empty(Audits); }
        public sealed class NativeManager : IDisposable
        {
            private readonly Fixture f;
            public SiteCollection Sites { get; } = [];
            public PoolCollection ApplicationPools { get; } = [];
            public NativeManager(Fixture f)
            { this.f = f; Sites.AddRange(f.Sites.Select(site => site.Clone())); ApplicationPools.AddRange(f.Pools.Select(pool => pool.Clone())); }
            public void CommitChanges()
            {
                foreach (var site in Sites) if (site.Id == 0) site.Id = ++f.nextId;
                f.Sites = new SiteCollection(); f.Sites.AddRange(Sites.Select(site => site.Clone()));
                f.Pools = new PoolCollection(); f.Pools.AddRange(ApplicationPools.Select(pool => pool.Clone()));
            }
            public void Dispose() { }
        }
        public sealed class RuntimeManager : IDisposable
        {
            private readonly Fixture f;
            public PoolCollection ApplicationPools { get; } = [];
            public RuntimeManager(Fixture f)
            { this.f = f; if (f.PoolFailure != "missing") ApplicationPools.AddRange(f.Pools.Select(pool => pool.Clone())); }
            public void CommitChanges()
            {
                f.RuntimeCommits++;
                if (f.PoolFailure == "throw") throw new IOException("fixture pool commit failed");
                if (f.PoolFailure != "ignored") { f.Pools = new PoolCollection(); f.Pools.AddRange(ApplicationPools.Select(pool => pool.Clone())); }
                f.AfterPoolCommit?.Invoke();
            }
            public void Dispose() { }
        }
        public void Dispose()
        {
            services?.Dispose(); foreach (var db in contexts) db.Dispose(); keeper.Dispose();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
    private static T Fake<T>(Func<MethodInfo, object?[], object?>? invoke = null) where T : class
    {
        var value = DispatchProxy.Create<T, Proxy>();
        ((Proxy)(object)value).Call = invoke ?? ((method, _) => throw new InvalidOperationException("Unexpected integration: " + method.Name));
        return value;
    }
    public class Proxy : DispatchProxy
    {
        public Func<MethodInfo, object?[], object?> Call { get; set; } = null!;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Call(targetMethod!, args ?? []);
    }
}
