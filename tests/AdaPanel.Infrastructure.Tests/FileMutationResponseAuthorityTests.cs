using System.Reflection;
using System.Security.Claims;
using System.Text;
using AdaPanel.Api.Controllers;
using AdaPanel.Api.Security;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Files;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Entities;
using AdaPanel.Core.Enums;
using AdaPanel.Core.Interfaces;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Files;
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

public sealed class FileMutationResponseAuthorityTests
{
    public static IEnumerable<object[]> AuditChanges =>
        from route in new[] { "write", "delete", "upload" }
        from change in new[] { "logout", "password", "inactive", "scope", "cancel" }
        select new object[] { route, change };

    [Theory]
    [MemberData(nameof(AuditChanges))]
    public async Task AnAuditWaitCannotOutliveTheAcceptedResponseAuthority(string route, string change)
    {
        using var fixture = new Fixture(route);
        fixture.BeforeAuditCompletes = () => fixture.ChangeAuthority(change);
        var error = await Record.ExceptionAsync(() => fixture.Invoke());
        AssertRejection(error, change);
        Assert.Equal(1, fixture.Audits);
        // An accepted filesystem effect already occurred; rejecting this response does not undo it.
        fixture.AssertCompletedEffect();
        fixture.AssertNoTemporaryFiles();
    }

    [Theory]
    [InlineData("write", UserRole.Customer)]
    [InlineData("write", UserRole.Reseller)]
    [InlineData("write", UserRole.Administrator)]
    [InlineData("delete", UserRole.Customer)]
    [InlineData("delete", UserRole.Reseller)]
    [InlineData("delete", UserRole.Administrator)]
    public async Task ACompletedMutationReturnsOnlyTheAllowedWorkspace(string route, UserRole role)
    {
        using var fixture = new Fixture(route, role);
        var workspace = Assert.IsType<FileWorkspaceDto>(await fixture.Invoke());
        Assert.Equal(role == UserRole.Administrator
            ? route == "write" ? fixture.Target : fixture.SiteRoot
            : string.Empty, workspace.PhysicalPath);
        Assert.Equal(route == "write" ? fixture.VirtualTarget : fixture.VirtualRoot, workspace.Path);
        if (route == "write")
        {
            Assert.True(workspace.ContentLoaded);
            Assert.Equal("NEW CONTENT", workspace.Content);
        }
        else Assert.DoesNotContain(workspace.Entries, item => item.Name == "target.txt");
        fixture.AssertCompletedEffect();
        Assert.Equal(1, fixture.Audits);
    }

    [Theory]
    [InlineData("logout")]
    [InlineData("password")]
    [InlineData("scope")]
    [InlineData("cancel")]
    public async Task WritingDoesNotAuthorizeTheLaterWorkspaceReadWithoutRechecking(string change)
    {
        using var fixture = new Fixture("write");
        fixture.BeforeWorkspaceRead = () => fixture.ChangeAuthority(change);
        var error = await Record.ExceptionAsync(() => fixture.Invoke());
        AssertRejection(error, change);
        Assert.Equal(1, fixture.WorkspaceBoundaryCalls);
        Assert.Equal(0, fixture.Audits);
        fixture.AssertCompletedEffect();
    }

    [Theory]
    [InlineData("write")]
    [InlineData("delete")]
    public async Task TheActualResolvedWorkspaceCannotBorrowAnotherSnapshotApproval(string route)
    {
        using var fixture = new Fixture(route);
        fixture.ResolveForeignWorkspace = true;
        var error = await Record.ExceptionAsync(() => fixture.Invoke());
        Assert.Equal(409, Assert.IsType<AppException>(error).HttpStatusCode);
        Assert.Equal(1, fixture.WorkspaceBoundaryCalls);
        Assert.Equal(0, fixture.Audits);
        fixture.AssertCompletedEffect();
        Assert.Equal("FOREIGN CONTENT", File.ReadAllText(Path.Combine(fixture.ForeignRoot, "target.txt")));
    }

    [Theory]
    [InlineData("write")]
    [InlineData("upload")]
    public async Task ReplacingTheAmbientPrincipalCannotBypassTheAcceptedCustomersQuota(string route)
    {
        using var fixture = new Fixture(route);
        fixture.UsageMb = 1;
        fixture.BeforeFirstScopeRead = () => fixture.Controller.HttpContext.User =
            Fixture.Principal(Guid.NewGuid(), UserRole.Administrator);
        var error = await Record.ExceptionAsync(() => fixture.Invoke());
        Assert.Equal("files.quota_exceeded", Assert.IsType<AppException>(error).MessageKey);
        Assert.Equal("OLD", File.ReadAllText(fixture.Target));
        Assert.Equal(1, fixture.UsageReads);
        Assert.Equal(0, fixture.Audits);
        fixture.AssertNoTemporaryFiles();
    }

    [Theory]
    [InlineData("write")]
    [InlineData("delete")]
    public async Task TheRequestPathAndContentAreCapturedBeforeScopeWaits(string route)
    {
        using var fixture = new Fixture(route);
        fixture.BeforeFirstScopeRead = () =>
        {
            fixture.WriteRequest.Path = fixture.VirtualRoot + "/keep.txt";
            fixture.WriteRequest.Content = "CHANGED REQUEST";
            fixture.DeleteRequest.Path = fixture.VirtualRoot + "/keep.txt";
        };
        await fixture.Invoke();
        fixture.AssertCompletedEffect();
        Assert.Equal("KEEP", File.ReadAllText(Path.Combine(fixture.SiteRoot, "keep.txt")));
    }

    [Fact]
    public async Task AnUnchangedUploadReturnsItsVirtualPathAndPreservesTheExactBytes()
    {
        using var fixture = new Fixture("upload");
        var response = await fixture.Invoke();
        Assert.NotNull(response);
        Assert.Equal(fixture.VirtualTarget, response.GetType().GetProperty("path")!.GetValue(response));
        fixture.AssertCompletedEffect();
        Assert.Equal(1, fixture.Audits);
    }

    [Fact]
    public async Task AFilesystemDeleteFailurePreservesTheFileAndDoesNotWriteASuccessAudit()
    {
        using var fixture = new Fixture("delete");
        Exception? failure;
        using (var locked = new FileStream(fixture.Target, FileMode.Open, FileAccess.Read, FileShare.None))
            failure = await Record.ExceptionAsync(() => fixture.Invoke());
        Assert.True(failure is IOException or UnauthorizedAccessException, failure?.ToString() ?? "The locked file was deleted.");
        Assert.Equal("OLD", File.ReadAllText(fixture.Target));
        Assert.Equal(0, fixture.Audits);
    }

    [Fact]
    public async Task DeletingAnOrdinaryDirectoryRemovesOnlyTheRequestedTree()
    {
        using var fixture = new Fixture("delete");
        var directory = Path.Combine(fixture.SiteRoot, "nested");
        Directory.CreateDirectory(Path.Combine(directory, "child"));
        File.WriteAllText(Path.Combine(directory, "child", "content.txt"), "DELETE");
        fixture.DeleteRequest.Path = fixture.VirtualRoot + "/nested";
        await fixture.Invoke();
        Assert.False(Directory.Exists(directory));
        Assert.Equal("OLD", File.ReadAllText(fixture.Target));
        Assert.Equal("KEEP", File.ReadAllText(Path.Combine(fixture.SiteRoot, "keep.txt")));
        Assert.Equal(1, fixture.Audits);
    }

    private static void AssertRejection(Exception? error, string change)
    {
        if (change == "cancel") Assert.IsAssignableFrom<OperationCanceledException>(error);
        else Assert.Equal(change == "scope" ? 403 : 401, Assert.IsType<AppException>(error).HttpStatusCode);
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "adapanel-mutation-response-" + Guid.NewGuid().ToString("N")));
        public string SiteRoot => Path.Combine(Root, "site");
        public string ForeignRoot => Path.Combine(Root, "other-site");
        public string Target => Path.Combine(SiteRoot, "target.txt");
        public string VirtualRoot { get; }
        public string VirtualTarget => VirtualRoot + "/target.txt";
        public Guid Actor { get; } = Guid.NewGuid();
        public FilesController Controller { get; }
        public WriteFileRequest WriteRequest { get; }
        public DeleteFileRequest DeleteRequest { get; }
        public Func<Task>? BeforeAuditCompletes, BeforeWorkspaceRead;
        public Action? BeforeFirstScopeRead;
        public bool ResolveForeignWorkspace;
        public long UsageMb;
        public int Audits, UsageReads, WorkspaceBoundaryCalls;
        private readonly string route;
        private bool allowed = true;
        private int scopeRead;
        private readonly string sid = SessionRevocationStore.NewSessionId();
        private readonly SqliteConnection connection = new("Data Source=:memory:");
        private readonly AdaPanelDbContext db;
        private readonly SessionRevocationStore sessions;
        private readonly ServiceProvider services;
        private readonly ObservedGate gates = new();
        private readonly CancellationTokenSource lifetime = new(TimeSpan.FromSeconds(30));

        public Fixture(string route, UserRole role = UserRole.Customer)
        {
            this.route = route;
            Directory.CreateDirectory(SiteRoot);
            Directory.CreateDirectory(ForeignRoot);
            File.WriteAllText(Target, "OLD");
            File.WriteAllText(Path.Combine(SiteRoot, "keep.txt"), "KEEP");
            File.WriteAllText(Path.Combine(ForeignRoot, "target.txt"), "FOREIGN CONTENT");
            connection.Open();
            db = new AdaPanelDbContext(new DbContextOptionsBuilder<AdaPanelDbContext>().UseSqlite(connection).Options);
            db.Database.EnsureCreated();
            db.Users.Add(new PanelUser { Id = Actor, UserName = "file-owner", Email = "files@example.invalid",
                Role = role, IsActive = true, PasswordHash = "fixture-password-hash" });
            db.SaveChanges();
            var name = "response-" + Guid.NewGuid().ToString("N") + ".test";
            VirtualRoot = "sites/" + name;
            var sites = Stub<IIisSiteService>((method, _) => method == nameof(IIisSiteService.GetSiteAsync)
                ? ReadSite() : throw new NotSupportedException(method));
            var scopes = Stub<ICustomerScopeService>((method, _) => method switch
            {
                nameof(ICustomerScopeService.GetAssignedSitesAsync) or nameof(ICustomerScopeService.GetResellerSitesAsync)
                    => Task.FromResult<IReadOnlyCollection<string>>(allowed ? [name] : []),
                nameof(ICustomerScopeService.GetParentSubscriptionAsync) => Task.FromResult<Guid?>(null),
                nameof(ICustomerScopeService.GetScopeAsync) => ReadScope(),
                nameof(ICustomerScopeService.GetSiteOwnerUserIdAsync) => Task.FromResult<Guid?>(Actor),
                nameof(ICustomerScopeService.GetDiskUsageMbAsync) => ReadUsage(),
                _ => throw new NotSupportedException(method)
            });
            var store = new JsonFileStore(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = Path.Combine(Root, "panel") }));
            sessions = new SessionRevocationStore(store);
            services = new ServiceCollection().AddSingleton(db).AddSingleton(sessions)
                .AddSingleton<IIisSiteService>(sites).AddSingleton<IConcurrencyLock>(gates).BuildServiceProvider();
            Controller = new FilesController(new FileService(sites), scopes, Stub<IAuditService>((method, _) =>
                method is nameof(IAuditService.WriteAsync) or nameof(IAuditService.WriteForSubscriptionAsync)
                    ? Audit() : throw new NotSupportedException(method)), new ConcurrentWorkGate())
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { RequestServices = services,
                    User = Principal(Actor, role, sid) } }
            };
            WriteRequest = new WriteFileRequest { Path = VirtualTarget, Content = "NEW CONTENT" };
            DeleteRequest = new DeleteFileRequest { Path = VirtualTarget };

            async Task<SiteDto> ReadSite()
            {
                // SiteMutationGuard makes fresh reads under its gate; the file service resolves
                // the actual workspace outside it. Return a changed measured snapshot only there.
                var workspace = !gates.InOperation && CompletedEffect() &&
                    (BeforeWorkspaceRead is not null || ResolveForeignWorkspace);
                if (workspace && WorkspaceBoundaryCalls++ == 0 && BeforeWorkspaceRead is { } before) await before();
                var root = workspace && ResolveForeignWorkspace ? ForeignRoot : SiteRoot;
                return new SiteDto { Name = name, Id = root == SiteRoot ? 24 : 25, PhysicalPath = root,
                    ApplicationRootPath = root, ApplicationPoolName = name };
            }
            Task<CustomerScope> ReadScope()
            {
                if (Interlocked.Exchange(ref scopeRead, 1) == 0) BeforeFirstScopeRead?.Invoke();
                return Task.FromResult(new CustomerScope([name], 1, 1, 0, 0, 0));
            }
        }

        public static ClaimsPrincipal Principal(Guid actor, UserRole role, string? session = null) =>
            new(new ClaimsIdentity([
                new Claim(ClaimTypes.NameIdentifier, actor.ToString()), new Claim(ClaimTypes.Role, role.ToString()),
                new Claim(CookieSessionValidator.CredentialStampClaim, CookieSessionValidator.ComputeStamp("fixture-password-hash")),
                new Claim(SessionRevocationStore.SessionIdClaim, session ?? SessionRevocationStore.NewSessionId())], CookieAuthenticationDefaults.AuthenticationScheme));

        private bool CompletedEffect() => route == "delete" ? !File.Exists(Target) :
            File.Exists(Target) && File.ReadAllText(Target) == "NEW CONTENT";
        private async Task Audit()
        { Audits++; if (BeforeAuditCompletes is { } before) await before(); }
        private Task<DiskUsage> ReadUsage()
        { UsageReads++; return Task.FromResult(new DiskUsage(UsageMb, true, string.Empty)); }
        public async Task ChangeAuthority(string change)
        {
            if (change == "logout") await sessions.RevokeAsync(sid, lifetime.Token);
            else if (change == "password") await db.Users.Where(user => user.Id == Actor).ExecuteUpdateAsync(setters => setters.SetProperty(user => user.PasswordHash, "changed"));
            else if (change == "inactive") await db.Users.Where(user => user.Id == Actor).ExecuteUpdateAsync(setters => setters.SetProperty(user => user.IsActive, false));
            else if (change == "scope") allowed = false;
            else if (change == "cancel") lifetime.Cancel();
            else throw new ArgumentOutOfRangeException(nameof(change));
        }
        public async Task<object?> Invoke()
        {
            if (route == "upload")
            {
                await using var stream = new MemoryStream(Encoding.UTF8.GetBytes("NEW CONTENT"));
                var file = new FormFile(stream, 0, stream.Length, "file", "target.txt");
                var upload = await Controller.UploadAsync(VirtualRoot, file, lifetime.Token);
                return Assert.IsType<ApiResponse<object>>(Assert.IsType<OkObjectResult>(upload.Result).Value).Data;
            }
            var response = route == "write"
                ? await Controller.WriteAsync(WriteRequest, lifetime.Token)
                : await Controller.DeleteAsync(DeleteRequest, lifetime.Token);
            return Assert.IsType<ApiResponse<FileWorkspaceDto>>(Assert.IsType<OkObjectResult>(response.Result).Value).Data;
        }
        public void AssertCompletedEffect()
        {
            if (route == "delete") Assert.False(File.Exists(Target));
            else Assert.Equal(Encoding.UTF8.GetBytes("NEW CONTENT"), File.ReadAllBytes(Target));
            Assert.Equal("KEEP", File.ReadAllText(Path.Combine(SiteRoot, "keep.txt")));
        }
        public void AssertNoTemporaryFiles() => Assert.Empty(Directory.GetFiles(SiteRoot, ".adapanel-*", SearchOption.AllDirectories));
        public void Dispose()
        {
            services.Dispose(); db.Dispose(); connection.Dispose(); gates.Dispose(); lifetime.Dispose();
            var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
            if (!string.Equals(Path.GetDirectoryName(Root), temp, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(Root).StartsWith("adapanel-mutation-response-", StringComparison.Ordinal))
                throw new InvalidOperationException("Mutation fixture cleanup is outside its owned temporary root.");
            Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class ObservedGate : IConcurrencyLock, IDisposable
    {
        private readonly SemaphoreSlim gate = new(1, 1);
        public bool InOperation { get; private set; }
        public Task RunExclusiveAsync(Func<Task> action, CancellationToken token = default) => RunExclusiveAsync(ConcurrencyKeys.Iis, action, token);
        public Task<T> RunExclusiveAsync<T>(Func<Task<T>> action, CancellationToken token = default) => RunExclusiveAsync(ConcurrencyKeys.Iis, action, token);
        public async Task RunExclusiveAsync(string key, Func<Task> action, CancellationToken token = default)
        { await gate.WaitAsync(token); try { InOperation = true; await action(); } finally { InOperation = false; gate.Release(); } }
        public async Task<T> RunExclusiveAsync<T>(string key, Func<Task<T>> action, CancellationToken token = default)
        { await gate.WaitAsync(token); try { InOperation = true; return await action(); } finally { InOperation = false; gate.Release(); } }
        public void Dispose() => gate.Dispose();
    }
    private static T Stub<T>(Func<string, object?[]?, object?> handler) where T : class
    { var proxy = DispatchProxy.Create<T, Proxy>(); ((Proxy)(object)proxy).Handler = handler; return proxy; }
    public class Proxy : DispatchProxy
    {
        public Func<string, object?[]?, object?> Handler { get; set; } = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => Handler(method!.Name, args);
    }
}
