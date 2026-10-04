using System.IO.Compression;
using System.Data.Common;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using AdaPanel.Api.Controllers;
using AdaPanel.Api.Security;
using AdaPanel.Core.Common;
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
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class FilePublicationAuthorityTests
{
    public static IEnumerable<object[]> ActorChanges() =>
        from route in new[] { "write", "upload", "compress" }
        from change in new[] { "disabled", "deleted", "role", "password", "logout", "scope", "section", "parent", "suspended" }
        select new object[] { route, change };

    [Theory]
    [MemberData(nameof(ActorChanges))]
    public async Task AuthorityChangedWhileTheSiteWasBeingResolvedCannotPublishAFile(string route, string change)
    {
        using var f = await Fixture.Create(route);
        f.PauseResolver = true;
        var operation = Record.ExceptionAsync(() => f.Invoke());
        await f.Entered.Task.WaitAsync(f.Token);
        try { await f.ChangeActor(change); }
        finally { f.Release.TrySetResult(); }
        var error = await operation.WaitAsync(f.Token);
        f.AssertOriginalFiles();
        Assert.True(Assert.IsType<AppException>(error).HttpStatusCode is 401 or 403);
        Assert.Empty(f.Audits);
    }

    public static IEnumerable<object[]> GenerationChanges() =>
        from route in new[] { "write", "upload", "compress" }
        from change in new[] { "id", "path", "version" }
        select new object[] { route, change };

    [Theory]
    [MemberData(nameof(GenerationChanges))]
    public async Task TheSameNameCannotSilentlySelectAReplacementSite(string route, string change)
    {
        using var f = await Fixture.Create(route);
        f.PauseResolver = true;
        var operation = Record.ExceptionAsync(() => f.Invoke());
        await f.Entered.Task.WaitAsync(f.Token);
        try
        {
            if (change == "id") f.SiteId++;
            else if (change == "path") f.SitePath = f.OtherSitePath;
            else await SiteMutationGuard.RunMutationAsync(f.Gates, f.Name, SiteMutationGuard.MutationKind.IdentityChange,
                () => Task.FromResult(true), f.Token);
        }
        finally { f.Release.TrySetResult(); }
        var error = await operation.WaitAsync(f.Token);
        f.AssertOriginalFiles();
        Assert.Equal("sites.target_changed", Assert.IsType<AppException>(error).MessageKey);
        Assert.Empty(f.Audits);
    }

    [Theory]
    [InlineData("write")]
    [InlineData("upload")]
    [InlineData("compress")]
    public async Task ReplacementWhileTheInitialActorQueryWaitsCannotBecomeTheRequestedSite(string route)
    {
        using var f = await Fixture.Create(route);
        var foreignTarget = Path.Combine(f.OtherSitePath, Path.GetFileName(f.Target));
        File.Copy(f.Target, foreignTarget);
        File.WriteAllText(Path.Combine(f.OtherSitePath, "source.txt"), "FOREIGN SOURCE");
        var originalForeignTarget = File.ReadAllBytes(foreignTarget);
        f.PauseActorRead = true;
        var operation = Record.ExceptionAsync(() => f.Invoke());
        // Fail immediately if the operation bypasses the expected SQL boundary.
        Assert.Same(f.Entered.Task, await Task.WhenAny(f.Entered.Task, operation).WaitAsync(f.Token));
        try
        {
            await SiteMutationGuard.RunMutationAsync(f.Gates, f.Name, SiteMutationGuard.MutationKind.IdentityChange,
                () => { f.SiteId++; f.SitePath = f.OtherSitePath; return Task.FromResult(true); }, f.Token);
        }
        finally { f.Release.TrySetResult(); }
        var error = await operation.WaitAsync(f.Token);
        Assert.Equal(originalForeignTarget, File.ReadAllBytes(foreignTarget));
        Assert.Equal("sites.target_changed", Assert.IsType<AppException>(error).MessageKey);
        Assert.Empty(f.Audits);
        f.AssertNoTemporaryFiles();
        Assert.Empty(Directory.GetFiles(f.OtherSitePath, ".adapanel-*", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("upload", "password")]
    [InlineData("upload", "logout")]
    [InlineData("upload", "scope")]
    [InlineData("upload", "section")]
    [InlineData("compress", "password")]
    [InlineData("compress", "logout")]
    [InlineData("compress", "scope")]
    [InlineData("compress", "section")]
    public async Task AuthorityIsCheckedAgainAfterThePrivateContentHasBeenWritten(string route, string change)
    {
        using var f = await Fixture.Create(route);
        f.PauseContent = true;
        var operation = Record.ExceptionAsync(() => f.Invoke());
        await f.Entered.Task.WaitAsync(f.Token);
        try { await f.ChangeActor(change); }
        finally { f.Release.TrySetResult(); }
        var error = await operation.WaitAsync(f.Token);
        f.AssertOriginalFiles();
        Assert.True(Assert.IsType<AppException>(error).HttpStatusCode is 401 or 403);
        Assert.Empty(f.Audits);
    }

    [Theory]
    [InlineData("write")]
    [InlineData("upload")]
    [InlineData("compress")]
    public async Task TheSameActorAndSiteCanStillPublishNormally(string route)
    {
        using var f = await Fixture.Create(route);
        Assert.IsType<OkObjectResult>(await f.Invoke());
        Assert.Single(f.Audits);
        if (route == "compress")
        {
            using var zip = ZipFile.OpenRead(f.Target);
            using var input = new StreamReader(Assert.Single(zip.Entries).Open());
            Assert.Equal("NEW CONTENT", await input.ReadToEndAsync());
        }
        else Assert.Equal("NEW CONTENT", await File.ReadAllTextAsync(f.Target, f.Token));
        f.AssertNoTemporaryFiles();
    }

    [Theory]
    [InlineData("write")]
    [InlineData("upload")]
    [InlineData("compress")]
    public async Task CancellationWhileResolvingTheSitePreservesTheOriginalTarget(string route)
    {
        using var f = await Fixture.Create(route);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(f.Token);
        f.PauseResolver = true;
        var operation = Record.ExceptionAsync(() => f.Invoke(cancellation.Token));
        await f.Entered.Task.WaitAsync(f.Token);
        cancellation.Cancel();
        f.Release.TrySetResult();
        Assert.IsAssignableFrom<OperationCanceledException>(await operation.WaitAsync(f.Token));
        f.AssertOriginalFiles();
        Assert.Empty(f.Audits);
    }

    [Theory]
    [InlineData("write")]
    [InlineData("upload")]
    [InlineData("compress")]
    public async Task AnAuthenticatedPanelTokenKeepsItsNormalPublicationContract(string route)
    {
        using var f = await Fixture.Create(route);
        f.UsePanelToken();
        Assert.IsType<OkObjectResult>(await f.Invoke());
        Assert.Single(f.Audits);
        f.AssertNoTemporaryFiles();
    }

    [Theory]
    [InlineData("upload")]
    [InlineData("compress")]
    public async Task ContentWorkPinsTheGenerationAndReleasesItAfterCompletion(string route)
    {
        using var f = await Fixture.Create(route);
        f.PauseContent = true;
        var operation = f.Invoke();
        await f.Entered.Task.WaitAsync(f.Token);
        Exception? mutationError;
        try
        {
            mutationError = await Record.ExceptionAsync(() => SiteMutationGuard.RunMutationAsync(f.Gates, f.Name,
                SiteMutationGuard.MutationKind.Delete, () => Task.FromResult(true), f.Token));
        }
        finally { f.Release.TrySetResult(); }
        Assert.IsType<OkObjectResult>(await operation.WaitAsync(f.Token));
        Assert.Equal("sites.target_changed", Assert.IsType<AppException>(mutationError).MessageKey);
        Assert.True(await SiteMutationGuard.RunMutationAsync(f.Gates, f.Name,
            SiteMutationGuard.MutationKind.IdentityChange, () => Task.FromResult(true), f.Token));
    }

    [Fact]
    public async Task AuthorizedCreateOnlyKeepsTheExistingTargetIntact()
    {
        using var f = await Fixture.Create("write");
        f.CreateOnly = true;
        var error = await Assert.ThrowsAsync<AppException>(() => f.Invoke());
        Assert.Equal("files.already_exists", error.MessageKey);
        f.AssertOriginalFiles();
        Assert.Empty(f.Audits);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AuthorizedCreateOnlyPublishesNothingAfterAnAuthorityRejection(bool revoke)
    {
        using var f = await Fixture.Create("write");
        File.Delete(f.Target);
        f.CreateOnly = true;
        f.PauseResolver = true;
        var operation = Record.ExceptionAsync(() => f.Invoke());
        await f.Entered.Task.WaitAsync(f.Token);
        try { if (revoke) await f.ChangeActor("logout"); }
        finally { f.Release.TrySetResult(); }
        var error = await operation.WaitAsync(f.Token);
        if (revoke)
        {
            Assert.Equal(401, Assert.IsType<AppException>(error).HttpStatusCode);
            Assert.False(File.Exists(f.Target));
            Assert.Empty(f.Audits);
        }
        else
        {
            Assert.Null(error);
            Assert.Equal("NEW CONTENT", File.ReadAllText(f.Target));
            Assert.Single(f.Audits);
        }
        f.AssertNoTemporaryFiles();
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "adapanel-file-authority-" + Guid.NewGuid().ToString("N"));
        private readonly SqliteConnection connection = new("Data Source=:memory:");
        private readonly CancellationTokenSource lifetime = new(TimeSpan.FromSeconds(30));
        private readonly Guid actor = Guid.NewGuid(), owner = Guid.NewGuid(), otherOwner = Guid.NewGuid();
        private readonly string route;
        private readonly string originalSitePath;
        private readonly byte[] original;
        private readonly AdaPanelDbContext db;
        private readonly ServiceProvider services;
        private readonly CustomerScopeService scopes;
        private readonly SessionRevocationStore sessions;
        private readonly string sessionId = SessionRevocationStore.NewSessionId();
        private readonly FileService files;
        private readonly FilesController controller;
        private int resolved;
        public string Name { get; } = "files-" + Guid.NewGuid().ToString("N") + ".test";
        public string SitePath { get; set; }
        public long SiteId { get; set; } = 13;
        public string OtherSitePath { get; }
        public string Target { get; }
        public CancellationToken Token => lifetime.Token;
        public bool PauseResolver, PauseContent, CreateOnly, PauseActorRead;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public LocalGate Gates { get; } = new();
        public List<string> Audits { get; } = [];

        private Fixture(string route)
        {
            this.route = route;
            originalSitePath = SitePath = Path.Combine(root, "site");
            OtherSitePath = Path.Combine(root, "foreign");
            Directory.CreateDirectory(SitePath);
            Directory.CreateDirectory(OtherSitePath);
            Target = Path.Combine(SitePath, route == "compress" ? "result.zip" : "page.txt");
            File.WriteAllText(Path.Combine(SitePath, "source.txt"), "NEW CONTENT");
            File.WriteAllText(Path.Combine(OtherSitePath, "sentinel.txt"), "FOREIGN CONTENT");
            if (route == "compress")
            {
                using var zip = ZipFile.Open(Target, ZipArchiveMode.Create);
                using var writer = new StreamWriter(zip.CreateEntry("old.txt").Open());
                writer.Write("OLD CONTENT");
            }
            else File.WriteAllText(Target, "OLD CONTENT");
            original = File.ReadAllBytes(Target);

            connection.Open();
            db = new AdaPanelDbContext(new DbContextOptionsBuilder<AdaPanelDbContext>().UseSqlite(connection)
                .AddInterceptors(new ActorReadBarrier(this)).Options);
            db.Database.EnsureCreated();
            db.Users.AddRange(User(actor, "actor"), User(owner, "owner"), User(otherOwner, "other"));
            db.SaveChanges();
            var store = new JsonFileStore(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = Path.Combine(root, "panel") }));
            var sites = Fake<IIisSiteService>((method, args) => method.Name == nameof(IIisSiteService.GetSiteAsync)
                ? GetSite((string)args![0]!) : throw new NotSupportedException(method.Name));
            scopes = new CustomerScopeService(store, Fake<IServicePlanService>(),
                Fake<IResellerQuotaService>((method, _) => method.Name == nameof(IResellerQuotaService.GetQuotaAsync)
                    ? Task.FromResult<ResellerQuotaDto?>(null) : throw new NotSupportedException(method.Name)), sites);
            sessions = new SessionRevocationStore(store);
            files = new FileService(sites);
            files.OpenCompressionFile = path => new BarrierStream(File.ReadAllBytes(path), ContentBoundary);
            services = new ServiceCollection().AddSingleton(db).AddSingleton(sessions)
                .AddSingleton<IIisSiteService>(sites).AddSingleton<IConcurrencyLock>(Gates).BuildServiceProvider();
            controller = new FilesController(files, scopes,
                Fake<IAuditService>((method, _) => { Audits.Add(method.Name); return Task.CompletedTask; }), new ConcurrentWorkGate())
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext
                {
                    RequestServices = services,
                    User = new ClaimsPrincipal(new ClaimsIdentity([
                        new Claim(ClaimTypes.NameIdentifier, actor.ToString()), new Claim(ClaimTypes.Role, "Customer"),
                        new Claim(CookieSessionValidator.CredentialStampClaim, CookieSessionValidator.ComputeStamp("original-hash")),
                        new Claim(SessionRevocationStore.SessionIdClaim, sessionId)], CookieAuthenticationDefaults.AuthenticationScheme))
                } }
            };
        }

        public static async Task<Fixture> Create(string route)
        {
            var fixture = new Fixture(route);
            await fixture.scopes.SetScopeAsync(fixture.owner, [fixture.Name], 0, 0, fixture.Token);
            await fixture.scopes.SetScopeAsync(fixture.otherOwner, [], 0, 0, fixture.Token);
            await fixture.scopes.SetParentSubscriptionAsync(fixture.actor, fixture.owner, fixture.Token);
            await fixture.scopes.SetSubUserSectionsAsync(fixture.actor, ["files"], fixture.Token);
            return fixture;
        }

        private static PanelUser User(Guid id, string name) => new()
        { Id = id, UserName = name, Email = name + "@test.invalid", Role = UserRole.Customer, IsActive = true, PasswordHash = "original-hash" };

        private async Task<SiteDto> GetSite(string name)
        {
            Assert.Equal(Name, name);
            var snapshot = new SiteDto { Name = Name, Id = SiteId, PhysicalPath = SitePath, ApplicationRootPath = SitePath, ApplicationPoolName = Name };
            if (Interlocked.Increment(ref resolved) == 1 && PauseResolver)
            {
                Entered.TrySetResult();
                // Native probes can complete successfully after caller cancellation.
                await Release.Task.WaitAsync(Token);
            }
            return snapshot;
        }
        private async Task ContentBoundary(CancellationToken token)
        {
            if (!PauseContent) return;
            Entered.TrySetResult();
            await Release.Task.WaitAsync(token);
        }

        public async Task<IActionResult> Invoke(CancellationToken? token = null)
        {
            var ct = token ?? Token;
            return route switch
            {
                "write" => (await controller.WriteAsync(new WriteFileRequest { Path = $"sites/{Name}/page.txt", Content = "NEW CONTENT", FailIfExists = CreateOnly }, ct)).Result!,
                "compress" => (await controller.CompressAsync(new ArchiveFileRequest { Path = $"sites/{Name}/source.txt", DestinationPath = $"sites/{Name}/result.zip", Overwrite = true }, ct)).Result!,
                "upload" => (await controller.UploadAsync($"sites/{Name}", new Upload(new BarrierStream("NEW CONTENT"u8.ToArray(), ContentBoundary)), ct)).Result!,
                _ => throw new NotSupportedException(route)
            };
        }

        public async Task ChangeActor(string change)
        {
            switch (change)
            {
                case "disabled": await db.Users.Where(user => user.Id == actor).ExecuteUpdateAsync(set => set.SetProperty(user => user.IsActive, false), Token); break;
                case "deleted": await db.Users.Where(user => user.Id == actor).ExecuteDeleteAsync(Token); break;
                case "role": await db.Users.Where(user => user.Id == actor).ExecuteUpdateAsync(set => set.SetProperty(user => user.Role, UserRole.Administrator), Token); break;
                case "password": await db.Users.Where(user => user.Id == actor).ExecuteUpdateAsync(set => set.SetProperty(user => user.PasswordHash, "new-hash"), Token); break;
                case "logout": await sessions.RevokeAsync(sessionId, Token); break;
                case "scope": await scopes.SetScopeAsync(owner, [], 0, 0, Token); break;
                case "section": await scopes.SetSubUserSectionsAsync(actor, [], Token); break;
                case "parent": await scopes.SetParentSubscriptionAsync(actor, otherOwner, Token); break;
                case "suspended": await scopes.SuspendAsync(owner, "manual", Token); break;
                default: throw new NotSupportedException(change);
            }
        }
        public void UsePanelToken() => controller.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, actor.ToString()), new Claim(ClaimTypes.Role, "Customer"),
            new Claim(PanelTokenAuthenticationHandler.AllowDestructiveClaim, "true")], PanelTokenAuthenticationHandler.SchemeName));
        public void AssertOriginalFiles()
        {
            Assert.Equal(original, File.ReadAllBytes(Target));
            Assert.Equal("NEW CONTENT", File.ReadAllText(Path.Combine(originalSitePath, "source.txt")));
            Assert.Equal(["sentinel.txt"], Directory.GetFiles(OtherSitePath).Select(path => Path.GetFileName(path)!).ToArray());
            Assert.Equal("FOREIGN CONTENT", File.ReadAllText(Path.Combine(OtherSitePath, "sentinel.txt")));
            AssertNoTemporaryFiles();
        }
        public void AssertNoTemporaryFiles() => Assert.Empty(Directory.GetFiles(originalSitePath, ".adapanel-*", SearchOption.AllDirectories));
        public void Dispose()
        {
            Release.TrySetResult();
            services.Dispose(); db.Dispose(); connection.Dispose(); lifetime.Dispose(); Gates.Dispose();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }

        private sealed class ActorReadBarrier(Fixture fixture) : DbCommandInterceptor
        {
            private int paused;
            public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
                DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
                CancellationToken cancellationToken = default)
            {
                if (fixture.PauseActorRead && command.CommandText.Contains("\"panel_users\"", StringComparison.Ordinal)
                    && Interlocked.Exchange(ref paused, 1) == 0)
                {
                    fixture.Entered.TrySetResult();
                    await fixture.Release.Task.WaitAsync(fixture.Token);
                }
                return result;
            }
        }
    }

    private sealed class BarrierStream : MemoryStream
    {
        private readonly Func<CancellationToken, Task> boundary;
        private bool entered;
        public BarrierStream(byte[] content, Func<CancellationToken, Task> boundary) : base(content) { this.boundary = boundary; }
        public override async Task CopyToAsync(Stream destination, int bufferSize, CancellationToken token)
        {
            await base.CopyToAsync(destination, bufferSize, token);
            await EnterBoundary(token);
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            var read = await base.ReadAsync(buffer, token);
            if (Position == Length) await EnterBoundary(token);
            return read;
        }
        private async Task EnterBoundary(CancellationToken token)
        {
            if (entered) return;
            entered = true;
            await boundary(token);
        }
    }
    private sealed class Upload(Stream content) : IFormFile
    {
        public string ContentType => "text/plain";
        public string ContentDisposition => "form-data";
        public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();
        public long Length => content.Length;
        public string Name => "file";
        public string FileName => "page.txt";
        public Stream OpenReadStream() => content;
        public void CopyTo(Stream target) => content.CopyTo(target);
        public Task CopyToAsync(Stream target, CancellationToken token = default) => content.CopyToAsync(target, token);
    }
    public sealed class LocalGate : IConcurrencyLock, IDisposable
    {
        private readonly SemaphoreSlim gate = new(1, 1);
        public Task RunExclusiveAsync(Func<Task> action, CancellationToken token = default) => RunExclusiveAsync(ConcurrencyKeys.Iis, action, token);
        public Task<T> RunExclusiveAsync<T>(Func<Task<T>> action, CancellationToken token = default) => RunExclusiveAsync(ConcurrencyKeys.Iis, action, token);
        public async Task RunExclusiveAsync(string key, Func<Task> action, CancellationToken token = default)
        { await gate.WaitAsync(token); try { await action(); } finally { gate.Release(); } }
        public async Task<T> RunExclusiveAsync<T>(string key, Func<Task<T>> action, CancellationToken token = default)
        { await gate.WaitAsync(token); try { return await action(); } finally { gate.Release(); } }
        public void Dispose() => gate.Dispose();
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
