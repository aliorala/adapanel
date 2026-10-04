using System.Reflection;
using System.Security.Claims;
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

public sealed class FileTransferResponseAuthorityTests
{
    public static IEnumerable<object[]> AuditChanges =>
        from route in new[] { "move", "copy" }
        from change in new[] { "logout", "password", "inactive", "scope", "cancel" }
        select new object[] { route, change };

    [Theory]
    [MemberData(nameof(AuditChanges))]
    public async Task AuditCompletionCannotPublishATransferToARevokedResponseActor(string route, string change)
    {
        using var f = new Fixture(route);
        f.BeforeAuditCompletes = () => f.ChangeAuthority(change);
        AssertRejection(await Record.ExceptionAsync(() => f.Invoke()), change);
        Assert.Equal(1, f.Audits);
        f.AssertCompletedEffect(); // Response rejection does not undo an accepted transfer.
        f.AssertNoTemporaryFiles();
    }

    public static IEnumerable<object[]> ServiceReturnChanges =>
        from route in new[] { "move", "copy" }
        from change in new[] { "logout", "scope", "cancel" }
        select new object[] { route, change };

    [Theory]
    [MemberData(nameof(ServiceReturnChanges))]
    public async Task TheControllerRechecksBeforeWritingTheTransferAudit(string route, string change)
    {
        using var f = new Fixture(route);
        f.BeforeServiceReturns = () => f.ChangeAuthority(change);
        AssertRejection(await Record.ExceptionAsync(() => f.Invoke()), change);
        Assert.Equal(0, f.Audits);
        f.AssertCompletedEffect();
    }

    [Theory]
    [InlineData("move", UserRole.Customer)]
    [InlineData("move", UserRole.Reseller)]
    [InlineData("move", UserRole.Administrator)]
    [InlineData("copy", UserRole.Customer)]
    [InlineData("copy", UserRole.Reseller)]
    [InlineData("copy", UserRole.Administrator)]
    public async Task TheNestedWorkspaceHasTheAcceptedActorsProjection(string route, UserRole role)
    {
        using var f = new Fixture(route, role);
        var result = await f.Invoke();
        Assert.Equal(1, result.Succeeded); Assert.Equal(0, result.Failed);
        Assert.Equal(f.VirtualDestination, result.Workspace.Path);
        Assert.Equal(role == UserRole.Administrator ? f.Destination : string.Empty, result.Workspace.PhysicalPath);
        Assert.Contains(result.Workspace.Entries, entry => entry.Name == "source.txt");
        Assert.Equal(f.VirtualDestination + "/source.txt", Assert.Single(result.Targets));
        f.AssertCompletedEffect(); Assert.Equal(1, f.Audits);
    }

    [Theory]
    [InlineData("move", "scope")]
    [InlineData("move", "cancel")]
    [InlineData("copy", "scope")]
    [InlineData("copy", "cancel")]
    public async Task WorkspaceResolutionStillRequiresTheCurrentResponseAuthority(string route, string change)
    {
        using var f = new Fixture(route);
        f.BeforeWorkspaceRead = () => f.ChangeAuthority(change);
        AssertRejection(await Record.ExceptionAsync(() => f.Invoke()), change);
        Assert.Equal(1, f.WorkspaceBoundaryCalls); Assert.Equal(0, f.Audits);
        f.AssertCompletedEffect();
    }

    [Theory]
    [InlineData("move")]
    [InlineData("copy")]
    public async Task TheActualResolvedWorkspaceCannotBorrowTheOldDestinationApproval(string route)
    {
        using var f = new Fixture(route);
        f.ResolveForeignWorkspace = true;
        Assert.Equal(409, Assert.IsType<AppException>(await Record.ExceptionAsync(() => f.Invoke())).HttpStatusCode);
        Assert.Equal(1, f.WorkspaceBoundaryCalls); Assert.Equal(0, f.Audits);
        f.AssertCompletedEffect();
        Assert.Equal("FOREIGN PRIVATE CONTENT", File.ReadAllText(f.ForeignFile));
    }

    [Theory]
    [InlineData(UserRole.Customer)]
    [InlineData(UserRole.Reseller)]
    public async Task ReplacingTheAmbientPrincipalCannotBypassTheAcceptedCopyQuota(UserRole role)
    {
        using var f = new Fixture("copy", role);
        f.UsageMb = 1;
        f.BeforeFirstScopeRead = () => f.Controller.HttpContext.User = Fixture.Principal(Guid.NewGuid(), UserRole.Administrator);
        var error = await Record.ExceptionAsync(() => f.Invoke());
        Assert.Equal("files.quota_exceeded", Assert.IsType<AppException>(error).MessageKey);
        Assert.Equal(1, f.UsageReads); Assert.Equal(0, f.Audits);
        Assert.False(File.Exists(f.Target));
        Assert.Equal("ORIGINAL CONTENT", File.ReadAllText(f.Source));
        f.AssertNoTemporaryFiles();
    }

    [Fact]
    public async Task ReplacingTheAmbientPrincipalCannotBorrowAnotherHeavyOperationBudget()
    {
        using var f = new Fixture("copy");
        using var first = f.HeavyGate.Enter($"files.heavy:{f.Actor:N}", 2, "files.too_many_operations");
        using var second = f.HeavyGate.Enter($"files.heavy:{f.Actor:N}", 2, "files.too_many_operations");
        f.BeforeFirstScopeRead = () => f.Controller.HttpContext.User = Fixture.Principal(Guid.NewGuid(), UserRole.Administrator);
        var error = await Record.ExceptionAsync(() => f.Invoke());
        Assert.Equal("files.too_many_operations", Assert.IsType<AppException>(error).MessageKey);
        Assert.False(File.Exists(f.Target));
        Assert.Equal("ORIGINAL CONTENT", File.ReadAllText(f.Source));
        Assert.Equal(0, f.UsageReads); Assert.Equal(0, f.Audits);
    }

    [Theory]
    [InlineData("move")]
    [InlineData("copy")]
    public async Task RequestArraysAndDestinationAreCapturedBeforeScopeWaits(string route)
    {
        using var f = new Fixture(route);
        f.BeforeFirstScopeRead = () =>
        {
            f.MoveRequest.Paths[0] = f.VirtualRoot + "/keep.txt";
            f.CopyRequest.Paths[0] = f.VirtualRoot + "/keep.txt";
            f.MoveRequest.DestinationPath = f.VirtualRoot;
            f.CopyRequest.DestinationPath = f.VirtualRoot;
            f.MoveRequest.NewName = "changed.txt"; f.CopyRequest.NewName = "changed.txt";
        };
        var result = await f.Invoke();
        Assert.Equal(1, result.Succeeded);
        f.AssertCompletedEffect();
        Assert.False(File.Exists(Path.Combine(f.SiteRoot, "changed.txt")));
        Assert.Equal(f.VirtualDestination, Assert.Single(f.AuditPaths));
    }

    [Theory]
    [InlineData("move")]
    [InlineData("copy")]
    public async Task PartialTransferResultsKeepTheirCountsAndScopedWorkspace(string route)
    {
        using var f = new Fixture(route);
        f.IncludeMissingSource();
        var result = await f.Invoke();
        Assert.Equal(1, result.Succeeded); Assert.Equal(1, result.Failed);
        Assert.Equal(string.Empty, result.Workspace.PhysicalPath);
        Assert.Equal("files.not_found", Assert.Single(result.Errors));
        Assert.Single(f.AuditDetails);
        Assert.Contains("1 of 2", f.AuditDetails[0], StringComparison.Ordinal);
        Assert.DoesNotContain("ORIGINAL CONTENT", f.AuditDetails[0], StringComparison.Ordinal);
        f.AssertCompletedEffect();
    }

    [Theory]
    [InlineData("move")]
    [InlineData("copy")]
    public async Task PartialEffectsDoNotPermitPublishingToALoggedOutSession(string route)
    {
        using var f = new Fixture(route);
        f.IncludeMissingSource();
        f.BeforeAuditCompletes = () => f.ChangeAuthority("logout");
        AssertRejection(await Record.ExceptionAsync(() => f.Invoke()), "logout");
        Assert.Equal(1, f.Audits);
        f.AssertCompletedEffect();
    }

    [Theory]
    [InlineData("move")]
    [InlineData("copy")]
    public async Task APreCanceledTransferHasNoFilesystemEffectOrAudit(string route)
    {
        using var f = new Fixture(route);
        await f.ChangeAuthority("cancel");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Invoke());
        Assert.False(File.Exists(f.Target));
        Assert.Equal("ORIGINAL CONTENT", File.ReadAllText(f.Source));
        Assert.Equal(0, f.Audits);
    }

    private static void AssertRejection(Exception? error, string change)
    {
        if (change == "cancel") Assert.IsAssignableFrom<OperationCanceledException>(error);
        else Assert.Equal(change == "scope" ? 403 : 401, Assert.IsType<AppException>(error).HttpStatusCode);
    }

    private sealed record Result(int Succeeded, int Failed, FileWorkspaceDto Workspace, string[] Targets, string[] Errors);

    private sealed class Fixture : IDisposable
    {
        private const string Prefix = "adapanel-transfer-response-";
        public string Root { get; } = Path.GetFullPath(Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N")));
        public string SiteRoot => Path.Combine(Root, "site");
        public string ForeignRoot => Path.Combine(Root, "other-site");
        public string Source => Path.Combine(SiteRoot, "source.txt");
        public string Destination => Path.Combine(SiteRoot, "destination");
        public string Target => Path.Combine(Destination, "source.txt");
        public string ForeignFile => Path.Combine(ForeignRoot, "destination", "foreign-private.txt");
        public string VirtualRoot { get; }
        public string VirtualDestination => VirtualRoot + "/destination";
        public Guid Actor { get; } = Guid.NewGuid();
        public FilesController Controller { get; }
        public ConcurrentWorkGate HeavyGate { get; } = new();
        public MoveFilesRequest MoveRequest { get; }
        public CopyFilesRequest CopyRequest { get; }
        public Func<Task>? BeforeAuditCompletes, BeforeWorkspaceRead, BeforeServiceReturns;
        public Action? BeforeFirstScopeRead;
        public bool ResolveForeignWorkspace;
        public long UsageMb;
        public int Audits, UsageReads, WorkspaceBoundaryCalls;
        public List<string> AuditPaths { get; } = [];
        public List<string> AuditDetails { get; } = [];
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
            Directory.CreateDirectory(Destination); Directory.CreateDirectory(Path.GetDirectoryName(ForeignFile)!);
            File.WriteAllText(Source, "ORIGINAL CONTENT");
            File.WriteAllText(Path.Combine(SiteRoot, "keep.txt"), "KEEP");
            File.WriteAllText(ForeignFile, "FOREIGN PRIVATE CONTENT");
            connection.Open();
            db = new AdaPanelDbContext(new DbContextOptionsBuilder<AdaPanelDbContext>().UseSqlite(connection).Options);
            db.Database.EnsureCreated();
            db.Users.Add(new PanelUser { Id = Actor, UserName = "transfer-owner", Email = "transfer@example.invalid",
                Role = role, IsActive = true, PasswordHash = "fixture-password-hash" }); db.SaveChanges();
            var name = "transfer-" + Guid.NewGuid().ToString("N") + ".test";
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
            var actual = new FileService(sites);
            // The service is real. The adapter controls only the await-return boundary so
            // the controller cannot borrow the service's earlier authority check.
            var service = Stub<IFileService>((method, args) => method switch
            {
                nameof(IFileService.MoveAsync) => Move((MoveFilesRequest)args![0]!, (Func<SiteDto, CancellationToken, Task>)args[1]!,
                    args.Length == 4 ? (FileTransferQuotaCheck)args[2]! : null, (CancellationToken)args[^1]!),
                nameof(IFileService.CopyAsync) => Copy((CopyFilesRequest)args![0]!, (Func<SiteDto, CancellationToken, Task>)args[1]!,
                    args.Length == 4 ? (FileTransferQuotaCheck)args[2]! : null, (CancellationToken)args[^1]!),
                nameof(IFileService.MeasureAsync) => actual.MeasureAsync((string[])args![0]!, (CancellationToken)args[1]!),
                _ => throw new NotSupportedException(method)
            });
            Controller = new FilesController(service, scopes, Stub<IAuditService>((method, args) =>
                method is nameof(IAuditService.WriteAsync) or nameof(IAuditService.WriteForSubscriptionAsync)
                    ? Audit((string)args![1]!, (string)args[2]!) : throw new NotSupportedException(method)), HeavyGate)
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { RequestServices = services,
                    User = Principal(Actor, role, sid) } }
            };
            MoveRequest = new() { Paths = [VirtualRoot + "/source.txt"], DestinationPath = VirtualDestination };
            CopyRequest = new() { Paths = [VirtualRoot + "/source.txt"], DestinationPath = VirtualDestination };

            async Task<MoveFilesResultDto> Move(MoveFilesRequest request, Func<SiteDto, CancellationToken, Task> approve,
                FileTransferQuotaCheck? quota, CancellationToken token)
            {
                var result = quota is null ? await actual.MoveAsync(request, approve, token)
                    : await actual.MoveAsync(request, approve, quota, token);
                if (BeforeServiceReturns is not null) await BeforeServiceReturns();
                return result;
            }
            async Task<CopyFilesResultDto> Copy(CopyFilesRequest request, Func<SiteDto, CancellationToken, Task> approve,
                FileTransferQuotaCheck? quota, CancellationToken token)
            {
                var result = quota is null ? await actual.CopyAsync(request, approve, token)
                    : await actual.CopyAsync(request, approve, quota, token);
                if (BeforeServiceReturns is not null) await BeforeServiceReturns();
                return result;
            }
            async Task<SiteDto> ReadSite()
            {
                // The guard obtains a fresh site under its gate. The service's actual final
                // workspace resolver is outside it; only that read returns the foreign DTO.
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
        private bool CompletedEffect() => File.Exists(Target) && File.ReadAllText(Target) == "ORIGINAL CONTENT";
        private async Task Audit(string path, string detail)
        { Audits++; AuditPaths.Add(path); AuditDetails.Add(detail); if (BeforeAuditCompletes is { } before) await before(); }
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
        public async Task<Result> Invoke()
        {
            if (route == "move")
            {
                var response = await Controller.MoveAsync(MoveRequest, lifetime.Token);
                var dto = Assert.IsType<ApiResponse<MoveFilesResultDto>>(Assert.IsType<OkObjectResult>(response.Result).Value).Data!;
                return new(dto.MovedCount, dto.FailedCount, dto.Workspace,
                    dto.Items.Where(item => item.Moved).Select(item => item.TargetPath).ToArray(),
                    dto.Items.Where(item => !item.Moved).Select(item => item.ErrorKey).ToArray());
            }
            var copied = await Controller.CopyAsync(CopyRequest, lifetime.Token);
            var copy = Assert.IsType<ApiResponse<CopyFilesResultDto>>(Assert.IsType<OkObjectResult>(copied.Result).Value).Data!;
            return new(copy.CopiedCount, copy.FailedCount, copy.Workspace,
                copy.Items.Where(item => item.Copied).Select(item => item.TargetPath).ToArray(),
                copy.Items.Where(item => !item.Copied).Select(item => item.ErrorKey).ToArray());
        }
        public void IncludeMissingSource()
        {
            MoveRequest.Paths = [VirtualRoot + "/source.txt", VirtualRoot + "/missing.txt"];
            CopyRequest.Paths = [VirtualRoot + "/source.txt", VirtualRoot + "/missing.txt"];
        }
        public void AssertCompletedEffect()
        {
            Assert.Equal("ORIGINAL CONTENT", File.ReadAllText(Target));
            if (route == "move") Assert.False(File.Exists(Source));
            else Assert.Equal("ORIGINAL CONTENT", File.ReadAllText(Source));
            Assert.Equal("KEEP", File.ReadAllText(Path.Combine(SiteRoot, "keep.txt")));
        }
        public void AssertNoTemporaryFiles() => Assert.Empty(Directory.GetFiles(SiteRoot, ".adapanel-*", SearchOption.AllDirectories));
        public void Dispose()
        {
            services.Dispose(); db.Dispose(); connection.Dispose(); gates.Dispose(); lifetime.Dispose();
            var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
            if (!string.Equals(Path.GetDirectoryName(Root), temp, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(Root).StartsWith(Prefix, StringComparison.Ordinal) ||
                File.GetAttributes(Root).HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidOperationException("Transfer fixture cleanup is outside its owned temporary root.");
            var pending = new Stack<string>(); pending.Push(Root);
            while (pending.TryPop(out var directory))
                foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
                {
                    if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new InvalidOperationException("Unexpected fixture reparse point.");
                    if (entry is DirectoryInfo) pending.Push(entry.FullName);
                }
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
    { var result = DispatchProxy.Create<T, Proxy>(); ((Proxy)(object)result).Handler = handler; return result; }
    public class Proxy : DispatchProxy
    { public Func<string, object?[]?, object?> Handler = null!; protected override object? Invoke(MethodInfo? method, object?[]? args) => Handler(method!.Name, args); }
}
