using System.Reflection;
using System.Security.Claims;
using System.Xml.Linq;
using AdaPanel.Api.Controllers;
using AdaPanel.Api.Security;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Dtos.Waf;
using AdaPanel.Core.Entities;
using AdaPanel.Core.Enums;
using AdaPanel.Core.Interfaces;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Iis;
using AdaPanel.Infrastructure.Options;
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

public sealed class SiteSettingsLifecycleTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static IEnumerable<object[]> ActorChanges() =>
        from route in new[] { "root", "rewrite", "repair" }
        from change in new[] { "disabled", "deleted", "password", "section", "scope" }
        select new object[] { route, change };

    [Theory]
    [MemberData(nameof(ActorChanges))]
    public async Task QueuedSettingsRecheckTheActorAndItsSitesSection(string route, string change)
    {
        using var f = await Fixture.Create();
        var entered = Signal();
        var release = Signal();
        f.BeforeService = async () => { entered.TrySetResult(); await release.Task.WaitAsync(Deadline); };
        var action = Capture(() => f.Invoke(route));
        await entered.Task.WaitAsync(Deadline);
        var commits = f.Commits;
        try { await f.ChangeActor(change); }
        finally { release.TrySetResult(); }
        var error = Assert.IsType<AppException>(await action.WaitAsync(Deadline));
        Assert.True(error.HttpStatusCode is 401 or 403);
        Assert.Equal(commits, f.Commits);
        Assert.Equal(0, f.Published);
        Assert.Equal(0, f.AclCalls);
        Assert.Empty(f.Audits);
    }

    public static IEnumerable<object[]> GenerationChanges() =>
        from route in new[] { "root", "rewrite", "repair" }
        from change in new[] { "id", "path", "same-id" }
        select new object[] { route, change };

    [Theory]
    [MemberData(nameof(GenerationChanges))]
    public async Task QueuedSettingsCannotAdoptAnotherGeneration(string route, string change)
    {
        using var f = await Fixture.Create();
        var entered = Signal();
        var release = Signal();
        f.BeforeService = async () => { entered.TrySetResult(); await release.Task.WaitAsync(Deadline); };
        var action = Capture(() => f.Invoke(route));
        await entered.Task.WaitAsync(Deadline);
        try
        {
            if (change == "same-id")
            {
                var oldId = f.Current.Id;
                await f.Native.CreateSiteAsync(f.Request(f.Name, f.SitePath));
                f.Current.Id = oldId;
            }
            else if (change == "id") f.Current.Id += 100;
            else f.Current.PhysicalPath = f.ForeignPath;
        }
        finally { release.TrySetResult(); }
        var error = Assert.IsType<AppException>(await action.WaitAsync(Deadline));
        Assert.Equal("sites.target_changed", error.MessageKey);
        Assert.Equal(0, f.Published);
        Assert.Equal(0, f.AclCalls);
        Assert.Empty(f.Audits);
        Assert.True(File.Exists(Path.Combine(f.ForeignPath, "index.html")));
    }

    [Theory]
    [InlineData("create")]
    [InlineData("delete")]
    [InlineData("root")]
    public async Task RootCommitAtomicallyPinsItsNewIdentityBeforeAdmittingAQueuedNativeMutation(string mutation)
    {
        using var f = await Fixture.Create();
        Task? contender = null;
        f.AfterCommit = () =>
        {
            f.AfterCommit = null;
            contender = f.Mutate(mutation);
        };
        // Keep the file phase open until the queued native operation observes its pin.
        f.BeforeSideEffect = () => Capture(() => contender!).WaitAsync(Deadline).GetAwaiter().GetResult();
        await f.Invoke("root").WaitAsync(Deadline);
        Assert.NotNull(contender);
        var error = await Capture(() => contender).WaitAsync(Deadline);
        Assert.Equal("sites.target_changed", Assert.IsType<AppException>(error).MessageKey);
        Assert.Equal(f.RequestedPath, f.Current.PhysicalPath);
        Assert.Equal(1, f.Published);
        Assert.False(File.Exists(Path.Combine(f.RequestedPath, "index.html")));
        await f.Native.UpdatePhysicalPathAsync(f.Name, f.SitePath); // The operation releases its new pin.
    }

    [Theory]
    [InlineData("root")]
    [InlineData("rewrite")]
    [InlineData("repair")]
    public async Task ReplacementQueuedBeforeInitialCaptureCannotBecomeItsApprovedGeneration(string route)
    {
        using var f = await Fixture.Create();
        var entered = Signal();
        var release = Signal();
        var blocker = f.Gates.RunExclusiveAsync(ConcurrencyKeys.Iis, async () =>
        { entered.TrySetResult(); await release.Task.WaitAsync(Deadline); }, CancellationToken.None);
        await entered.Task.WaitAsync(Deadline);
        var originalId = f.Current.Id;
        var replacement = f.Native.CreateSiteAsync(f.Request(f.Name, f.SitePath));
        var action = Capture(() => f.Invoke(route));
        release.TrySetResult();
        await blocker.WaitAsync(Deadline);
        await replacement.WaitAsync(Deadline);
        Assert.Equal("sites.target_changed", Assert.IsType<AppException>(await action.WaitAsync(Deadline)).MessageKey);
        Assert.NotEqual(originalId, f.Current.Id);
        Assert.Equal(0, f.Published);
        Assert.Equal(0, f.AclCalls);
    }

    public static IEnumerable<object[]> SideEffectMutations() =>
        from route in new[] { "rewrite", "repair" }
        from mutation in new[] { "create", "delete", "root" }
        select new object[] { route, mutation };

    [Theory]
    [MemberData(nameof(SideEffectMutations))]
    public async Task SettingsKeepTheirGenerationPinnedThroughoutFileAndPermissionWork(string route, string mutation)
    {
        using var f = await Fixture.Create();
        Task? contender = null;
        f.BeforeSideEffect = () => { f.BeforeSideEffect = null; contender = f.Mutate(mutation); };
        await f.Invoke(route).WaitAsync(Deadline);
        Assert.NotNull(contender);
        Assert.Equal("sites.target_changed", Assert.IsType<AppException>(
            await Capture(() => contender).WaitAsync(Deadline)).MessageKey);
        Assert.Equal(f.SitePath, f.Current.PhysicalPath);
        await f.Native.UpdatePhysicalPathAsync(f.Name, f.RequestedPath);
    }

    [Fact]
    public async Task CancellationAfterRootCommitStopsPublisherAndReleasesThePin()
    {
        using var f = await Fixture.Create();
        using var cancellation = new CancellationTokenSource(Deadline);
        f.AfterCommit = () => { f.AfterCommit = null; cancellation.Cancel(); };
        Assert.IsAssignableFrom<OperationCanceledException>(await Capture(() => f.Invoke("root", cancellation.Token)));
        Assert.Equal(0, f.Published);
        Assert.Empty(f.Audits);
        Assert.Equal(f.RequestedPath, f.Current.PhysicalPath); // The already committed path is reported by IIS.
        await f.Native.UpdatePhysicalPathAsync(f.Name, f.SitePath);
    }

    [Fact]
    public async Task CancellationAfterRewriteReadStopsTheConfigurationWrite()
    {
        using var f = await Fixture.Create();
        using var cancellation = new CancellationTokenSource(Deadline);
        f.BeforeService = () =>
        {
            f.InspectManager = _ => { f.InspectManager = null; cancellation.Cancel(); };
            return Task.CompletedTask;
        };
        Assert.IsAssignableFrom<OperationCanceledException>(await Capture(() => f.Invoke("rewrite", cancellation.Token)));
        Assert.Equal(0, f.Published);
        Assert.Empty(f.Audits);
        Assert.False(File.Exists(Path.Combine(f.SitePath, "web.config")));
    }

    [Fact]
    public async Task CanceledPermissionCommandCannotContinueIsolationOrPublishARepairResult()
    {
        using var f = await Fixture.Create();
        using var cancellation = new CancellationTokenSource(Deadline);
        f.PermissionCommand = token => { cancellation.Cancel(); throw new OperationCanceledException(token); };
        Assert.IsAssignableFrom<OperationCanceledException>(await Capture(() => f.Invoke("repair", cancellation.Token)));
        Assert.Equal(1, f.AclCalls);
        Assert.Equal(0, f.WafCalls);
        Assert.Equal(0, f.PhpCalls);
        Assert.Equal(0, f.ProbeCalls);
        Assert.Empty(f.Audits);
        await f.Native.UpdatePhysicalPathAsync(f.Name, f.RequestedPath);
    }

    [Fact]
    public async Task TenantCanUseAnotherOwnedSitesChildDirectory()
    {
        using var f = await Fixture.Create();
        f.RequestedPath = Path.Combine(f.DonorPath, "public");
        Fixture.WritePhp(f.RequestedPath);
        await f.Invoke("root");
        Assert.Equal(f.RequestedPath, f.Current.PhysicalPath);
        Assert.Equal("index.php", (string?)XDocument.Load(Path.Combine(f.RequestedPath, "web.config"))
            .Descendants("defaultDocument").Descendants("add").First().Attribute("value"));
        Assert.Single(f.Audits);
    }

    [Fact]
    public async Task TenantCannotUseAnotherTenantsDirectory()
    {
        using var f = await Fixture.Create();
        f.RequestedPath = f.ForeignPath;
        var result = await f.Invoke("root");
        Assert.Equal(403, Assert.IsAssignableFrom<ObjectResult>(result).StatusCode);
        Assert.Equal(f.SitePath, f.Current.PhysicalPath);
        Assert.Equal(0, f.Published);
    }

    [Theory]
    [InlineData("root")]
    [InlineData("rewrite")]
    [InlineData("repair")]
    public async Task UnchangedActorAndGenerationKeepNormalSettingsWorking(string route)
    {
        using var f = await Fixture.Create();
        Assert.IsType<OkObjectResult>(await f.Invoke(route));
        Assert.Single(f.Audits);
        if (route == "repair")
        {
            Assert.Equal(1, f.AclCalls);
            Assert.Equal(1, f.WafCalls);
            Assert.Equal(1, f.PhpCalls);
            Assert.Equal(1, f.ProbeCalls);
        }
        else Assert.Equal(1, f.Published);
    }

    [Theory]
    [InlineData("root")]
    [InlineData("rewrite")]
    [InlineData("repair")]
    public async Task BindingOnlyChangesDoNotInvalidateTheSameSite(string route)
    {
        using var f = await Fixture.Create();
        f.BeforeService = () =>
        {
            f.Current.Bindings = [new SiteNativeLifecycleRecoveryTests.Binding { Protocol = "https", BindingInformation = "*:443:" + f.Name }];
            return Task.CompletedTask;
        };
        Assert.IsType<OkObjectResult>(await f.Invoke(route));
        Assert.Single(f.Audits);
    }

    private static async Task<Exception?> Capture(Func<Task> action)
    { try { await action(); return null; } catch (Exception error) { return error; } }

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "AdaPanel.SiteSettings." + Guid.NewGuid().ToString("N"));
        private readonly SqliteConnection keeper = new($"Data Source=SiteSettings-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Pooling=False");
        private readonly List<AdaPanelDbContext> contexts = [];
        private ServiceProvider services = null!;
        private SitesController controller = null!;
        private readonly Guid actor = Guid.NewGuid();
        private readonly Guid parent = Guid.NewGuid();
        private bool sectionAllowed = true;
        private string[] assignedSites = [];
        public string Name { get; } = "settings-" + Guid.NewGuid().ToString("N") + ".test";
        private string DonorName => "donor." + Name;
        public string SitePath => Path.Combine(root, "site");
        public string DonorPath => Path.Combine(root, "donor");
        public string ForeignPath => Path.Combine(root, "foreign");
        public string RequestedPath;
        public IConcurrencyLock Gates { get; } = new ConcurrencyLock();
        public Dictionary<string, MemorySite> Sites { get; private set; } = new(StringComparer.OrdinalIgnoreCase);
        public PoolCollection Pools { get; private set; } = [];
        public MemorySite Current => Sites[Name];
        public WebAdministrationSiteService Native { get; }
        public SiteProvisioningService Provisioning { get; private set; } = null!;
        public int Commits, Published, AclCalls, WafCalls, PhpCalls, ProbeCalls;
        public List<string> Audits { get; } = [];
        public Func<Task>? BeforeService;
        public Action? AfterCommit;
        public Action? BeforeSideEffect;
        public Action<Manager>? InspectManager;
        public Func<CancellationToken, Task<CommandResult>> PermissionCommand = _ => Task.FromResult(new CommandResult());
        private long nextId = 200;

        private Fixture()
        {
            RequestedPath = Path.Combine(SitePath, "public");
            Native = new WebAdministrationSiteService(Gates, NullLogger<WebAdministrationSiteService>.Instance,
                () => { var manager = new Manager(this); InspectManager?.Invoke(manager); return manager; },
                token => { token.ThrowIfCancellationRequested(); return Task.CompletedTask; },
                (_, _, _) => Task.FromResult(new CommandResult()));
        }

        public static async Task<Fixture> Create()
        {
            var f = new Fixture();
            await f.keeper.OpenAsync();
            var db = f.Context();
            await db.Database.EnsureCreatedAsync();
            db.Users.AddRange(
                new PanelUser { Id = f.actor, UserName = "child", Email = "child@example.test", Role = UserRole.Customer, IsActive = true, PasswordHash = "original-hash" },
                new PanelUser { Id = f.parent, UserName = "parent", Email = "parent@example.test", Role = UserRole.Customer, IsActive = true, PasswordHash = "parent-hash" });
            await db.SaveChangesAsync();
            f.assignedSites = [f.Name, f.DonorName];
            var scopes = Fake<ICustomerScopeService>((method, args) => method.Name switch
            {
                "GetAssignedSitesAsync" => Task.FromResult<IReadOnlyCollection<string>>(f.assignedSites.ToArray()),
                "GetResellerSitesAsync" => Task.FromResult<IReadOnlyCollection<string>>(f.assignedSites.ToArray()),
                "GetParentSubscriptionAsync" => Task.FromResult<Guid?>((Guid)args[0]! == f.actor ? f.parent : null),
                "GetSubUserSectionsAsync" => Task.FromResult<IReadOnlyCollection<string>?>(f.sectionAllowed ? ["sites"] : []),
                _ => throw new InvalidOperationException("Unexpected scope: " + method.Name)
            });
            var audit = Fake<IAuditService>((method, args) => { f.Audits.Add(method.Name); return Task.CompletedTask; });
            f.Provisioning = new SiteProvisioningService(f.Native, Fake<IComponentBootstrapper>(), Fake<IPhpService>((method, _) =>
                { if (method.Name != "EnsureSiteIsolationAsync") throw new InvalidOperationException(method.Name); f.PhpCalls++; return Task.CompletedTask; }),
                Fake<IAspNetService>(), Fake<IAppRuntimeService>(), Fake<ISiteDefaultsService>(),
                Fake<IWafService>((method, _) =>
                { if (method.Name != "ApplyFolderAclAsync") throw new InvalidOperationException(method.Name); f.WafCalls++; return Task.FromResult(new SiteSecurityConfigurationDto()); }),
                NullLogger<SiteProvisioningService>.Instance, f.Gates)
            {
                RunPermissionCommand = (_, _, token) => { f.AclCalls++; f.BeforeSideEffect?.Invoke(); return f.PermissionCommand(token); },
                ProbePermission = (_, _) => { f.ProbeCalls++; return new FolderWriteAccessProbe.Result(FolderWriteAccessProbe.Access.Granted, "measured by fixture"); },
                PublishPhpEntry = path => { f.BeforeSideEffect?.Invoke(); f.Published++; WebAppPublisher.EnsurePhpEntry(path); },
                PublishFrameworkRewrite = path => { f.BeforeSideEffect?.Invoke(); f.Published++; return WebAppPublisher.TryWriteFrontControllerRewrite(path, true); }
            };
            var provisioning = Fake<ISiteProvisioningService>((method, args) => method.Name switch
            {
                "SetDocumentRootAsync" or "EnableFrameworkRewriteAsync" => f.CallProvision<SiteDto>(method, args),
                "RepairPermissionsAsync" => f.CallProvision<SitePermissionRepairResultDto>(method, args),
                _ => throw new InvalidOperationException(method.Name)
            });
            f.services = new ServiceCollection().AddSingleton(f.Context()).AddSingleton(scopes).BuildServiceProvider();
            f.controller = new SitesController(f.Native, provisioning, null!, null!, scopes, null!, null!, null!, null!, audit, f.Gates,
                Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = Path.Combine(f.root, "panel") }),
                NullLogger<SitesController>.Instance)
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext
                {
                    RequestServices = f.services,
                    User = new ClaimsPrincipal(new ClaimsIdentity([
                        new Claim(ClaimTypes.NameIdentifier, f.actor.ToString()), new Claim(ClaimTypes.Role, "Customer"),
                        new Claim(CookieSessionValidator.CredentialStampClaim, CookieSessionValidator.ComputeStamp("original-hash"))], "test"))
                } }
            };
            await f.Native.CreateSiteAsync(f.Request(f.Name, f.SitePath));
            await f.Native.CreateSiteAsync(f.Request(f.DonorName, f.DonorPath));
            WritePhp(f.SitePath); WritePhp(f.RequestedPath); WritePhp(f.DonorPath); WritePhp(f.ForeignPath);
            return f;
        }
        private AdaPanelDbContext Context()
        { var db = new AdaPanelDbContext(new DbContextOptionsBuilder<AdaPanelDbContext>().UseSqlite(keeper.ConnectionString).Options); contexts.Add(db); return db; }
        public static void WritePhp(string path)
        { Directory.CreateDirectory(path); File.WriteAllText(Path.Combine(path, "index.php"), "<?php echo 'fixture';"); File.WriteAllText(Path.Combine(path, "index.html"), "keep unless publishing succeeds"); }
        public CreateSiteRequest Request(string name, string path) => new() { Name = name, HostName = name, PhysicalPath = path, Port = 8080 };
        public Task Mutate(string kind) => kind switch
        {
            "create" => Native.CreateSiteAsync(Request(Name, RequestedPath)),
            "delete" => Native.DeleteSiteAsync(Name),
            _ => Native.UpdatePhysicalPathAsync(Name, ForeignPath)
        };
        private async Task<T> CallProvision<T>(MethodInfo method, object?[] args)
        {
            if (BeforeService is not null) await BeforeService();
            try { return await (Task<T>)method.Invoke(Provisioning, args)!; }
            catch (TargetInvocationException error) when (error.InnerException is not null)
            { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
        }
        public async Task<IActionResult> Invoke(string route, CancellationToken token = default) => route switch
        {
            "root" => (await controller.SetDocumentRootAsync(Name, new UpdateDocumentRootRequest { PhysicalPath = RequestedPath }, token)).Result!,
            "rewrite" => (await controller.EnableFrameworkRewriteAsync(Name, token)).Result!,
            _ => (await controller.RepairPermissionsAsync(Name, token)).Result!
        };
        public async Task ChangeActor(string change)
        {
            if (change == "section") { sectionAllowed = false; return; }
            if (change == "scope") { assignedSites = []; return; }
            var db = Context();
            var current = await db.Users.SingleAsync(user => user.Id == actor);
            if (change == "disabled") current.IsActive = false;
            else if (change == "deleted") db.Users.Remove(current);
            else current.PasswordHash = "changed-hash";
            await db.SaveChangesAsync();
        }
        public sealed class Manager : IDisposable
        {
            private readonly Fixture fixture;
            public SiteCollection Sites { get; } = [];
            public PoolCollection ApplicationPools { get; } = [];
            public Manager(Fixture fixture)
            {
                this.fixture = fixture;
                Sites.AddRange(fixture.Sites.Values.Select(site => site.Clone()));
                ApplicationPools.AddRange(fixture.Pools.Select(pool => pool.Clone()));
            }
            public void CommitChanges()
            {
                fixture.Commits++;
                foreach (var site in Sites) if (site.Id == 0) site.Id = ++fixture.nextId;
                fixture.Sites = Sites.ToDictionary(site => site.Name, site => site.Clone(), StringComparer.OrdinalIgnoreCase);
                fixture.Pools = new PoolCollection();
                fixture.Pools.AddRange(ApplicationPools.Select(pool => pool.Clone()));
                fixture.AfterCommit?.Invoke();
            }
            public void Dispose() { }
        }
        public void Dispose()
        {
            services?.Dispose();
            foreach (var db in contexts) db.Dispose();
            keeper.Dispose();
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
