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

public sealed class FileBulkMutationAuthorityTests
{
    public static IEnumerable<object[]> Revocations() =>
        from route in new[] { "delete", "move", "copy" }
        from change in new[] { "disabled", "deleted", "role", "password", "logout", "source-scope", "section", "parent", "suspended" }
        select new object[] { route, change };

    [Theory]
    [MemberData(nameof(Revocations))]
    public async Task RevocationDuringResolutionPreservesEverySourceAndDestination(string route, string change)
    {
        using var f = await Fixture.Create(route);
        f.PauseSite = f.SourceName;
        var operation = Record.ExceptionAsync(() => f.Run());
        await f.WaitForBoundary(operation);
        try { await f.ChangeActor(change); }
        finally { f.Release.TrySetResult(); }
        var error = await operation.WaitAsync(f.Token);
        f.AssertUnchanged();
        Assert.True(Assert.IsType<AppException>(error).HttpStatusCode is 401 or 403);
        Assert.Empty(f.Audits);
    }

    public static IEnumerable<object[]> SiteChanges() =>
        from route in new[] { "delete", "move", "copy" }
        from side in route == "delete" ? new[] { "source" } : new[] { "source", "destination" }
        from change in new[] { "id", "path", "version" }
        select new object[] { route, side, change };

    [Theory]
    [MemberData(nameof(SiteChanges))]
    public async Task SourceAndDestinationGenerationsAreBothBoundBeforeTheirResolverWaits(string route, string side, string change)
    {
        using var f = await Fixture.Create(route);
        f.PauseSite = side == "source" ? f.SourceName : f.DestinationName;
        var operation = Record.ExceptionAsync(() => f.Run());
        await f.WaitForBoundary(operation);
        try { await f.ReplaceSite(side, change); }
        finally { f.Release.TrySetResult(); }
        var error = await operation.WaitAsync(f.Token);
        f.AssertUnchanged();
        Assert.Equal("sites.target_changed", Assert.IsType<AppException>(error).MessageKey);
        Assert.Empty(f.Audits);
    }

    [Theory]
    [InlineData("delete")]
    [InlineData("move")]
    [InlineData("copy")]
    public async Task SameNameReplacementDuringTheFirstScopeReadCannotBecomeTheAcceptedTarget(string route)
    {
        using var f = await Fixture.Create(route);
        f.PauseScope = true;
        var operation = Record.ExceptionAsync(() => f.Run());
        await f.WaitForBoundary(operation);
        try { await f.ReplaceSite("source", "path"); }
        finally { f.Release.TrySetResult(); }
        var error = await operation.WaitAsync(f.Token);
        f.AssertUnchanged();
        Assert.Equal("sites.target_changed", Assert.IsType<AppException>(error).MessageKey);
        Assert.Empty(f.Audits);
    }

    [Theory]
    [InlineData("delete")]
    [InlineData("move")]
    [InlineData("copy")]
    public async Task CancellationAfterAnIgnoringResolverDoesNotPerformTheFirstMutation(string route)
    {
        using var f = await Fixture.Create(route);
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(f.Token);
        f.PauseSite = f.SourceName;
        var operation = Record.ExceptionAsync(() => f.Run(canceled.Token));
        await f.WaitForBoundary(operation);
        canceled.Cancel(); f.Release.TrySetResult();
        Assert.IsAssignableFrom<OperationCanceledException>(await operation.WaitAsync(f.Token));
        f.AssertUnchanged(); Assert.Empty(f.Audits);
    }

    [Theory]
    [InlineData("move", "logout")]
    [InlineData("copy", "logout")]
    [InlineData("move", "destination-scope")]
    [InlineData("copy", "destination-scope")]
    [InlineData("move", "cancel")]
    [InlineData("copy", "cancel")]
    public async Task AuthorityInterruptionBetweenItemsStopsBeforeTheNextEffect(string route, string change)
    {
        using var f = await Fixture.Create(route);
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(f.Token);
        f.Files.TransferItemReady = async (source, _) =>
        {
            if (!source.EndsWith("second.txt", StringComparison.Ordinal)) return;
            Assert.Equal("FIRST", File.ReadAllText(f.Destination("first.txt")));
            if (change == "cancel") canceled.Cancel();
            else await f.ChangeActor(change);
        };
        var error = await Record.ExceptionAsync(() => f.Run(canceled.Token));
        Assert.Equal("SECOND", File.ReadAllText(f.Source("second.txt")));
        Assert.False(File.Exists(f.Destination("second.txt")));
        Assert.Equal("FIRST", File.ReadAllText(f.Destination("first.txt")));
        if (change == "cancel") Assert.IsAssignableFrom<OperationCanceledException>(error);
        else Assert.True(Assert.IsType<AppException>(error).HttpStatusCode is 401 or 403);
        Assert.Empty(f.Audits);
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("cancel")]
    public async Task RecursiveCopyStopsAtTheNextFileAndRemovesOnlyItsIncompleteTree(string change)
    {
        using var f = await Fixture.Create("copy");
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(f.Token);
        Directory.CreateDirectory(f.Source("tree"));
        File.WriteAllText(f.Source("tree/one.txt"), "ONE");
        File.WriteAllText(f.Source("tree/two.txt"), "TWO");
        f.Paths = [f.VirtualSource("first.txt"), f.VirtualSource("tree")];
        var entries = 0;
        f.Files.CopyEntryReady = _ =>
        {
            if (++entries != 2) return;
            if (change == "cancel") canceled.Cancel();
            else f.DisableActorSynchronously();
        };
        var error = await Record.ExceptionAsync(() => f.Run(canceled.Token));
        Assert.Equal(2, entries);
        Assert.Equal("FIRST", File.ReadAllText(f.Destination("first.txt")));
        Assert.False(Directory.Exists(f.Destination("tree")));
        Assert.Equal("ONE", File.ReadAllText(f.Source("tree/one.txt")));
        Assert.Equal("TWO", File.ReadAllText(f.Source("tree/two.txt")));
        if (change == "cancel") Assert.IsAssignableFrom<OperationCanceledException>(error);
        else Assert.Equal(401, Assert.IsType<AppException>(error).HttpStatusCode);
        Assert.Empty(f.Audits);
    }

    [Fact]
    public async Task FailedOverwriteMovePreservesTheExistingDestinationBytes()
    {
        using var f = await Fixture.Create("move");
        f.Paths = [f.VirtualSource("first.txt")]; f.Overwrite = true;
        File.WriteAllText(f.Destination("first.txt"), "ORIGINAL DESTINATION");
        using var lockedSource = new FileStream(f.Source("first.txt"), FileMode.Open, FileAccess.Read, FileShare.None);
        var response = Assert.IsType<OkObjectResult>(await f.Run());
        var result = Assert.IsType<ApiResponse<MoveFilesResultDto>>(response.Value).Data!;
        Assert.Equal("ORIGINAL DESTINATION", File.ReadAllText(f.Destination("first.txt")));
        Assert.Equal(0, result.MovedCount); Assert.Equal(1, result.FailedCount);
        Assert.False(Assert.Single(result.Items).Moved);
    }

    [Theory]
    [InlineData("delete")]
    [InlineData("move")]
    [InlineData("copy")]
    public async Task ACurrentActorCanStillMutateItsSelectedFiles(string route)
    {
        using var f = await Fixture.Create(route);
        Assert.IsType<OkObjectResult>(await f.Run());
        if (route != "copy") Assert.False(File.Exists(f.Source("first.txt")));
        else Assert.Equal("FIRST", File.ReadAllText(f.Source("first.txt")));
        if (route != "delete") Assert.Equal("FIRST", File.ReadAllText(f.Destination("first.txt")));
        Assert.Single(f.Audits);
    }

    [Theory]
    [InlineData("move")]
    [InlineData("copy")]
    public async Task OrdinaryItemFailureStillReturnsTheCompletedItems(string route)
    {
        using var f = await Fixture.Create(route);
        f.Paths = [f.VirtualSource("first.txt"), f.VirtualSource("missing.txt")];
        var response = Assert.IsType<OkObjectResult>(await f.Run());
        if (route == "move")
        {
            var result = Assert.IsType<ApiResponse<MoveFilesResultDto>>(response.Value).Data!;
            Assert.Equal(1, result.MovedCount); Assert.Equal(1, result.FailedCount);
            Assert.Equal("files.not_found", result.Items.Last().ErrorKey);
        }
        else
        {
            var result = Assert.IsType<ApiResponse<CopyFilesResultDto>>(response.Value).Data!;
            Assert.Equal(1, result.CopiedCount); Assert.Equal(1, result.FailedCount);
            Assert.Equal("files.not_found", result.Items.Last().ErrorKey);
        }
        Assert.Equal("FIRST", File.ReadAllText(f.Destination("first.txt")));
        Assert.Single(f.Audits);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnAuthorityReadFailureInsideARecursiveCopyIsNotAnOrdinaryPartialResult(bool accessDenied)
    {
        using var f = await Fixture.Create("copy");
        Directory.CreateDirectory(f.Source("tree"));
        File.WriteAllText(f.Source("tree/one.txt"), "ONE");
        File.WriteAllText(f.Source("tree/two.txt"), "TWO");
        f.Paths = [f.VirtualSource("first.txt"), f.VirtualSource("tree")];
        Exception failure = accessDenied ? new UnauthorizedAccessException("scope read unavailable") : new IOException("scope read unavailable");
        var count = 0;
        f.Files.CopyEntryReady = _ => { if (++count == 2) f.ScopeFailure = failure; };
        Assert.Same(failure, await Record.ExceptionAsync(() => f.Run()));
        Assert.Equal("FIRST", File.ReadAllText(f.Destination("first.txt")));
        Assert.False(Directory.Exists(f.Destination("tree")));
        f.AssertNoStagingTrees();
        Assert.Empty(f.Audits);
    }

    [Fact]
    public async Task ACompetingDestinationDirectorySurvivesTheFailedPublicationAndCleanup()
    {
        using var f = await Fixture.Create("copy");
        Directory.CreateDirectory(f.Source("tree"));
        File.WriteAllText(f.Source("tree/one.txt"), "ONE");
        f.Paths = [f.VirtualSource("tree")];
        f.Files.CopyEntryReady = _ =>
        {
            Directory.CreateDirectory(f.Destination("tree"));
            File.WriteAllText(f.Destination("tree/foreign.txt"), "FOREIGN CONTENT");
        };
        var response = Assert.IsType<OkObjectResult>(await f.Run());
        var result = Assert.IsType<ApiResponse<CopyFilesResultDto>>(response.Value).Data!;
        Assert.Equal(0, result.CopiedCount); Assert.Equal(1, result.FailedCount);
        Assert.Equal("FOREIGN CONTENT", File.ReadAllText(f.Destination("tree/foreign.txt")));
        Assert.False(File.Exists(f.Destination("tree/one.txt")));
        f.AssertNoStagingTrees();
    }

    [Theory]
    [InlineData("move")]
    [InlineData("copy")]
    public async Task TheSelectedPathArrayIsCapturedBeforeTheFirstAwait(string route)
    {
        using var f = await Fixture.Create(route);
        File.WriteAllText(f.Source("unselected.txt"), "UNSELECTED");
        f.PauseSite = f.SourceName;
        var operation = f.Run();
        await f.WaitForBoundary(operation);
        f.Paths[1] = f.VirtualSource("unselected.txt");
        f.Release.TrySetResult();
        Assert.IsType<OkObjectResult>(await operation.WaitAsync(f.Token));
        Assert.Equal("SECOND", File.ReadAllText(f.Destination("second.txt")));
        Assert.Equal("UNSELECTED", File.ReadAllText(f.Source("unselected.txt")));
        Assert.False(File.Exists(f.Destination("unselected.txt")));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "adapanel-file-bulk-" + Guid.NewGuid().ToString("N"));
        private readonly SqliteConnection connection = new("Data Source=:memory:");
        private readonly CancellationTokenSource lifetime = new(TimeSpan.FromSeconds(30));
        private readonly Guid actor = Guid.NewGuid(), owner = Guid.NewGuid(), other = Guid.NewGuid();
        private readonly string route, sourceRoot, destinationRoot, foreignRoot;
        private readonly string sid = SessionRevocationStore.NewSessionId();
        private readonly AdaPanelDbContext db;
        private readonly CustomerScopeService scopes;
        private readonly SessionRevocationStore sessions;
        private readonly ServiceProvider services;
        private readonly FilesController controller;
        private readonly FilePublicationAuthorityTests.LocalGate gates = new();
        private int paused;
        private long sourceId = 21, destinationId = 22;
        private string resolvedSourceRoot, resolvedDestinationRoot;
        public string SourceName { get; } = "a-" + Guid.NewGuid().ToString("N") + ".test";
        public string DestinationName { get; } = "b-" + Guid.NewGuid().ToString("N") + ".test";
        public FileService Files { get; }
        public CancellationToken Token => lifetime.Token;
        public string? PauseSite;
        public bool PauseScope, Overwrite;
        public Exception? ScopeFailure;
        public string[] Paths;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<string> Audits { get; } = [];
        public string Source(string relative) => Path.Combine(sourceRoot, relative.Replace('/', Path.DirectorySeparatorChar));
        public string Destination(string relative) => Path.Combine(destinationRoot, "out", relative.Replace('/', Path.DirectorySeparatorChar));
        public string VirtualSource(string relative) => $"sites/{SourceName}/{relative}";

        private Fixture(string route)
        {
            this.route = route;
            sourceRoot = resolvedSourceRoot = Path.Combine(root, "source");
            destinationRoot = resolvedDestinationRoot = Path.Combine(root, "destination");
            foreignRoot = Path.Combine(root, "replacement");
            foreach (var path in new[] { sourceRoot, destinationRoot, foreignRoot }) Directory.CreateDirectory(Path.Combine(path, "out"));
            foreach (var path in new[] { sourceRoot, foreignRoot })
            { File.WriteAllText(Path.Combine(path, "first.txt"), "FIRST"); File.WriteAllText(Path.Combine(path, "second.txt"), "SECOND"); }
            Paths = [VirtualSource("first.txt"), VirtualSource("second.txt")];
            connection.Open();
            db = new AdaPanelDbContext(new DbContextOptionsBuilder<AdaPanelDbContext>().UseSqlite(connection).Options);
            db.Database.EnsureCreated();
            db.Users.AddRange(User(actor, "actor"), User(owner, "owner"), User(other, "other")); db.SaveChanges();
            var store = new JsonFileStore(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = Path.Combine(root, "panel") }));
            var sites = Fake<IIisSiteService>((method, args) => method.Name == nameof(IIisSiteService.GetSiteAsync)
                ? ReadSite((string)args![0]!) : throw new NotSupportedException(method.Name));
            scopes = new CustomerScopeService(store, Fake<IServicePlanService>(),
                Fake<IResellerQuotaService>((method, _) => method.Name == nameof(IResellerQuotaService.GetQuotaAsync)
                    ? Task.FromResult<ResellerQuotaDto?>(null) : throw new NotSupportedException(method.Name)), sites);
            var scopeProxy = Fake<ICustomerScopeService>((method, args) => method.Name == nameof(ICustomerScopeService.GetAssignedSitesAsync)
                ? ReadAssignedSites((Guid)args![0]!, (CancellationToken)args[1]!) : method.Invoke(scopes, args));
            sessions = new SessionRevocationStore(store);
            Files = new FileService(sites);
            services = new ServiceCollection().AddSingleton(db).AddSingleton(sessions)
                .AddSingleton<IIisSiteService>(sites).AddSingleton<IConcurrencyLock>(gates).BuildServiceProvider();
            controller = new FilesController(Files, scopeProxy,
                Fake<IAuditService>((method, _) => { Audits.Add(method.Name); return Task.CompletedTask; }), new ConcurrentWorkGate())
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { RequestServices = services,
                    User = new ClaimsPrincipal(new ClaimsIdentity([
                        new Claim(ClaimTypes.NameIdentifier, actor.ToString()), new Claim(ClaimTypes.Role, "Customer"),
                        new Claim(CookieSessionValidator.CredentialStampClaim, CookieSessionValidator.ComputeStamp("original")),
                        new Claim(SessionRevocationStore.SessionIdClaim, sid)], CookieAuthenticationDefaults.AuthenticationScheme)) } }
            };
        }

        public static async Task<Fixture> Create(string route)
        {
            var f = new Fixture(route);
            await f.scopes.SetScopeAsync(f.owner, [f.SourceName, f.DestinationName], 0, 0, f.Token);
            await f.scopes.SetScopeAsync(f.other, [], 0, 0, f.Token);
            await f.scopes.SetParentSubscriptionAsync(f.actor, f.owner, f.Token);
            await f.scopes.SetSubUserSectionsAsync(f.actor, ["files"], f.Token);
            return f;
        }
        private static PanelUser User(Guid id, string name) => new()
        { Id = id, UserName = name, Email = name + "@test.invalid", Role = UserRole.Customer, IsActive = true, PasswordHash = "original" };
        private async Task<SiteDto> ReadSite(string name)
        {
            Assert.True(name == SourceName || name == DestinationName);
            var source = name == SourceName;
            var path = source ? resolvedSourceRoot : resolvedDestinationRoot;
            var snapshot = new SiteDto { Name = name, Id = source ? sourceId : destinationId, PhysicalPath = path,
                ApplicationRootPath = path, ApplicationPoolName = name };
            if (PauseSite == name && Interlocked.Exchange(ref paused, 1) == 0)
            { Entered.TrySetResult(); await Release.Task.WaitAsync(Token); }
            return snapshot;
        }
        private async Task<IReadOnlyCollection<string>> ReadAssignedSites(Guid user, CancellationToken token)
        {
            if (ScopeFailure is not null) throw ScopeFailure;
            if (PauseScope && Interlocked.Exchange(ref paused, 1) == 0)
            { Entered.TrySetResult(); await Release.Task.WaitAsync(Token); }
            return await scopes.GetAssignedSitesAsync(user, token);
        }
        public async Task WaitForBoundary(Task operation) =>
            Assert.Same(Entered.Task, await Task.WhenAny(Entered.Task, operation).WaitAsync(Token));
        public async Task<IActionResult> Run(CancellationToken? cancellation = null)
        {
            var token = cancellation ?? Token;
            return route switch
            {
                "delete" => (await controller.DeleteAsync(new DeleteFileRequest { Path = Paths[0] }, token)).Result!,
                "move" => (await controller.MoveAsync(new MoveFilesRequest { Paths = Paths, DestinationPath = $"sites/{DestinationName}/out", Overwrite = Overwrite }, token)).Result!,
                "copy" => (await controller.CopyAsync(new CopyFilesRequest { Paths = Paths, DestinationPath = $"sites/{DestinationName}/out", Overwrite = Overwrite }, token)).Result!,
                _ => throw new NotSupportedException(route)
            };
        }
        public async Task ChangeActor(string change)
        {
            switch (change)
            {
                case "disabled": await db.Users.Where(u => u.Id == actor).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, false), Token); break;
                case "deleted": await db.Users.Where(u => u.Id == actor).ExecuteDeleteAsync(Token); break;
                case "role": await db.Users.Where(u => u.Id == actor).ExecuteUpdateAsync(s => s.SetProperty(u => u.Role, UserRole.Administrator), Token); break;
                case "password": await db.Users.Where(u => u.Id == actor).ExecuteUpdateAsync(s => s.SetProperty(u => u.PasswordHash, "changed"), Token); break;
                case "logout": await sessions.RevokeAsync(sid, Token); break;
                case "source-scope": await scopes.SetScopeAsync(owner, [DestinationName], 0, 0, Token); break;
                case "destination-scope": await scopes.SetScopeAsync(owner, [SourceName], 0, 0, Token); break;
                case "section": await scopes.SetSubUserSectionsAsync(actor, [], Token); break;
                case "parent": await scopes.SetParentSubscriptionAsync(actor, other, Token); break;
                case "suspended": await scopes.SuspendAsync(owner, "manual", Token); break;
                default: throw new NotSupportedException(change);
            }
        }
        public void DisableActorSynchronously() => db.Users.Where(u => u.Id == actor).ExecuteUpdate(s => s.SetProperty(u => u.IsActive, false));
        public Task ReplaceSite(string side, string change)
        {
            var name = side == "source" ? SourceName : DestinationName;
            return SiteMutationGuard.RunMutationAsync(gates, name, SiteMutationGuard.MutationKind.IdentityChange, () =>
            {
                if (side == "source") { if (change == "id") sourceId++; if (change == "path") resolvedSourceRoot = foreignRoot; }
                else { if (change == "id") destinationId++; if (change == "path") resolvedDestinationRoot = foreignRoot; }
                return Task.FromResult(true);
            }, Token);
        }
        public void AssertUnchanged()
        {
            Assert.Equal("FIRST", File.ReadAllText(Source("first.txt")));
            Assert.Equal("SECOND", File.ReadAllText(Source("second.txt")));
            Assert.Equal("FIRST", File.ReadAllText(Path.Combine(foreignRoot, "first.txt")));
            Assert.Equal("SECOND", File.ReadAllText(Path.Combine(foreignRoot, "second.txt")));
            Assert.Empty(Directory.GetFiles(Path.Combine(destinationRoot, "out"), "*", SearchOption.AllDirectories));
            Assert.Empty(Directory.GetFiles(Path.Combine(foreignRoot, "out"), "*", SearchOption.AllDirectories));
        }
        public void AssertNoStagingTrees() => Assert.Empty(Directory.GetDirectories(Path.Combine(destinationRoot, "out"), ".adapanel-copy-*.tmp"));
        public void Dispose()
        {
            Release.TrySetResult(); services.Dispose(); db.Dispose(); connection.Dispose(); gates.Dispose(); lifetime.Dispose();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
    private static T Fake<T>(Func<MethodInfo, object?[]?, object?>? handler = null) where T : class
    {
        var proxy = DispatchProxy.Create<T, Proxy>();
        ((Proxy)(object)proxy).Handler = handler ?? ((method, _) => throw new NotSupportedException(method.Name));
        return proxy;
    }
    public class Proxy : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?> Handler { get; set; } = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => Handler(method!, args);
    }
}
