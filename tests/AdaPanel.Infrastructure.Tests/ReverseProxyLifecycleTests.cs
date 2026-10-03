using System.Reflection;
using System.Security.Claims;
using AdaPanel.Api.Controllers;
using AdaPanel.Api.Security;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Components;
using AdaPanel.Core.Dtos.Proxy;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Entities;
using AdaPanel.Core.Enums;
using AdaPanel.Core.Interfaces;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Iis;
using AdaPanel.Infrastructure.ReverseProxy;
using AdaPanel.Infrastructure.Services;
using AdaPanel.Infrastructure.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using SiteCollection = AdaPanel.Infrastructure.Tests.SiteNativeLifecycleRecoveryTests.SiteCollection;
using PoolCollection = AdaPanel.Infrastructure.Tests.SiteNativeLifecycleRecoveryTests.PoolCollection;

namespace AdaPanel.Infrastructure.Tests;

public sealed class ReverseProxyLifecycleTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task<Exception?> Capture(Func<Task> action)
    { try { await action(); return null; } catch (Exception error) { return error; } }
    private static async Task Reached(Task signal, Task<Exception?> action)
    {
        var first = await Task.WhenAny(signal, action).WaitAsync(Deadline);
        Assert.True(first == signal, "The real operation failed before the intended wait: " + (action.IsCompleted ? await action : null));
        await signal.WaitAsync(Deadline);
    }
    public static IEnumerable<object[]> CatalogActors() =>
        from operation in new[] { "enable", "rewrite" }
        from change in new[] { "disabled", "deleted", "password", "role", "scope" }
        select new object[] { operation, change };

    [Theory, MemberData(nameof(CatalogActors))]
    public async Task CatalogWaitCannotOutliveTheApprovedActor(string operation, string change)
    {
        using var f = await Fixture.Create(); var entered = Signal(); var release = Signal();
        f.BeforeCatalog = async _ => { entered.TrySetResult(); await release.Task.WaitAsync(Deadline); };
        var action = Capture(() => f.Invoke(operation)); await Reached(entered.Task, action);
        try { await f.ChangeActor(change); } finally { release.TrySetResult(); }
        AssertAuthority(await action.WaitAsync(Deadline)); f.AssertUnchanged();
    }
    public static IEnumerable<object[]> DependencyActors() =>
        from operation in new[] { "enable", "rewrite" }
        from change in new[] { "disabled", "password", "scope" }
        select new object[] { operation, change };

    [Theory, MemberData(nameof(DependencyActors))]
    public async Task RejectedAuthorityCannotStartGlobalComponentInstallation(string operation, string change)
    {
        using var f = await Fixture.Create(); f.ComponentsInstalled = false;
        var entered = Signal(); var release = Signal();
        f.BeforeCatalog = async _ => { entered.TrySetResult(); await release.Task.WaitAsync(Deadline); };
        var action = Capture(() => f.Invoke(operation)); await Reached(entered.Task, action);
        try { await f.ChangeActor(change); } finally { release.TrySetResult(); }
        AssertAuthority(await action.WaitAsync(Deadline)); Assert.Equal(0, f.InstallCalls); f.AssertUnchanged();
    }
    [Theory, InlineData("enable"), InlineData("rewrite")]
    public async Task AuthorityLostDuringInstallationStopsNativeAndXmlPublication(string operation)
    {
        using var f = await Fixture.Create(); f.ComponentsInstalled = false;
        f.BeforeInstall = _ => f.ChangeActor("disabled");
        AssertAuthority(await Capture(() => f.Invoke(operation)));
        Assert.Equal(1, f.InstallCalls); f.AssertUnchanged();
    }
    public static IEnumerable<object[]> QueuedOperations() =>
        from operation in new[] { "enable", "remove", "rewrite" }
        from cancel in new[] { false, true }
        select new object[] { operation, cancel };

    [Theory, MemberData(nameof(QueuedOperations))]
    public async Task IisQueueRechecksAuthorityAndCancellation(string operation, bool cancel)
    {
        using var f = await Fixture.Create(); using var cts = new CancellationTokenSource(Deadline);
        var held = Signal(); var release = Signal();
        var blocker = f.Gates.RunExclusiveAsync(async () => { held.TrySetResult(); await release.Task.WaitAsync(Deadline); });
        await held.Task.WaitAsync(Deadline);
        var action = Capture(() => f.Invoke(operation, cts.Token)); await Reached(f.SiteRead.Task, action);
        try { if (cancel) cts.Cancel(); else await f.ChangeActor("disabled"); }
        finally { release.TrySetResult(); await blocker; }
        var error = await action.WaitAsync(Deadline);
        if (cancel) Assert.IsAssignableFrom<OperationCanceledException>(error); else AssertAuthority(error);
        f.AssertUnchanged();
    }
    [Theory, InlineData("enable", false), InlineData("enable", true), InlineData("rewrite", false), InlineData("rewrite", true)]
    public async Task FinalIisWaitAfterInstallationRevalidatesAndReleasesItsPin(string operation, bool cancel)
    {
        using var f = await Fixture.Create(); f.ComponentsInstalled = false;
        using var cts = new CancellationTokenSource(Deadline); var held = Signal(); var release = Signal();
        Task? blocker = null;
        f.BeforeInstall = async _ =>
        {
            f.BeforeInstall = null;
            blocker = f.Gates.RunExclusiveAsync(async () => { held.TrySetResult(); await release.Task.WaitAsync(Deadline); });
            await held.Task.WaitAsync(Deadline);
        };
        var action = Capture(() => f.Invoke(operation, cts.Token)); await Reached(held.Task, action);
        try { if (cancel) cts.Cancel(); else await f.ChangeActor("disabled"); }
        finally { release.TrySetResult(); if (blocker is not null) await blocker; }
        var error = await action.WaitAsync(Deadline);
        if (cancel) Assert.IsAssignableFrom<OperationCanceledException>(error); else AssertAuthority(error);
        Assert.Equal(1, f.InstallCalls); f.AssertUnchanged();
        await f.Native.UpdatePhysicalPathAsync(f.Name, f.OtherPath);
    }
    public static IEnumerable<object[]> CatalogSites() =>
        from operation in new[] { "enable", "rewrite" }
        from change in new[] { "id", "path", "same-id" }
        select new object[] { operation, change };

    [Theory, MemberData(nameof(CatalogSites))]
    public async Task CatalogWaitCannotAdoptAReplacementGeneration(string operation, string change)
    {
        using var f = await Fixture.Create(); var entered = Signal(); var release = Signal();
        f.BeforeCatalog = async _ => { entered.TrySetResult(); await release.Task.WaitAsync(Deadline); };
        var action = Capture(() => f.Invoke(operation)); await Reached(entered.Task, action);
        try
        {
            if (change == "id") f.Sites.Single().Id += 100;
            else if (change == "path") await f.Native.UpdatePhysicalPathAsync(f.Name, f.OtherPath);
            else { f.ReuseNextSiteId(); await f.Native.CreateSiteAsync(f.Request()); }
        }
        finally { release.TrySetResult(); }
        Assert.Equal("sites.target_changed", Assert.IsType<AppException>(await action.WaitAsync(Deadline)).MessageKey);
        f.AssertUnchanged(); Assert.False(File.Exists(Path.Combine(f.OtherPath, "web.config")));
    }
    [Theory, InlineData("enable"), InlineData("remove"), InlineData("rewrite")]
    public async Task ReplacementQueuedBeforeFirstCaptureCannotBecomeItsApprovedSite(string operation)
    {
        using var f = await Fixture.Create(); f.ReuseNextSiteId();
        var held = Signal(); var release = Signal();
        var blocker = f.Gates.RunExclusiveAsync(async () => { held.TrySetResult(); await release.Task.WaitAsync(Deadline); });
        await held.Task.WaitAsync(Deadline);
        var replacement = f.Native.CreateSiteAsync(f.Request());
        var action = Capture(() => f.Invoke(operation)); await Reached(f.SiteRead.Task, action);
        release.TrySetResult(); await blocker; await replacement.WaitAsync(Deadline);
        Assert.Equal("sites.target_changed", Assert.IsType<AppException>(await action.WaitAsync(Deadline)).MessageKey);
        f.AssertUnchanged();
    }
    [Theory, InlineData("enable"), InlineData("remove"), InlineData("rewrite")]
    public async Task OriginalSiteSurvivesTheFirstControllerScopeWait(string operation)
    {
        using var f = await Fixture.Create(); var entered = Signal(); var release = Signal();
        f.BeforeScope = async () => { entered.TrySetResult(); await release.Task.WaitAsync(Deadline); };
        var action = Capture(() => f.Invoke(operation)); await Reached(entered.Task, action);
        try { f.ReuseNextSiteId(); await f.Native.CreateSiteAsync(f.Request()); }
        finally { release.TrySetResult(); }
        Assert.Equal("sites.target_changed", Assert.IsType<AppException>(await action.WaitAsync(Deadline)).MessageKey);
        f.AssertUnchanged();
    }
    public static IEnumerable<object[]> AllowedRoles() =>
        from operation in new[] { "enable", "remove", "rewrite" }
        from role in new[] { UserRole.Administrator, UserRole.Reseller }
        select new object[] { operation, role };

    [Theory, MemberData(nameof(AllowedRoles))]
    public async Task CurrentAuthorizedActorStillPublishesAndAudits(string operation, UserRole role)
    {
        using var f = await Fixture.Create(role);
        var result = await f.Invoke(operation);
        Assert.Equal(f.Name, result.SiteName); Assert.Single(f.Audits); Assert.Equal(1, f.ProxyCommits);
        Assert.NotEqual(Fixture.OriginalXml, File.ReadAllText(f.WebConfig));
        await f.Native.UpdatePhysicalPathAsync(f.Name, f.OtherPath);
    }
    public static IEnumerable<object[]> DeniedRoles() =>
        from operation in new[] { "enable", "remove", "rewrite" }
        from role in new[] { UserRole.Customer, UserRole.Operator }
        select new object[] { operation, role };

    [Theory, MemberData(nameof(DeniedRoles))]
    public async Task DirectControllerCallsCannotBypassTheRoleBoundary(string operation, UserRole role)
    {
        using var f = await Fixture.Create(role);
        AssertAuthority(await Capture(() => f.Invoke(operation))); f.AssertUnchanged(); Assert.Equal(0, f.InstallCalls);
    }
    [Theory, InlineData("enable"), InlineData("remove"), InlineData("rewrite")]
    public async Task CancellationBeforeStartingCannotPublishOrAudit(string operation)
    {
        using var f = await Fixture.Create(); using var cts = new CancellationTokenSource(); cts.Cancel();
        Assert.IsAssignableFrom<OperationCanceledException>(await Capture(() => f.Invoke(operation, cts.Token)));
        f.AssertUnchanged(); Assert.Equal(0, f.InstallCalls);
    }
    [Theory, InlineData("enable"), InlineData("rewrite")]
    public async Task SiteStaysPinnedDuringInstallationAndIsReleasedOnSuccess(string operation)
    {
        using var f = await Fixture.Create(); f.ComponentsInstalled = false;
        Exception? replacementError = null;
        f.BeforeInstall = async _ => { replacementError = await Capture(() => f.Native.UpdatePhysicalPathAsync(f.Name, f.OtherPath)); };
        await f.Invoke(operation);
        Assert.Equal("sites.target_changed", Assert.IsType<AppException>(replacementError).MessageKey);
        Assert.Single(f.Audits); await f.Native.UpdatePhysicalPathAsync(f.Name, f.OtherPath);
    }
    [Theory, InlineData(false), InlineData(true)]
    public async Task TrustedPrerequisiteCallerStillConfiguresArrWithoutWritingSiteXml(bool install)
    {
        using var f = await Fixture.Create(); f.ComponentsInstalled = !install;
        await f.Service.EnsureArrPrerequisitesAsync(f.Name);
        Assert.True(f.ArrEnabled); Assert.Equal(Fixture.OriginalXml, File.ReadAllText(f.WebConfig));
        Assert.Empty(f.Audits); Assert.Equal(install ? 2 : 0, f.InstallCalls);
        await f.Native.UpdatePhysicalPathAsync(f.Name, f.OtherPath);
    }
    private static void AssertAuthority(Exception? error) =>
        Assert.True(Assert.IsType<AppException>(error).HttpStatusCode is 401 or 403);

    private sealed class Fixture : IDisposable
    {
        public const string OriginalXml = "<configuration><appSettings><add key=\"keep\" value=\"secret\" /></appSettings><system.webServer><rewrite><rules><rule name=\"AdaPanel.ReverseProxy.Inbound|root\"><match url=\"(.*)\"/><action type=\"Rewrite\" url=\"http://old/{R:1}\"/></rule></rules></rewrite></system.webServer></configuration>";
        private readonly string root = Path.Combine(Path.GetTempPath(), "AdaPanel.ProxyLifecycle." + Guid.NewGuid().ToString("N"));
        private readonly SqliteConnection keeper = new($"Data Source=Proxy-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Pooling=False");
        private readonly List<AdaPanelDbContext> contexts = [];
        private ServiceProvider services = null!;
        private ProxyController controller = null!;
        private readonly Guid actor = Guid.NewGuid();
        private bool scopeAllowed = true;
        private long nextId = 100;
        public string Name { get; } = "proxy-" + Guid.NewGuid().ToString("N") + ".test";
        public string SitePath => Path.Combine(root, "site");
        public string OtherPath => Path.Combine(root, "other");
        public string WebConfig => Path.Combine(SitePath, "web.config");
        public IConcurrencyLock Gates { get; } = new ConcurrencyLock();
        public SiteCollection Sites { get; private set; } = [];
        public PoolCollection Pools { get; private set; } = [];
        public WebAdministrationSiteService Native { get; }
        public ReverseProxyService Service { get; private set; } = null!;
        public Func<CancellationToken, Task>? BeforeCatalog, BeforeInstall;
        public Func<Task>? BeforeScope;
        public bool ComponentsInstalled = true, ArrEnabled;
        public int InstallCalls, ProxyCommits;
        public List<string> Audits { get; } = [];
        public TaskCompletionSource SiteRead { get; } = Signal();
        private Fixture()
        {
            Native = new WebAdministrationSiteService(Gates, NullLogger<WebAdministrationSiteService>.Instance,
                () => new NativeManager(this), token => { token.ThrowIfCancellationRequested(); return Task.CompletedTask; },
                (_, _, _) => Task.FromResult(new CommandResult()));
        }
        public static async Task<Fixture> Create(UserRole role = UserRole.Reseller)
        {
            var f = new Fixture(); await f.keeper.OpenAsync(); Directory.CreateDirectory(f.OtherPath);
            var db = f.Context(); await db.Database.EnsureCreatedAsync();
            db.Users.Add(new PanelUser { Id = f.actor, UserName = "actor", Email = "actor@example.test", Role = role, IsActive = true, PasswordHash = "original-hash" });
            await db.SaveChangesAsync();
            var scopes = Fake<ICustomerScopeService>((method, _) => method.Name switch
            {
                "GetResellerSitesAsync" or "GetAssignedSitesAsync" => f.ReadScope(),
                "GetParentSubscriptionAsync" or "GetSiteOwnerUserIdAsync" => Task.FromResult<Guid?>(null),
                _ => throw new InvalidOperationException("Unexpected scope: " + method.Name)
            });
            var iis = Fake<IIisSiteService>((method, args) => method.Name == "GetSiteAsync"
                ? f.ReadSite((string)args[0]!, (CancellationToken)args[1]!) : throw new InvalidOperationException("Unexpected IIS call"));
            var bootstrapper = Fake<IComponentBootstrapper>((method, args) => method.Name switch
            {
                "GetCatalogAsync" => f.ReadCatalog((CancellationToken)args[0]!),
                "InstallAsync" => f.Install((string)args[0]!, (CancellationToken)args[4]!),
                _ => throw new InvalidOperationException("Unexpected bootstrapper: " + method.Name)
            });
            f.Service = new ReverseProxyService(iis, bootstrapper, f.Gates, NullLogger<ReverseProxyService>.Instance)
                { ManagerFactory = () => new ProxyManager(f) };
            f.services = new ServiceCollection().AddSingleton(f.Context()).AddSingleton(iis).AddSingleton(f.Gates).AddSingleton(scopes).BuildServiceProvider();
            f.controller = new ProxyController(f.Service, scopes, Fake<IAuditService>((_, _) => { f.Audits.Add("success"); return Task.CompletedTask; }))
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext
                {
                    RequestServices = f.services,
                    User = new ClaimsPrincipal(new ClaimsIdentity([
                        new Claim(ClaimTypes.NameIdentifier, f.actor.ToString()), new Claim(ClaimTypes.Role, role.ToString()),
                        new Claim(CookieSessionValidator.CredentialStampClaim, CookieSessionValidator.ComputeStamp("original-hash"))], "fixture"))
                } }
            };
            await f.Native.CreateSiteAsync(f.Request()); File.WriteAllText(f.WebConfig, OriginalXml); return f;
        }
        private AdaPanelDbContext Context()
        { var db = new AdaPanelDbContext(new DbContextOptionsBuilder<AdaPanelDbContext>().UseSqlite(keeper.ConnectionString).Options); contexts.Add(db); return db; }
        public CreateSiteRequest Request() => new() { Name = Name, HostName = Name, PhysicalPath = SitePath, Port = 8080 };
        public void ReuseNextSiteId() => nextId = Sites.Single().Id - 1;
        private async Task<SiteDto> ReadSite(string name, CancellationToken token)
        { var site = await Native.GetSiteAsync(name, token); SiteRead.TrySetResult(); return site; }
        private async Task<IReadOnlyCollection<string>> ReadScope()
        { if (BeforeScope is not null) await BeforeScope(); return scopeAllowed ? [Name] : []; }
        private async Task<SoftwareCatalogDto> ReadCatalog(CancellationToken token)
        {
            if (BeforeCatalog is not null) await BeforeCatalog(token);
            return new SoftwareCatalogDto { Installed = (ComponentsInstalled ? new[] { "url-rewrite", "iis-arr" } : [])
                .Select(id => new InstalledComponentDto { ComponentId = id, Status = "Installed" }).ToArray() };
        }
        private async Task<ComponentInstallResultDto> Install(string componentId, CancellationToken token)
        {
            InstallCalls++; if (BeforeInstall is not null) await BeforeInstall(token);
            return new ComponentInstallResultDto { ComponentId = componentId, Status = "Installed" };
        }
        public async Task<ReverseProxySiteConfigurationDto> Invoke(string operation, CancellationToken token = default)
        {
            var result = operation switch
            {
                "enable" => await controller.EnableProxyAsync(Name, new EnableReverseProxyRequest { DestinationUrl = "http://127.0.0.1:3000", MountPath = "/app" }, token),
                "remove" => await controller.RemoveProxyAsync(Name, token),
                _ => await controller.AddRewriteRuleAsync(Name, new AddRewriteRuleRequest { Name = "new-route", MatchPattern = "^new$", RewriteUrl = "new.php" }, token)
            };
            return Assert.IsType<ApiResponse<ReverseProxySiteConfigurationDto>>(Assert.IsType<OkObjectResult>(result.Result).Value).Data!;
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
        { Assert.Equal(OriginalXml, File.ReadAllText(WebConfig)); Assert.Equal(0, ProxyCommits); Assert.Empty(Audits); Assert.False(ArrEnabled); }
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
        public sealed class ProxyManager(Fixture f) : IDisposable
        {
            public ProxySection Section { get; } = new(f.ArrEnabled);
            public ProxyManager GetApplicationHostConfiguration() => this;
            public ProxyManager GetWebConfiguration(string siteName) => this;
            public ProxySection GetSection(string name) => Section;
            public void CommitChanges() { f.ProxyCommits++; f.ArrEnabled = Section.Enabled; }
            public void Dispose() { }
        }
        public sealed class ProxySection(bool enabled)
        {
            public bool Enabled = enabled;
            public object GetAttributeValue(string name) => name == "enabled" && Enabled;
            public void SetAttributeValue(string name, object value) { if (name == "enabled") Enabled = Convert.ToBoolean(value); }
        }
        public void Dispose()
        { services?.Dispose(); foreach (var db in contexts) db.Dispose(); keeper.Dispose(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private static T Fake<T>(Func<MethodInfo, object?[], object?> invoke) where T : class
    { var result = DispatchProxy.Create<T, Proxy>(); ((Proxy)(object)result).Call = invoke; return result; }
    public class Proxy : DispatchProxy
    {
        public Func<MethodInfo, object?[], object?> Call { get; set; } = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => Call(method!, args ?? []);
    }
}
