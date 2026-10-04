using System.IO.Compression;
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

public sealed class FileArchiveSearchSizeAuthorityTests
{
    public static IEnumerable<object[]> ActorChanges() =>
        from route in new[] { "extract", "search", "size" }
        from change in new[] { "disabled", "deleted", "role", "password", "logout", "scope", "section", "parent" }
        select new object[] { route, change };

    [Theory]
    [MemberData(nameof(ActorChanges))]
    public async Task AQueuedOperationCannotUseRevokedAuthority(string route, string change)
    {
        using var f = await Fixture.Create(route);
        f.Pause = "site";
        var pending = Record.ExceptionAsync(() => f.Run());
        await f.WaitForBoundary(pending);
        try { await f.ChangeActor(change); }
        finally { f.Release.TrySetResult(); }
        Assert.True(Assert.IsType<AppException>(await pending.WaitAsync(f.Token)).HttpStatusCode is 401 or 403);
        f.AssertNoPublicationOrWrite();
    }

    public static IEnumerable<object[]> SiteChanges() =>
        from route in new[] { "extract", "search", "size" }
        from change in new[] { "id", "path", "version" }
        select new object[] { route, change };

    [Theory]
    [MemberData(nameof(SiteChanges))]
    public async Task TheRequestedSiteGenerationSurvivesTheInitialResolverWait(string route, string change)
    {
        using var f = await Fixture.Create(route);
        f.Pause = "site";
        var pending = Record.ExceptionAsync(() => f.Run());
        await f.WaitForBoundary(pending);
        try { await f.ReplaceSite(false, change); }
        finally { f.Release.TrySetResult(); }
        Assert.Equal("sites.target_changed", Assert.IsType<AppException>(await pending.WaitAsync(f.Token)).MessageKey);
        f.AssertNoPublicationOrWrite();
    }

    [Theory]
    [InlineData("extract")]
    [InlineData("search")]
    [InlineData("size")]
    public async Task SiteCapturePrecedesTheFirstScopeAwait(string route)
    {
        using var f = await Fixture.Create(route);
        f.Pause = "scope";
        var pending = Record.ExceptionAsync(() => f.Run());
        await f.WaitForBoundary(pending);
        try { await f.ReplaceSite(false, "path"); }
        finally { f.Release.TrySetResult(); }
        Assert.Equal("sites.target_changed", Assert.IsType<AppException>(await pending.WaitAsync(f.Token)).MessageKey);
        f.AssertNoPublicationOrWrite();
    }

    [Theory]
    [InlineData("extract")]
    [InlineData("search")]
    [InlineData("size")]
    public async Task MutableRequestObjectsCannotSelectDifferentInputsAfterAuthorization(string route)
    {
        using var f = await Fixture.Create(route);
        f.Pause = "scope";
        var pending = f.Run();
        await f.WaitForBoundary(pending);
        f.ArchiveRequest.Path = $"sites/{f.SourceName}/different.zip";
        f.ArchiveRequest.DestinationPath = $"sites/{f.DestinationName}/unselected";
        f.SearchRequest.Path = $"sites/{f.SourceName}/unselected";
        f.SearchRequest.Query = "NEVER";
        f.SizeRequest.Path = $"sites/{f.SourceName}/unselected";
        f.Release.TrySetResult();
        var result = await pending.WaitAsync(f.Token);
        if (route == "extract")
        {
            Assert.Equal("FIRST", File.ReadAllText(Path.Combine(f.Output, "one.txt")));
            Assert.False(Directory.Exists(Path.Combine(f.DestinationRoot, "unselected")));
        }
        else if (route == "search")
        {
            var search = Assert.IsType<FileSearchResultDto>(result);
            Assert.Equal("MATCH", search.Query);
            Assert.Equal(f.SourcePath, search.Path);
            Assert.Contains(search.Items, item => item.Name == "alpha.txt");
        }
        else Assert.Equal(f.SourcePath, Assert.IsType<DirectorySizeDto>(result).Path);
    }

    [Theory]
    [InlineData("extract")]
    [InlineData("search")]
    [InlineData("size")]
    public async Task AReplacementPrincipalCannotAdoptARevokedRequest(string route)
    {
        using var f = await Fixture.Create(route);
        f.Pause = "site";
        var pending = Record.ExceptionAsync(() => f.Run());
        await f.WaitForBoundary(pending);
        try { await f.ChangeActor("logout"); f.ReplacePrincipal(); }
        finally { f.Release.TrySetResult(); }
        Assert.Equal(401, Assert.IsType<AppException>(await pending.WaitAsync(f.Token)).HttpStatusCode);
        f.AssertNoPublicationOrWrite();
    }

    [Theory]
    [InlineData("extract")]
    [InlineData("search")]
    [InlineData("size")]
    public async Task CancellationAfterAnIgnoringResolverPreventsPublication(string route)
    {
        using var f = await Fixture.Create(route);
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(f.Token);
        f.Pause = "site";
        var pending = Record.ExceptionAsync(() => f.Run(canceled.Token));
        await f.WaitForBoundary(pending);
        canceled.Cancel(); f.Release.TrySetResult();
        Assert.IsAssignableFrom<OperationCanceledException>(await pending.WaitAsync(f.Token));
        f.AssertNoPublicationOrWrite();
    }

    [Theory]
    [InlineData("search", false)]
    [InlineData("search", true)]
    [InlineData("size", false)]
    [InlineData("size", true)]
    public async Task ActualContentOrMeasurementCannotPublishAfterItsFinalWait(string route, bool cancel)
    {
        using var f = await Fixture.Create(route);
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(f.Token);
        f.Pause = "read-completed";
        var pending = Record.ExceptionAsync(() => f.Run(canceled.Token));
        await f.WaitForBoundary(pending);
        try { if (cancel) canceled.Cancel(); else await f.ChangeActor("logout"); }
        finally { f.Release.TrySetResult(); }
        var error = await pending.WaitAsync(f.Token);
        if (cancel) Assert.IsAssignableFrom<OperationCanceledException>(error);
        else Assert.Equal(401, Assert.IsType<AppException>(error).HttpStatusCode);
        Assert.False(f.Published);
    }

    [Theory]
    [InlineData("logout")]
    [InlineData("scope")]
    [InlineData("cancel")]
    public async Task ExtractionStopsBeforeTheNextEntryWithoutUndoingCompletedFiles(string change)
    {
        using var f = await Fixture.Create("extract");
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(f.Token);
        f.Pause = "second-entry";
        var pending = Record.ExceptionAsync(() => f.Run(canceled.Token));
        await f.WaitForBoundary(pending);
        Assert.Equal("FIRST", File.ReadAllText(Path.Combine(f.Output, "one.txt")));
        try { if (change == "cancel") canceled.Cancel(); else await f.ChangeActor(change); }
        finally { f.Release.TrySetResult(); }
        var error = await pending.WaitAsync(f.Token);
        if (change == "cancel") Assert.IsAssignableFrom<OperationCanceledException>(error);
        else Assert.True(Assert.IsType<AppException>(error).HttpStatusCode is 401 or 403);
        Assert.False(File.Exists(Path.Combine(f.Output, "two.txt")));
        Assert.Equal("FIRST", File.ReadAllText(Path.Combine(f.Output, "one.txt")));
        Assert.Empty(f.Audits);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExtractionPinsBothTheArchiveSiteAndDestinationSite(bool destination)
    {
        using var f = await Fixture.Create("extract");
        f.Pause = "second-entry";
        var pending = f.Run();
        await f.WaitForBoundary(pending);
        Exception? replacement;
        try { replacement = await Record.ExceptionAsync(() => f.ReplaceSite(destination, "version")); }
        finally { f.Release.TrySetResult(); }
        await pending.WaitAsync(f.Token);
        Assert.Equal("sites.target_changed", Assert.IsType<AppException>(replacement).MessageKey);
        await f.ReplaceSite(destination, "version");
    }

    [Fact]
    public async Task ExtractionCannotAdoptAReplacementDuringItsDestinationResolution()
    {
        using var f = await Fixture.Create("extract");
        f.Pause = "destination";
        var pending = Record.ExceptionAsync(() => f.Run());
        await f.WaitForBoundary(pending);
        try { await f.ReplaceSite(true, "path"); }
        finally { f.Release.TrySetResult(); }
        Assert.Equal("sites.target_changed", Assert.IsType<AppException>(await pending.WaitAsync(f.Token)).MessageKey);
        f.AssertNoPublicationOrWrite();
    }

    [Theory]
    [InlineData("search")]
    [InlineData("size")]
    public async Task SuspendedCustomersKeepTheirReadOnlySiteRootOperations(string route)
    {
        using var f = await Fixture.Create(route);
        await f.ChangeActor("suspended");
        Assert.NotNull(await f.Run());
        Assert.Empty(f.Audits);
    }

    [Fact]
    public async Task ASubscriptionSuspendedDuringExtractionResolutionCannotWrite()
    {
        using var f = await Fixture.Create("extract");
        f.Pause = "site";
        var pending = Record.ExceptionAsync(() => f.Run());
        await f.WaitForBoundary(pending);
        try { await f.ChangeActor("suspended"); }
        finally { f.Release.TrySetResult(); }
        Assert.Equal(403, Assert.IsType<AppException>(await pending.WaitAsync(f.Token)).HttpStatusCode);
        f.AssertNoPublicationOrWrite();
    }

    [Theory]
    [InlineData("extract")]
    [InlineData("search")]
    [InlineData("size")]
    public async Task DelegatedOperationsStillDependOnTheOriginalAdministratorSession(string route)
    {
        using var f = await Fixture.Create(route, "delegated");
        f.Pause = "site";
        var pending = Record.ExceptionAsync(() => f.Run());
        await f.WaitForBoundary(pending);
        try { await f.RevokeDelegator(); }
        finally { f.Release.TrySetResult(); }
        Assert.Equal(401, Assert.IsType<AppException>(await pending.WaitAsync(f.Token)).HttpStatusCode);
        f.AssertNoPublicationOrWrite();
    }

    public static IEnumerable<object[]> PositiveActors() =>
        from route in new[] { "extract", "search", "size" }
        from actor in new[] { "cookie", "token", "administrator" }
        select new object[] { route, actor };

    [Theory]
    [MemberData(nameof(PositiveActors))]
    public async Task CurrentActorsKeepMeasuredResultsAndNormalRootBehavior(string route, string identity)
    {
        using var f = await Fixture.Create(route, identity);
        var result = await f.Run();
        if (route == "extract")
        {
            Assert.Equal("SECOND", File.ReadAllText(Path.Combine(f.Output, "two.txt")));
            Assert.Equal(identity == "administrator" ? f.Output : string.Empty, Assert.IsType<FileWorkspaceDto>(result).PhysicalPath);
            Assert.Single(f.Audits);
        }
        else if (route == "search")
        {
            var search = Assert.IsType<FileSearchResultDto>(result);
            Assert.False(search.Truncated);
            Assert.Equal(string.Empty, search.TruncatedReasonKey);
            Assert.Equal(f.SourcePath, search.Path);
            Assert.Equal("MATCH", search.Query);
            Assert.Contains(search.Items, item => item.Path == f.SourcePath + "/alpha.txt");
            Assert.All(search.Items, item => Assert.StartsWith(f.SourcePath + "/", item.Path));
            Assert.Empty(f.Audits);
        }
        else
        {
            var size = Assert.IsType<DirectorySizeDto>(result);
            Assert.Equal(f.SourcePath, size.Path);
            Assert.False(size.Truncated);
            Assert.Equal(Directory.GetFiles(f.SourceRoot).Sum(path => new FileInfo(path).Length), size.TotalBytes);
            Assert.Empty(f.Audits);
        }
    }

    [Theory]
    [InlineData("search", "")]
    [InlineData("search", "sites")]
    [InlineData("size", "")]
    [InlineData("size", "sites")]
    public async Task ScopedRootUrisCannotBecomeGlobalScans(string route, string path)
    {
        using var f = await Fixture.Create(route);
        f.SearchRequest.Path = path; f.SizeRequest.Path = path;
        Assert.Equal(403, Assert.IsType<AppException>(await Record.ExceptionAsync(() => f.Run())).HttpStatusCode);
        Assert.False(f.Published);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "adapanel-file-archive-search-size-" + Guid.NewGuid().ToString("N"));
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
        private readonly string route, foreignSource, foreignDestination;
        private string resolvedSource, resolvedDestination;
        private long sourceId = 12, destinationId = 24;
        private int paused;
        public string SourceName { get; } = "archive-source-" + Guid.NewGuid().ToString("N") + ".test";
        public string DestinationName { get; } = "archive-destination-" + Guid.NewGuid().ToString("N") + ".test";
        public string SourceRoot { get; }
        public string DestinationRoot { get; }
        public string SourcePath => $"sites/{SourceName}";
        public string Output => Path.Combine(DestinationRoot, "output");
        public CancellationToken Token => lifetime.Token;
        public string? Pause;
        public bool Published;
        public FileService Files { get; }
        public ArchiveFileRequest ArchiveRequest { get; }
        public SearchFilesRequest SearchRequest { get; }
        public DirectorySizeRequest SizeRequest { get; }
        public List<string> Audits { get; } = [];
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private Fixture(string route, string identity)
        {
            this.route = route;
            SourceRoot = resolvedSource = Path.Combine(root, "source");
            DestinationRoot = resolvedDestination = Path.Combine(root, "destination");
            foreignSource = Path.Combine(root, "foreign-source"); foreignDestination = Path.Combine(root, "foreign-destination");
            foreach (var directory in new[] { SourceRoot, DestinationRoot, foreignSource, foreignDestination }) Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(SourceRoot, "alpha.txt"), "MATCH ORIGINAL");
            File.WriteAllText(Path.Combine(foreignSource, "alpha.txt"), "MATCH FOREIGN");
            foreach (var directory in new[] { SourceRoot, foreignSource })
            {
                using var zip = ZipFile.Open(Path.Combine(directory, "bundle.zip"), ZipArchiveMode.Create);
                using (var text = new StreamWriter(zip.CreateEntry("one.txt").Open())) text.Write("FIRST");
                using (var text = new StreamWriter(zip.CreateEntry("two.txt").Open())) text.Write("SECOND");
            }
            ArchiveRequest = new() { Path = SourcePath + "/bundle.zip", DestinationPath = $"sites/{DestinationName}/output" };
            SearchRequest = new() { Path = SourcePath, Query = "MATCH" };
            SizeRequest = new() { Path = SourcePath };
            connection.Open();
            db = new AdaPanelDbContext(new DbContextOptionsBuilder<AdaPanelDbContext>().UseSqlite(connection).Options);
            db.Database.EnsureCreated();
            var user = User(actor, "actor", identity == "administrator" ? UserRole.Administrator : UserRole.Customer);
            db.Users.AddRange(user, User(owner, "owner"), User(other, "other", UserRole.Administrator), User(delegator, "delegator", UserRole.Administrator)); db.SaveChanges();
            var store = new JsonFileStore(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = Path.Combine(root, "panel") }));
            var sites = Fake<IIisSiteService>((method, args) => method.Name switch
            {
                nameof(IIisSiteService.GetSiteAsync) => ReadSite((string)args![0]!),
                nameof(IIisSiteService.GetSitesAsync) => Task.FromResult<IReadOnlyCollection<SiteDto>>([Site(false), Site(true)]),
                _ => throw new NotSupportedException(method.Name)
            });
            scopes = new CustomerScopeService(store, Fake<IServicePlanService>(),
                Fake<IResellerQuotaService>((method, _) => method.Name == nameof(IResellerQuotaService.GetQuotaAsync)
                    ? Task.FromResult<ResellerQuotaDto?>(null) : throw new NotSupportedException(method.Name)), sites);
            var proxy = Fake<ICustomerScopeService>((method, args) => method.Name == nameof(ICustomerScopeService.GetAssignedSitesAsync)
                ? ReadScope((Guid)args![0]!, (CancellationToken)args[1]!) : method.Invoke(scopes, args));
            sessions = new SessionRevocationStore(store);
            Files = new FileService(sites)
            {
                ReadSearchContent = async (path, token) => { var content = await File.ReadAllTextAsync(path, token); await Boundary("read-completed"); return content; },
                SizeWalkCompleted = _ => Boundary("read-completed"),
                ExtractEntryReady = (entry, _) => entry == "two.txt" ? Boundary("second-entry") : Task.CompletedTask
            };
            services = new ServiceCollection().AddSingleton(db).AddSingleton(sessions).AddSingleton<IIisSiteService>(sites)
                .AddSingleton<IConcurrencyLock>(gates).BuildServiceProvider();
            var claims = Claims(actor, user.Role, sid).ToList();
            if (identity == "delegated") claims.AddRange([
                new Claim("impersonator_id", delegator.ToString()), new Claim("impersonator_name", "delegator"),
                new Claim(CookieSessionValidator.DelegatingCredentialStampClaim, CookieSessionValidator.ComputeStamp("original")),
                new Claim(CookieSessionValidator.DelegatingSessionIdClaim, sourceSid)]);
            var audit = Fake<IAuditService>((method, args) =>
            {
                if (method.Name is not (nameof(IAuditService.WriteAsync) or nameof(IAuditService.WriteForSubscriptionAsync))) throw new NotSupportedException(method.Name);
                Audits.Add((string)args![0]!); return Task.CompletedTask;
            });
            controller = new FilesController(Files, proxy, audit, new ConcurrentWorkGate())
            { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { RequestServices = services,
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, identity == "token" ? PanelTokenAuthenticationHandler.SchemeName : CookieAuthenticationDefaults.AuthenticationScheme)) } } };
        }
        public static async Task<Fixture> Create(string route, string identity = "cookie")
        {
            var f = new Fixture(route, identity);
            await f.scopes.SetScopeAsync(f.owner, [f.SourceName, f.DestinationName], 0, 0, f.Token);
            if (identity != "administrator") await f.scopes.SetParentSubscriptionAsync(f.actor, f.owner, f.Token);
            await f.scopes.SetSubUserSectionsAsync(f.actor, ["files"], f.Token);
            return f;
        }
        private static PanelUser User(Guid id, string name, UserRole role = UserRole.Customer) => new()
        { Id = id, UserName = name, Email = name + "@test.invalid", Role = role, IsActive = true, PasswordHash = "original" };
        private static Claim[] Claims(Guid id, UserRole role, string session) =>
        [new(ClaimTypes.NameIdentifier, id.ToString()), new(ClaimTypes.Role, role.ToString()),
            new(CookieSessionValidator.CredentialStampClaim, CookieSessionValidator.ComputeStamp("original")), new(SessionRevocationStore.SessionIdClaim, session)];
        private SiteDto Site(bool destination) => new() { Name = destination ? DestinationName : SourceName,
            Id = destination ? destinationId : sourceId, PhysicalPath = destination ? resolvedDestination : resolvedSource,
            ApplicationRootPath = destination ? resolvedDestination : resolvedSource, ApplicationPoolName = destination ? DestinationName : SourceName };
        private async Task<SiteDto> ReadSite(string name)
        { var destination = name == DestinationName; var snapshot = Site(destination); await Boundary(destination ? "destination" : "site"); return snapshot; }
        private async Task<IReadOnlyCollection<string>> ReadScope(Guid user, CancellationToken token)
        { var snapshot = await scopes.GetAssignedSitesAsync(user, token); await Boundary("scope"); return snapshot; }
        private async Task Boundary(string boundary)
        {
            if (Pause != boundary || Interlocked.Exchange(ref paused, 1) != 0) return;
            Entered.TrySetResult(); await Release.Task.WaitAsync(Token);
        }
        public async Task WaitForBoundary(Task pending) => Assert.Same(Entered.Task, await Task.WhenAny(Entered.Task, pending).WaitAsync(Token));
        public async Task<object> Run(CancellationToken? cancellation = null)
        {
            var token = cancellation ?? Token;
            object response;
            if (route == "extract") response = Assert.IsType<ApiResponse<FileWorkspaceDto>>(Assert.IsType<OkObjectResult>((await controller.ExtractAsync(ArchiveRequest, token)).Result).Value).Data!;
            else if (route == "search") response = Assert.IsType<ApiResponse<FileSearchResultDto>>(Assert.IsType<OkObjectResult>((await controller.SearchAsync(SearchRequest, token)).Result).Value).Data!;
            else response = Assert.IsType<ApiResponse<DirectorySizeDto>>(Assert.IsType<OkObjectResult>((await controller.CalculateSizeAsync(SizeRequest, token)).Result).Value).Data!;
            Published = true; return response;
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
                case "scope": await scopes.SetScopeAsync(owner, [], 0, 0, Token); break;
                case "section": await scopes.SetSubUserSectionsAsync(actor, [], Token); break;
                case "parent": await scopes.SetParentSubscriptionAsync(actor, other, Token); break;
                case "suspended": await scopes.SuspendAsync(owner, "manual", Token); break;
                default: throw new NotSupportedException(change);
            }
        }
        public Task RevokeDelegator() => sessions.RevokeAsync(sourceSid, Token);
        public void ReplacePrincipal() => controller.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
            Claims(other, UserRole.Administrator, SessionRevocationStore.NewSessionId()), CookieAuthenticationDefaults.AuthenticationScheme));
        public Task ReplaceSite(bool destination, string change) => SiteMutationGuard.RunMutationAsync(gates,
            destination ? DestinationName : SourceName, SiteMutationGuard.MutationKind.IdentityChange, () =>
            {
                if (destination) { if (change == "id") destinationId++; if (change == "path") resolvedDestination = foreignDestination; }
                else { if (change == "id") sourceId++; if (change == "path") resolvedSource = foreignSource; }
                return Task.FromResult(true);
            }, Token);
        public void AssertNoPublicationOrWrite()
        {
            Assert.False(Published); Assert.Empty(Audits);
            Assert.False(Directory.Exists(Output));
            Assert.False(Directory.Exists(Path.Combine(foreignDestination, "output")));
            Assert.Equal("MATCH ORIGINAL", File.ReadAllText(Path.Combine(SourceRoot, "alpha.txt")));
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
