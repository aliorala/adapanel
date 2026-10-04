using System.Reflection;
using System.Security.Claims;
using AdaPanel.Api.Controllers;
using AdaPanel.Api.Security;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Files;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Dtos.Users;
using AdaPanel.Core.Entities;
using AdaPanel.Core.Enums;
using AdaPanel.Core.Interfaces;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Files;
using AdaPanel.Infrastructure.Iis;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Security;
using AdaPanel.Infrastructure.Support;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class FileDirectoryPermissionAuthorityTests
{
    public static IEnumerable<object[]> ActorChanges() =>
        from route in new[] { "mkdir", "acl" }
        from change in new[] { "disabled", "deleted", "role", "password", "logout" }
        select new object[] { route, change };

    [Theory]
    [MemberData(nameof(ActorChanges))]
    public async Task RevocationDuringSiteResolutionPreventsTheMutation(string route, string change)
    {
        using var f = await Fixture.Create(route);
        f.Pause = "site";
        var pending = Record.ExceptionAsync(() => f.Run());
        await f.WaitForBoundary(pending);
        try { await f.ChangeActor(change); }
        finally { f.Release.TrySetResult(); }
        Assert.True(Assert.IsType<AppException>(await pending.WaitAsync(f.Token)).HttpStatusCode is 401 or 403);
        f.AssertNoEffect();
    }

    [Theory]
    [InlineData("scope")]
    [InlineData("section")]
    [InlineData("parent")]
    [InlineData("suspended")]
    public async Task AQueuedDirectoryCreationRechecksTheSubscription(string change)
    {
        using var f = await Fixture.Create("mkdir");
        f.Pause = "site";
        var pending = Record.ExceptionAsync(() => f.Run());
        await f.WaitForBoundary(pending);
        try { await f.ChangeActor(change); }
        finally { f.Release.TrySetResult(); }
        Assert.Equal(403, Assert.IsType<AppException>(await pending.WaitAsync(f.Token)).HttpStatusCode);
        f.AssertNoEffect();
    }

    public static IEnumerable<object[]> SiteChanges() =>
        from route in new[] { "mkdir", "acl" }
        from change in new[] { "id", "path", "version" }
        select new object[] { route, change };

    [Theory]
    [MemberData(nameof(SiteChanges))]
    public async Task ResolutionCannotReplaceTheAcceptedSiteGeneration(string route, string change)
    {
        using var f = await Fixture.Create(route);
        f.Pause = "site";
        var pending = Record.ExceptionAsync(() => f.Run());
        await f.WaitForBoundary(pending);
        try { await f.ReplaceSite(change); }
        finally { f.Release.TrySetResult(); }
        Assert.Equal("sites.target_changed", Assert.IsType<AppException>(await pending.WaitAsync(f.Token)).MessageKey);
        f.AssertNoEffect();
    }

    [Fact]
    public async Task SiteIntentPrecedesTheFirstDirectoryScopeAwait()
    {
        using var f = await Fixture.Create("mkdir");
        f.Pause = "scope";
        var pending = Record.ExceptionAsync(() => f.Run());
        await f.WaitForBoundary(pending);
        try { await f.ReplaceSite("path"); }
        finally { f.Release.TrySetResult(); }
        Assert.Equal("sites.target_changed", Assert.IsType<AppException>(await pending.WaitAsync(f.Token)).MessageKey);
        f.AssertNoEffect();
    }

    [Theory]
    [InlineData("mkdir")]
    [InlineData("acl")]
    public async Task RequestFieldsAndAuditTargetAreSnapshottedBeforeAwait(string route)
    {
        using var f = await Fixture.Create(route);
        f.Pause = route == "mkdir" ? "scope" : "site";
        var pending = f.Run();
        await f.WaitForBoundary(pending);
        f.DirectoryRequest.Path = $"sites/{f.SiteName}/unselected";
        f.PermissionRequest.Path = $"sites/{f.SiteName}/unselected";
        f.PermissionRequest.Principal = "Everyone";
        f.PermissionRequest.Rights = "F";
        f.Release.TrySetResult();
        await pending.WaitAsync(f.Token);
        if (route == "mkdir") Assert.True(Directory.Exists(f.CreatedPath));
        else
        {
            Assert.Contains("OriginalUser:RX", Assert.Single(f.Commands));
            Assert.DoesNotContain("Everyone", Assert.Single(f.Commands));
        }
        Assert.False(Directory.Exists(Path.Combine(f.SiteRoot, "unselected")));
        var audit = Assert.Single(f.Audits);
        Assert.Equal(f.RequestedPath, audit.Resource);
        Assert.DoesNotContain("Everyone", audit.Details);
    }

    [Theory]
    [InlineData("mkdir")]
    [InlineData("acl")]
    public async Task AReplacementPrincipalDoesNotReplaceTheAcceptedActor(string route)
    {
        using var f = await Fixture.Create(route);
        f.Pause = "site";
        var pending = Record.ExceptionAsync(() => f.Run());
        await f.WaitForBoundary(pending);
        try { await f.ChangeActor("logout"); f.ReplacePrincipal(); }
        finally { f.Release.TrySetResult(); }
        Assert.Equal(401, Assert.IsType<AppException>(await pending.WaitAsync(f.Token)).HttpStatusCode);
        f.AssertNoEffect();
    }

    public static IEnumerable<object[]> PostEffectChanges() =>
        from route in new[] { "mkdir", "acl" }
        from change in new[] { "logout", "password" }
        select new object[] { route, change };

    [Theory]
    [MemberData(nameof(PostEffectChanges))]
    public async Task AnAppliedMutationCannotPublishSuccessForARevokedActor(string route, string change)
    {
        using var f = await Fixture.Create(route);
        f.Pause = "after-effect";
        var pending = Record.ExceptionAsync(() => f.Run());
        await f.WaitForBoundary(pending);
        try { await f.ChangeActor(change); }
        finally { f.Release.TrySetResult(); }
        Assert.Equal(401, Assert.IsType<AppException>(await pending.WaitAsync(f.Token)).HttpStatusCode);
        Assert.True(f.HasEffect); // No claim that already-applied filesystem/ACL work was undone.
        Assert.Empty(f.Audits);
    }

    [Theory]
    [InlineData("mkdir", "site")]
    [InlineData("acl", "site")]
    [InlineData("mkdir", "after-effect")]
    [InlineData("acl", "after-effect")]
    public async Task CancellationAtAnIgnoringBoundaryDoesNotPublishSuccess(string route, string boundary)
    {
        using var f = await Fixture.Create(route);
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(f.Token);
        f.Pause = boundary;
        var pending = Record.ExceptionAsync(() => f.Run(canceled.Token));
        await f.WaitForBoundary(pending);
        canceled.Cancel(); f.Release.TrySetResult();
        Assert.IsAssignableFrom<OperationCanceledException>(await pending.WaitAsync(f.Token));
        Assert.Empty(f.Audits);
        if (boundary == "site") Assert.False(f.HasEffect);
    }

    [Fact]
    public async Task TheSiteStaysPinnedThroughoutTheNativePermissionCommand()
    {
        using var f = await Fixture.Create("acl");
        f.Pause = "native";
        var pending = f.Run();
        await f.WaitForBoundary(pending);
        Exception? competing;
        try { competing = await Record.ExceptionAsync(() => f.ReplaceSite("path")); }
        finally { f.Release.TrySetResult(); }
        await pending.WaitAsync(f.Token);
        Assert.Equal("sites.target_changed", Assert.IsType<AppException>(competing).MessageKey);
        await f.ReplaceSite("path"); // Pin and entry are released after completion.
    }

    [Theory]
    [InlineData("password")]
    [InlineData("logout")]
    [InlineData("disabled")]
    public async Task DelegatedDirectoryCreationRechecksTheOriginalAdministrator(string change)
    {
        using var f = await Fixture.Create("mkdir", "delegated");
        f.Pause = "site";
        var pending = Record.ExceptionAsync(() => f.Run());
        await f.WaitForBoundary(pending);
        try { await f.ChangeDelegator(change); }
        finally { f.Release.TrySetResult(); }
        Assert.Equal(401, Assert.IsType<AppException>(await pending.WaitAsync(f.Token)).HttpStatusCode);
        f.AssertNoEffect();
    }

    [Theory]
    [InlineData("mkdir", "cookie")]
    [InlineData("mkdir", "token")]
    [InlineData("mkdir", "delegated")]
    [InlineData("mkdir", "administrator")]
    [InlineData("mkdir", "reseller")]
    [InlineData("acl", "cookie")]
    [InlineData("acl", "token")]
    public async Task CurrentSupportedActorsStillCompleteTheRequestedOperation(string route, string identity)
    {
        using var f = await Fixture.Create(route, identity);
        var response = await f.Run();
        Assert.True(f.HasEffect);
        Assert.Single(f.Audits);
        Assert.Equal(route == "mkdir" ? "files.directory_created" : "files.acl_updated", response.Message);
        if (route == "mkdir")
            Assert.Equal(identity == "administrator" ? f.CreatedPath : string.Empty, response.Data!.PhysicalPath);
        Assert.Equal("ORIGINAL", File.ReadAllText(Path.Combine(f.SiteRoot, "existing.txt")));
    }

    [Theory]
    [InlineData(0, "Successfully processed 1 files; Failed processing 1 files")]
    [InlineData(0, "Successfully processed 0 files; Failed processing 0 files")]
    [InlineData(1, "Successfully processed 1 files; Failed processing 0 files")]
    public async Task FailedOrEmptyPermissionProcessingCannotBeReportedAsCompleted(int exitCode, string output)
    {
        using var f = await Fixture.Create("acl");
        f.NativeResult = new CommandResult { ExitCode = exitCode, StandardOutput = output };
        var error = await Record.ExceptionAsync(() => f.Run());
        Assert.Equal("files.acl_failed", Assert.IsType<AppException>(error).MessageKey);
        Assert.Single(f.Commands);
        Assert.Empty(f.Audits);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1 dosya başarıyla işlendi; 0 dosya işlenemedi")]
    [InlineData("Successfully processed 1 files; Failed processing unknown files")]
    public async Task UnrecognizedPermissionOutputIsExplicitlyUnmeasured(string output)
    {
        using var f = await Fixture.Create("acl");
        f.NativeResult = new CommandResult { ExitCode = 0, StandardOutput = output + "\npassword=do-not-publish-this" };
        var response = await f.Run();
        Assert.Null(response.Data!.PermissionsVerified);
        Assert.Equal("files.acl_unverified", response.Message);
        Assert.NotEmpty(response.Data.PermissionsDetail);
        Assert.True(response.Data.PermissionsDetail.Length <= 1024);
        Assert.DoesNotContain("do-not-publish-this", response.Data.PermissionsDetail);
        Assert.Equal("files.acl_unverified", Assert.Single(f.Audits).Action);
    }

    [Fact]
    public async Task KnownSuccessfulProcessingIsMeasuredWithoutClaimingEffectiveAccess()
    {
        using var f = await Fixture.Create("acl");
        var response = await f.Run();
        Assert.True(response.Data!.PermissionsVerified);
        Assert.NotEmpty(response.Data.PermissionsDetail);
        // Ordinary entries are measured first and processed without native recursion.
        Assert.EndsWith(" /C /L", Assert.Single(f.Commands));
        Assert.DoesNotContain(" /T ", Assert.Single(f.Commands));
        var read = await f.Files.GetWorkspaceAsync(f.RequestedPath, f.Token);
        Assert.Null(read.PermissionsVerified);
        Assert.Empty(read.PermissionsDetail);
    }

    [Fact]
    public async Task NativeCancellationRemainsCancellationAndDoesNotAuditSuccess()
    {
        using var f = await Fixture.Create("acl");
        f.Files.PermissionCommand = (_, _, token) => Task.FromException<CommandResult>(new OperationCanceledException(token));
        Assert.IsAssignableFrom<OperationCanceledException>(await Record.ExceptionAsync(() => f.Run()));
        Assert.Empty(f.Audits);
    }

    [Theory]
    [InlineData("bad\" /deny", "RX", "files.invalid_principal")]
    [InlineData("OriginalUser", "RX /remove", "files.invalid_rights")]
    public async Task PermissionInputValidationStillPrecedesNativeExecution(string principal, string rights, string key)
    {
        using var f = await Fixture.Create("acl");
        f.PermissionRequest.Principal = principal; f.PermissionRequest.Rights = rights;
        Assert.Equal(key, Assert.IsType<AppException>(await Record.ExceptionAsync(() => f.Run())).MessageKey);
        f.AssertNoEffect();
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "adapanel-file-directory-permission-" + Guid.NewGuid().ToString("N"));
        private readonly SqliteConnection connection = new("Data Source=:memory:");
        private readonly CancellationTokenSource lifetime = new(TimeSpan.FromSeconds(30));
        private readonly Guid actor = Guid.NewGuid(), owner = Guid.NewGuid(), other = Guid.NewGuid(), delegator = Guid.NewGuid();
        private readonly string sid = SessionRevocationStore.NewSessionId(), sourceSid = SessionRevocationStore.NewSessionId();
        private readonly AdaPanelDbContext db;
        private readonly CustomerScopeService scopes;
        private readonly SessionRevocationStore sessions;
        private readonly ServiceProvider services;
        private readonly FilesController controller;
        private readonly FilePublicationAuthorityTests.LocalGate gates = new();
        private readonly string route, foreignRoot;
        private string resolvedRoot;
        private long siteId = 12;
        private int paused;
        public string SiteName { get; } = "mutation-" + Guid.NewGuid().ToString("N") + ".test";
        public string SiteRoot { get; }
        public string CreatedPath => Path.Combine(SiteRoot, "created");
        public string RequestedPath => $"sites/{SiteName}/" + (route == "mkdir" ? "created" : "existing.txt");
        public CancellationToken Token => lifetime.Token;
        public string? Pause;
        public FileService Files { get; }
        public CreateDirectoryRequest DirectoryRequest { get; }
        public SetFilePermissionsRequest PermissionRequest { get; }
        public List<string> Commands { get; } = [];
        public List<(string Action, string Resource, string Details)> Audits { get; } = [];
        public CommandResult NativeResult = new() { ExitCode = 0, StandardOutput = "Successfully processed 1 files; Failed processing 0 files" };
        public bool HasEffect => Directory.Exists(CreatedPath) || Directory.Exists(Path.Combine(foreignRoot, "created")) || Commands.Count > 0;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private Fixture(string route, string identity)
        {
            this.route = route;
            SiteRoot = resolvedRoot = Path.Combine(root, "site"); foreignRoot = Path.Combine(root, "foreign");
            Directory.CreateDirectory(SiteRoot); Directory.CreateDirectory(foreignRoot);
            File.WriteAllText(Path.Combine(SiteRoot, "existing.txt"), "ORIGINAL");
            File.WriteAllText(Path.Combine(foreignRoot, "existing.txt"), "FOREIGN");
            DirectoryRequest = new() { Path = RequestedPath };
            PermissionRequest = new() { Path = RequestedPath, Principal = "OriginalUser", Rights = "RX" };
            connection.Open();
            db = new AdaPanelDbContext(new DbContextOptionsBuilder<AdaPanelDbContext>().UseSqlite(connection).Options);
            db.Database.EnsureCreated();
            var user = User(actor, "actor", route == "acl" || identity == "administrator" ? UserRole.Administrator
                : identity == "reseller" ? UserRole.Reseller : UserRole.Customer);
            db.Users.AddRange(user, User(owner, "owner"), User(other, "other", UserRole.Administrator), User(delegator, "delegator", UserRole.Administrator));
            db.SaveChanges();
            var store = new JsonFileStore(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = Path.Combine(root, "panel") }));
            var sites = Fake<IIisSiteService>((method, _) => method.Name switch
            {
                nameof(IIisSiteService.GetSiteAsync) => ReadSite(),
                nameof(IIisSiteService.GetSitesAsync) => Task.FromResult<IReadOnlyCollection<SiteDto>>([Site()]),
                _ => throw new NotSupportedException(method.Name)
            });
            scopes = new CustomerScopeService(store, Fake<IServicePlanService>(),
                Fake<IResellerQuotaService>((method, _) => method.Name == nameof(IResellerQuotaService.GetQuotaAsync)
                    ? Task.FromResult<ResellerQuotaDto?>(null) : throw new NotSupportedException(method.Name)), sites);
            var proxy = Fake<ICustomerScopeService>((method, args) => method.Name == nameof(ICustomerScopeService.GetAssignedSitesAsync)
                ? ReadScope((Guid)args![0]!, (CancellationToken)args[1]!) : method.Invoke(scopes, args));
            sessions = new SessionRevocationStore(store);
            Files = new FileService(sites) { PermissionCommand = ExecutePermission };
            services = new ServiceCollection().AddSingleton(db).AddSingleton(sessions)
                .AddSingleton<IIisSiteService>(sites).AddSingleton<IConcurrencyLock>(gates).BuildServiceProvider();
            var claims = Claims(actor, user.Role, sid).ToList();
            if (identity == "delegated") claims.AddRange([
                new Claim("impersonator_id", delegator.ToString()), new Claim("impersonator_name", "delegator"),
                new Claim(CookieSessionValidator.DelegatingCredentialStampClaim, CookieSessionValidator.ComputeStamp("original")),
                new Claim(CookieSessionValidator.DelegatingSessionIdClaim, sourceSid)]);
            var audit = Fake<IAuditService>((method, args) =>
            {
                if (method.Name is not (nameof(IAuditService.WriteAsync) or nameof(IAuditService.WriteForSubscriptionAsync))) throw new NotSupportedException(method.Name);
                Audits.Add(((string)args![0]!, (string)args[1]!, (string)args[2]!)); return Task.CompletedTask;
            });
            controller = new FilesController(Files, proxy, audit, new ConcurrentWorkGate())
            { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { RequestServices = services,
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, identity == "token" ? PanelTokenAuthenticationHandler.SchemeName : CookieAuthenticationDefaults.AuthenticationScheme)) } } };
        }
        public static async Task<Fixture> Create(string route, string identity = "cookie")
        {
            var f = new Fixture(route, identity);
            await f.scopes.SetScopeAsync(f.owner, [f.SiteName], 0, 0, f.Token);
            if (route == "mkdir" && identity is not ("administrator" or "reseller"))
                await f.scopes.SetParentSubscriptionAsync(f.actor, f.owner, f.Token);
            if (identity == "reseller") await f.scopes.SetOwnerAsync(f.owner, f.actor, f.Token);
            await f.scopes.SetSubUserSectionsAsync(f.actor, ["files"], f.Token);
            return f;
        }
        private static PanelUser User(Guid id, string name, UserRole role = UserRole.Customer) => new()
        { Id = id, UserName = name, Email = name + "@test.invalid", Role = role, IsActive = true, PasswordHash = "original" };
        private static Claim[] Claims(Guid id, UserRole role, string session) =>
        [new(ClaimTypes.NameIdentifier, id.ToString()), new(ClaimTypes.Role, role.ToString()),
            new(CookieSessionValidator.CredentialStampClaim, CookieSessionValidator.ComputeStamp("original")), new(SessionRevocationStore.SessionIdClaim, session)];
        private SiteDto Site() => new() { Name = SiteName, Id = siteId, PhysicalPath = resolvedRoot,
            ApplicationRootPath = resolvedRoot, ApplicationPoolName = SiteName };
        private async Task<SiteDto> ReadSite()
        {
            var snapshot = Site();
            if (HasEffect) await Boundary("after-effect");
            await Boundary("site"); return snapshot;
        }
        private async Task<IReadOnlyCollection<string>> ReadScope(Guid user, CancellationToken token)
        { var snapshot = await scopes.GetAssignedSitesAsync(user, token); await Boundary("scope"); return snapshot; }
        private async Task<CommandResult> ExecutePermission(string file, string arguments, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); // The real ProcessRunner also checks before starting a process.
            Assert.Equal("icacls.exe", file); Commands.Add(arguments); await Boundary("native"); return NativeResult;
        }
        private async Task Boundary(string boundary)
        {
            if (Pause != boundary || Interlocked.Exchange(ref paused, 1) != 0) return;
            Entered.TrySetResult(); await Release.Task.WaitAsync(Token);
        }
        public async Task WaitForBoundary(Task pending) => Assert.Same(Entered.Task, await Task.WhenAny(Entered.Task, pending).WaitAsync(Token));
        public async Task<ApiResponse<FileWorkspaceDto>> Run(CancellationToken? cancellation = null)
        {
            var token = cancellation ?? Token;
            var result = route == "mkdir" ? await controller.CreateDirectoryAsync(DirectoryRequest, token)
                : await controller.SetPermissionsAsync(PermissionRequest, token);
            return Assert.IsType<ApiResponse<FileWorkspaceDto>>(Assert.IsType<OkObjectResult>(result.Result).Value);
        }
        public async Task ChangeActor(string change)
        {
            switch (change)
            {
                case "disabled": await db.Users.Where(u => u.Id == actor).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, false), Token); break;
                case "deleted": await db.Users.Where(u => u.Id == actor).ExecuteDeleteAsync(Token); break;
                case "role": await db.Users.Where(u => u.Id == actor).ExecuteUpdateAsync(s => s.SetProperty(u => u.Role, route == "acl" ? UserRole.Customer : UserRole.Administrator), Token); break;
                case "password": await db.Users.Where(u => u.Id == actor).ExecuteUpdateAsync(s => s.SetProperty(u => u.PasswordHash, "changed"), Token); break;
                case "logout": await sessions.RevokeAsync(sid, Token); break;
                case "scope": await scopes.SetScopeAsync(owner, [], 0, 0, Token); break;
                case "section": await scopes.SetSubUserSectionsAsync(actor, [], Token); break;
                case "parent": await scopes.SetParentSubscriptionAsync(actor, other, Token); break;
                case "suspended": await scopes.SuspendAsync(owner, "manual", Token); break;
                default: throw new NotSupportedException(change);
            }
        }
        public Task ChangeDelegator(string change) => change == "logout" ? sessions.RevokeAsync(sourceSid, Token)
            : change == "password" ? db.Users.Where(u => u.Id == delegator).ExecuteUpdateAsync(s => s.SetProperty(u => u.PasswordHash, "changed"), Token)
            : db.Users.Where(u => u.Id == delegator).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, false), Token);
        public void ReplacePrincipal() => controller.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
            Claims(other, UserRole.Administrator, SessionRevocationStore.NewSessionId()), CookieAuthenticationDefaults.AuthenticationScheme));
        public Task ReplaceSite(string change) => SiteMutationGuard.RunMutationAsync(gates, SiteName, SiteMutationGuard.MutationKind.IdentityChange, () =>
        { if (change == "id") siteId++; if (change == "path") resolvedRoot = foreignRoot; return Task.FromResult(true); }, Token);
        public void AssertNoEffect()
        {
            Assert.False(HasEffect); Assert.Empty(Audits);
            Assert.Equal("ORIGINAL", File.ReadAllText(Path.Combine(SiteRoot, "existing.txt")));
            Assert.Equal("FOREIGN", File.ReadAllText(Path.Combine(foreignRoot, "existing.txt")));
        }
        public void Dispose()
        { Release.TrySetResult(); services.Dispose(); db.Dispose(); connection.Dispose(); gates.Dispose(); lifetime.Dispose(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static T Fake<T>(Func<MethodInfo, object?[]?, object?>? handler = null) where T : class
    { var proxy = DispatchProxy.Create<T, Proxy>(); ((Proxy)(object)proxy).Handler = handler ?? ((method, _) => throw new NotSupportedException(method.Name)); return proxy; }
    public class Proxy : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?> Handler { get; set; } = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => Handler(method!, args);
    }
}
