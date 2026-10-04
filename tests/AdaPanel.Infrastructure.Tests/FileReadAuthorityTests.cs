using System.Reflection;
using System.Security.Claims;
using System.Text;
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

public sealed class FileReadAuthorityTests
{
    public static IEnumerable<object[]> Revocations() =>
        from route in new[] { "workspace", "download", "root" }
        from change in new[] { "disabled", "deleted", "role", "password", "logout", "section", "parent" }
        select new object[] { route, change };

    [Theory]
    [MemberData(nameof(Revocations))]
    public async Task AQueuedReadCannotPublishForAnActorWhoseAuthorityWasRevoked(string route, string change)
    {
        using var f = await Fixture.Create(route);
        f.Pause = route == "root" ? "scope" : "site";
        var pending = Record.ExceptionAsync(() => f.ReadAndDispose());
        await f.WaitForBoundary(pending);
        try { await f.ChangeActor(change); }
        finally { f.Release.TrySetResult(); }
        Assert.True(Assert.IsType<AppException>(await pending.WaitAsync(f.Token)).HttpStatusCode is 401 or 403);
        Assert.False(f.Published);
        f.AssertOriginal();
    }

    [Theory]
    [InlineData("workspace")]
    [InlineData("download")]
    public async Task AResolvedFileCannotBePublishedAfterItsSiteIsRemovedFromTheSubscription(string route)
    {
        using var f = await Fixture.Create(route);
        f.Pause = "site";
        var pending = Record.ExceptionAsync(() => f.ReadAndDispose());
        await f.WaitForBoundary(pending);
        try { await f.ChangeActor("scope"); }
        finally { f.Release.TrySetResult(); }
        Assert.Equal(403, Assert.IsType<AppException>(await pending.WaitAsync(f.Token)).HttpStatusCode);
        Assert.False(f.Published);
    }

    [Fact]
    public async Task ARootListingDoesNotPublishASiteFromAnOutdatedScopeSnapshot()
    {
        using var f = await Fixture.Create("root");
        f.Pause = "scope";
        var pending = f.Read();
        await f.WaitForBoundary(pending);
        try { await f.ChangeActor("scope"); }
        finally { f.Release.TrySetResult(); }
        var result = Workspace(await pending.WaitAsync(f.Token));
        Assert.DoesNotContain(result.Entries, item => item.Name == f.SiteName);
    }

    public static IEnumerable<object[]> SiteChanges() =>
        from route in new[] { "workspace", "download" }
        from change in new[] { "id", "path", "version" }
        select new object[] { route, change };

    [Theory]
    [MemberData(nameof(SiteChanges))]
    public async Task TheReadRemainsBoundToTheGenerationRequestedBeforeTheSiteAwait(string route, string change)
    {
        using var f = await Fixture.Create(route);
        f.Pause = "site";
        var pending = Record.ExceptionAsync(() => f.ReadAndDispose());
        await f.WaitForBoundary(pending);
        try { await f.ReplaceSite(change); }
        finally { f.Release.TrySetResult(); }
        Assert.Equal("sites.target_changed", Assert.IsType<AppException>(await pending.WaitAsync(f.Token)).MessageKey);
        Assert.False(f.Published);
    }

    [Theory]
    [InlineData("workspace")]
    [InlineData("download")]
    public async Task AReplacementDuringInitialScopeReadCannotBecomeTheApprovedSite(string route)
    {
        using var f = await Fixture.Create(route);
        f.Pause = "scope";
        var pending = Record.ExceptionAsync(() => f.ReadAndDispose());
        await f.WaitForBoundary(pending);
        try { await f.ReplaceSite("path"); }
        finally { f.Release.TrySetResult(); }
        Assert.Equal("sites.target_changed", Assert.IsType<AppException>(await pending.WaitAsync(f.Token)).MessageKey);
        Assert.False(f.Published);
    }

    [Theory]
    [InlineData("password")]
    [InlineData("logout")]
    [InlineData("scope")]
    public async Task AuthorityIsCheckedAfterActualFileIoAndTheRejectedHandleIsClosed(string change)
    {
        using var f = await Fixture.Create("workspace");
        f.Pause = "bytes";
        var pending = Record.ExceptionAsync(() => f.ReadAndDispose());
        await f.WaitForBoundary(pending);
        try { await f.ChangeActor(change); }
        finally { f.Release.TrySetResult(); }
        Assert.True(Assert.IsType<AppException>(await pending.WaitAsync(f.Token)).HttpStatusCode is 401 or 403);
        Assert.False(f.Published);
        Assert.True(f.ReadStreamDisposed);
        using var exclusive = new FileStream(f.FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Theory]
    [InlineData("workspace")]
    [InlineData("download")]
    [InlineData("root")]
    public async Task CancellationAfterAnIgnoringReadDoesNotPublishTheResponse(string route)
    {
        using var f = await Fixture.Create(route);
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(f.Token);
        f.Pause = route == "root" ? "scope" : "site";
        var pending = Record.ExceptionAsync(() => f.ReadAndDispose(canceled.Token));
        await f.WaitForBoundary(pending);
        canceled.Cancel(); f.Release.TrySetResult();
        Assert.IsAssignableFrom<OperationCanceledException>(await pending.WaitAsync(f.Token));
        Assert.False(f.Published);
    }

    public static IEnumerable<object[]> CurrentActors() =>
        from route in new[] { "workspace", "download", "root" }
        from identity in new[] { "cookie", "token", "administrator" }
        select new object[] { route, identity };

    [Theory]
    [MemberData(nameof(CurrentActors))]
    public async Task CurrentCookieAndApiActorsStillReadTheirFiles(string route, string identity)
    {
        using var f = await Fixture.Create(route, identity);
        var result = await f.Read();
        if (route == "download")
        {
            Assert.Equal("ORIGINAL SECRET", await ReadDownloadedText(result));
            Assert.Equal("application/octet-stream", Assert.IsAssignableFrom<FileResult>(result).ContentType);
        }
        else
        {
            var workspace = Workspace(result);
            if (route == "workspace") Assert.Equal("ORIGINAL SECRET", workspace.Content);
            else Assert.Contains(workspace.Entries, entry => entry.Name == f.SiteName);
        }
        f.AssertOriginal();
    }

    [Theory]
    [InlineData("workspace")]
    [InlineData("download")]
    [InlineData("root")]
    public async Task SuspensionKeepsExistingReadOnlyAccessAvailable(string route)
    {
        using var f = await Fixture.Create(route);
        await f.ChangeActor("suspended");
        await f.ReadAndDispose();
        Assert.True(f.Published);
        f.AssertOriginal();
    }

    [Fact]
    public async Task ADownloadPublishesTheAlreadyOpenedFileRatherThanReopeningItsPathLater()
    {
        using var f = await Fixture.Create("download");
        var result = await f.Read();
        try
        {
            // A safe open handle may block this competing writer, or retain the original object.
            // The old PhysicalFileResult permits this write and later sends the replacement bytes.
            try { File.WriteAllText(f.FilePath, "REPLACEMENT SECRET"); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            Assert.Equal("ORIGINAL SECRET", await ReadDownloadedText(result));
        }
        finally { if (result is FileStreamResult stream) await stream.FileStream.DisposeAsync(); }
        using var exclusive = new FileStream(f.FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Theory]
    [InlineData("cookie")]
    [InlineData("administrator")]
    public async Task WorkspacePhysicalPathsAreProjectedForTheAcceptedActor(string identity)
    {
        using var f = await Fixture.Create("workspace", identity);
        var workspace = Workspace(await f.Read());
        Assert.Equal(identity == "administrator" ? f.FilePath : string.Empty, workspace.PhysicalPath);
        Assert.Equal("ORIGINAL SECRET", workspace.Content);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnAuthorizedGetNeverCreatesAMissingDirectoryEvenForASuspendedReader(bool suspended)
    {
        using var f = await Fixture.Create("workspace");
        if (suspended) await f.ChangeActor("suspended");
        var missing = Path.Combine(Path.GetDirectoryName(f.FilePath)!, "absent");
        f.PathOverride = $"sites/{f.SiteName}/absent";
        var error = await Record.ExceptionAsync(() => f.ReadAndDispose());
        Assert.False(Directory.Exists(missing));
        Assert.Equal("files.not_found", Assert.IsType<AppException>(error).MessageKey);
        Assert.False(f.Published);
        // The old internal/trusted workspace behavior remains a separate compatibility path.
        var trusted = await f.Files.GetWorkspaceAsync(f.PathOverride);
        Assert.True(trusted.IsDirectory);
        Assert.True(Directory.Exists(missing));
    }

    [Fact]
    public async Task ARejectionAfterTheDownloadHandleWasOpenedClosesItBeforeReturning()
    {
        using var f = await Fixture.Create("download");
        f.Pause = "opened";
        var pending = Record.ExceptionAsync(() => f.ReadAndDispose());
        await f.WaitForBoundary(pending);
        try { await f.ChangeActor("logout"); }
        finally { f.Release.TrySetResult(); }
        Assert.Equal(401, Assert.IsType<AppException>(await pending.WaitAsync(f.Token)).HttpStatusCode);
        Assert.False(f.Published);
        using var exclusive = new FileStream(f.FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var reader = new StreamReader(exclusive, leaveOpen: true);
        Assert.Equal("ORIGINAL SECRET", reader.ReadToEnd());
    }

    private static FileWorkspaceDto Workspace(IActionResult result) =>
        Assert.IsType<ApiResponse<FileWorkspaceDto>>(Assert.IsType<OkObjectResult>(result).Value).Data!;
    private static async Task<string> ReadDownloadedText(IActionResult result)
    {
        if (result is PhysicalFileResult physical) return await File.ReadAllTextAsync(physical.FileName);
        var stream = Assert.IsType<FileStreamResult>(result).FileStream;
        await using (stream)
        using (var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true))
            return await reader.ReadToEndAsync();
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "adapanel-file-read-" + Guid.NewGuid().ToString("N"));
        private readonly SqliteConnection connection = new("Data Source=:memory:");
        private readonly CancellationTokenSource lifetime = new(TimeSpan.FromSeconds(30));
        private readonly Guid actor = Guid.NewGuid(), owner = Guid.NewGuid(), other = Guid.NewGuid();
        private readonly string sid = SessionRevocationStore.NewSessionId();
        private readonly AdaPanelDbContext db;
        private readonly CustomerScopeService scopes;
        private readonly SessionRevocationStore sessions;
        private readonly ServiceProvider services;
        private readonly FilesController controller;
        private readonly FilePublicationAuthorityTests.LocalGate gates = new();
        private readonly string route, siteRoot, foreignRoot;
        private string resolvedRoot;
        private long siteId = 12;
        private int paused;
        public string SiteName { get; } = "read-" + Guid.NewGuid().ToString("N") + ".test";
        public string FilePath => Path.Combine(siteRoot, "secret.txt");
        public CancellationToken Token => lifetime.Token;
        public string? Pause;
        public string? PathOverride;
        public FileService Files { get; }
        public bool Published, ReadStreamDisposed;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private Fixture(string route, string identity)
        {
            this.route = route;
            siteRoot = resolvedRoot = Path.Combine(root, "site"); foreignRoot = Path.Combine(root, "foreign");
            Directory.CreateDirectory(siteRoot); Directory.CreateDirectory(foreignRoot);
            File.WriteAllText(FilePath, "ORIGINAL SECRET");
            File.WriteAllText(Path.Combine(foreignRoot, "secret.txt"), "FOREIGN SECRET");
            connection.Open();
            db = new AdaPanelDbContext(new DbContextOptionsBuilder<AdaPanelDbContext>().UseSqlite(connection).Options);
            db.Database.EnsureCreated();
            var user = User(actor, "actor"); if (identity == "administrator") user.Role = UserRole.Administrator;
            db.Users.AddRange(user, User(owner, "owner"), User(other, "other")); db.SaveChanges();
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
            var scopeProxy = Fake<ICustomerScopeService>((method, args) => method.Name == nameof(ICustomerScopeService.GetAssignedSitesAsync)
                ? ReadScope((Guid)args![0]!, (CancellationToken)args[1]!) : method.Invoke(scopes, args));
            sessions = new SessionRevocationStore(store);
            Files = new FileService(sites)
            {
                OpenWorkspaceFile = path => new ObservedStream(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read),
                    () => Boundary("bytes"), () => ReadStreamDisposed = true)
            };
            services = new ServiceCollection().AddSingleton(db).AddSingleton(sessions)
                .AddSingleton<IIisSiteService>(sites).AddSingleton<IConcurrencyLock>(gates).BuildServiceProvider();
            var claims = new[] { new Claim(ClaimTypes.NameIdentifier, actor.ToString()), new Claim(ClaimTypes.Role, user.Role.ToString()),
                new Claim(CookieSessionValidator.CredentialStampClaim, CookieSessionValidator.ComputeStamp("original")),
                new Claim(SessionRevocationStore.SessionIdClaim, sid) };
            controller = new FilesController(Files, scopeProxy, Fake<IAuditService>(), new ConcurrentWorkGate())
            { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { RequestServices = services,
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, identity == "token" ? PanelTokenAuthenticationHandler.SchemeName : CookieAuthenticationDefaults.AuthenticationScheme)) } } };
        }
        public static async Task<Fixture> Create(string route, string identity = "cookie")
        {
            var f = new Fixture(route, identity);
            await f.scopes.SetScopeAsync(f.owner, [f.SiteName], 0, 0, f.Token);
            await f.scopes.SetScopeAsync(f.other, [], 0, 0, f.Token);
            if (identity != "administrator") await f.scopes.SetParentSubscriptionAsync(f.actor, f.owner, f.Token);
            await f.scopes.SetSubUserSectionsAsync(f.actor, ["files"], f.Token);
            return f;
        }
        private static PanelUser User(Guid id, string name) => new()
        { Id = id, UserName = name, Email = name + "@test.invalid", Role = UserRole.Customer, IsActive = true, PasswordHash = "original" };
        private SiteDto Site() => new() { Name = SiteName, Id = siteId, PhysicalPath = resolvedRoot,
            ApplicationRootPath = resolvedRoot, ApplicationPoolName = SiteName };
        private async Task<SiteDto> ReadSite() { var snapshot = Site(); await Boundary("site"); return snapshot; }
        private async Task<IReadOnlyCollection<string>> ReadScope(Guid user, CancellationToken token)
        {
            var snapshot = await scopes.GetAssignedSitesAsync(user, token);
            if (Pause == "opened")
            {
                var held = false;
                try { using var probe = new FileStream(FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
                catch (IOException) { held = true; }
                if (held) await Boundary("opened");
            }
            await Boundary("scope"); return snapshot;
        }
        private async Task Boundary(string boundary)
        {
            if (Pause != boundary || Interlocked.Exchange(ref paused, 1) != 0) return;
            Entered.TrySetResult(); await Release.Task.WaitAsync(Token);
        }
        public async Task WaitForBoundary(Task pending) => Assert.Same(Entered.Task, await Task.WhenAny(Entered.Task, pending).WaitAsync(Token));
        public async Task<IActionResult> Read(CancellationToken? cancellation = null)
        {
            var token = cancellation ?? Token;
            var result = route == "download" ? await controller.DownloadAsync($"sites/{SiteName}/secret.txt", token)
                : (await controller.GetWorkspaceAsync(PathOverride ?? (route == "root" ? "sites" : $"sites/{SiteName}/secret.txt"), token)).Result!;
            Published = true; return result;
        }
        public async Task ReadAndDispose(CancellationToken? cancellation = null)
        { if (await Read(cancellation) is FileStreamResult stream) await stream.FileStream.DisposeAsync(); }
        public async Task ChangeActor(string change)
        {
            switch (change)
            {
                case "disabled": await db.Users.Where(u => u.Id == actor).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, false), Token); break;
                case "deleted": await db.Users.Where(u => u.Id == actor).ExecuteDeleteAsync(Token); break;
                case "role": await db.Users.Where(u => u.Id == actor).ExecuteUpdateAsync(s => s.SetProperty(u => u.Role, UserRole.Administrator), Token); break;
                case "password": await db.Users.Where(u => u.Id == actor).ExecuteUpdateAsync(s => s.SetProperty(u => u.PasswordHash, "changed"), Token); break;
                case "logout": await sessions.RevokeAsync(sid, Token); break;
                case "scope": await scopes.SetScopeAsync(owner, [], 0, 0, Token); break;
                case "section": await scopes.SetSubUserSectionsAsync(actor, [], Token); break;
                case "parent": await scopes.SetParentSubscriptionAsync(actor, other, Token); break;
                case "suspended": await scopes.SuspendAsync(owner, "manual", Token); break;
                default: throw new NotSupportedException(change);
            }
        }
        public Task ReplaceSite(string change) => SiteMutationGuard.RunMutationAsync(gates, SiteName, SiteMutationGuard.MutationKind.IdentityChange, () =>
        { if (change == "id") siteId++; if (change == "path") resolvedRoot = foreignRoot; return Task.FromResult(true); }, Token);
        public void AssertOriginal() { Assert.Equal("ORIGINAL SECRET", File.ReadAllText(FilePath)); Assert.Equal("FOREIGN SECRET", File.ReadAllText(Path.Combine(foreignRoot, "secret.txt"))); }
        public void Dispose()
        { Release.TrySetResult(); services.Dispose(); db.Dispose(); connection.Dispose(); gates.Dispose(); lifetime.Dispose(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class ObservedStream(Stream inner, Func<Task> afterRead, Action disposed) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        { var count = await inner.ReadAsync(buffer, token); await afterRead(); return count; }
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token)
        { var read = await inner.ReadAsync(buffer.AsMemory(offset, count), token); await afterRead(); return read; }
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void Flush() => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) { inner.Dispose(); disposed(); } base.Dispose(disposing); }
    }
    private static T Fake<T>(Func<MethodInfo, object?[]?, object?>? handler = null) where T : class
    { var proxy = DispatchProxy.Create<T, Proxy>(); ((Proxy)(object)proxy).Handler = handler ?? ((method, _) => throw new NotSupportedException(method.Name)); return proxy; }
    public class Proxy : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?> Handler { get; set; } = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => Handler(method!, args);
    }
}
