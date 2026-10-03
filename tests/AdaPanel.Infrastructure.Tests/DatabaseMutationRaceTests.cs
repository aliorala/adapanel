using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Claims;
using AdaPanel.Api.Controllers;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Databases;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Components;
using AdaPanel.Core.Interfaces;
using AdaPanel.Core.Entities;
using AdaPanel.Core.Enums;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Database;
using AdaPanel.Infrastructure.Iis;
using AdaPanel.Infrastructure.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Ownership = (string Engine, string DatabaseName, System.Guid OwnerUserId, string SiteName);

namespace AdaPanel.Infrastructure.Tests;

// Controllers are real; the provider contract fake holds exactly the same public lifecycle lease
// as DatabaseService. No database server, IIS, scheduled task or process is started.
public sealed class DatabaseMutationRaceTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ParallelCreatesCannotExceedSharedSubscriptionQuota(bool useSubUsers)
    {
        using var fixture = new Fixture();
        var owner = Guid.NewGuid();
        var firstActor = useSubUsers ? Guid.NewGuid() : owner;
        var secondActor = useSubUsers ? Guid.NewGuid() : owner;
        if (useSubUsers)
        {
            fixture.Parents[firstActor] = owner;
            fixture.Parents[secondActor] = owner;
        }
        var entered = Signal();
        var release = Signal();
        fixture.BeforeCreate = async request =>
        {
            if (request.Name != fixture.Name + "first") return;
            entered.TrySetResult();
            await release.Task.WaitAsync(Timeout);
        };
        var first = fixture.Controller(firstActor).CreateAsync(fixture.Request("first"), default);
        await entered.Task.WaitAsync(Timeout);
        var second = Capture(() => fixture.Controller(secondActor).CreateAsync(fixture.Request("second"), default));
        release.TrySetResult();
        await first.WaitAsync(Timeout);
        var error = await second.WaitAsync(Timeout);
        Assert.Equal("database.quota_exceeded", Assert.IsType<AppException>(error).MessageKey);
        Assert.Single(fixture.Databases);
        Assert.Single(fixture.Rows);
    }

    [Fact]
    public async Task IndependentSubscriptionsDoNotShareQuotaGate()
    {
        using var fixture = new Fixture();
        var entered = Signal();
        var release = Signal();
        fixture.BeforeCreate = async request =>
        {
            if (request.Name != fixture.Name + "first") return;
            entered.TrySetResult();
            await release.Task.WaitAsync(Timeout);
        };
        var first = fixture.Controller(Guid.NewGuid()).CreateAsync(fixture.Request("first"), default);
        await entered.Task.WaitAsync(Timeout);
        try
        {
            await fixture.Controller(Guid.NewGuid()).CreateAsync(fixture.Request("second"), default).WaitAsync(Timeout);
        }
        finally { release.TrySetResult(); }
        await first.WaitAsync(Timeout);
        Assert.Equal(2, fixture.Databases.Count);
    }

    [Theory]
    [InlineData("postgresql")]
    [InlineData("postgres")]
    public async Task DeleteRechecksOwnerAfterWaitingForAnotherMutation(string requestedEngine)
    {
        using var fixture = new Fixture();
        var owner = Guid.NewGuid();
        var replacementOwner = Guid.NewGuid();
        fixture.Seed(owner);
        var lease = await DatabaseLifecycle.EnterResourceAsync("postgresql", fixture.Name);
        var deleting = Capture(() => fixture.Controller(owner).DeleteAsync(fixture.Name, requestedEngine, default));
        Assert.False(deleting.IsCompleted);
        fixture.Seed(replacementOwner); // Another operation replaces the resource while holding its lease.
        lease.Dispose();
        var error = await deleting.WaitAsync(Timeout);
        Assert.Equal("database.not_owned", Assert.IsType<AppException>(error).MessageKey);
        Assert.Contains(fixture.Key, fixture.Databases.Keys);
        Assert.Equal(replacementOwner, fixture.Rows[fixture.Key].OwnerUserId);
    }

    [Fact]
    public async Task CreateRetainsResourceLeaseUntilOwnershipCommit()
    {
        using var fixture = new Fixture();
        var entered = Signal();
        var release = Signal();
        fixture.BeforeRecord = async () =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(Timeout);
        };
        var create = fixture.Controller(Guid.NewGuid()).CreateAsync(fixture.Request(), default);
        await entered.Task.WaitAsync(Timeout);
        var delete = fixture.Controller(Guid.NewGuid(), "Administrator").DeleteAsync(fixture.Name, "postgres", default);
        release.TrySetResult();
        await Task.WhenAll(create, delete).WaitAsync(Timeout);
        Assert.Empty(fixture.Databases);
        Assert.Empty(fixture.Rows);
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("site")]
    [InlineData("removed")]
    [InlineData("case")]
    public async Task CascadeDetachCannotRewriteChangedOwnershipSnapshot(string change)
    {
        using var fixture = new Fixture();
        var owner = Guid.NewGuid();
        var replacement = Guid.NewGuid();
        fixture.Rows[fixture.Key] = ("postgresql", fixture.Name, owner, "site");
        if (change == "owner") fixture.Rows[fixture.Key] = ("postgresql", fixture.Name, replacement, "site");
        if (change == "site") fixture.Rows[fixture.Key] = ("postgresql", fixture.Name, owner, "new-site");
        if (change == "removed") fixture.Rows.Clear();
        if (change == "case")
        {
            fixture.Rows.Clear();
            fixture.Rows[DatabaseResourceIdentity.Key("postgresql", fixture.Name.ToUpperInvariant())] =
                ("postgresql", fixture.Name.ToUpperInvariant(), owner, "site");
        }
        var before = fixture.Rows.ToArray();
        var cascade = new SiteCascadeDeletionService(null!, null!, null!, null!, null!, null!, fixture.Ownership(),
            null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, NullLogger<SiteCascadeDeletionService>.Instance, new ConcurrencyLock());
        var method = typeof(SiteCascadeDeletionService).GetMethod("DetachSurvivingDatabasesAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await ((Task)method.Invoke(cascade, [new SiteDeletionResultDto(), "site",
            new (string, string, Guid)[] { ("postgres", fixture.Name, owner) }, Array.Empty<string>(), false, CancellationToken.None])!).WaitAsync(Timeout);
        Assert.Equal(before, fixture.Rows.ToArray());
    }

    [Fact]
    public async Task AdministratorSiteProvisioningPersistsDatabaseSiteLink()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var actors = new AdaPanelDbContext(new DbContextOptionsBuilder<AdaPanelDbContext>().UseSqlite(connection).Options);
        actors.Database.EnsureCreated();
        var actor = new PanelUser { UserName = "site-admin", Email = "site-admin@example.test", PasswordHash = "fixture", Role = UserRole.Administrator, IsActive = true };
        actors.Users.Add(actor);
        actors.SaveChanges();
        const string site = "fixture-site";
        CreateDatabaseRequest? created = null;
        var provider = Fake<IDatabaseService>((method, args) =>
        {
            Assert.Equal("CreateDatabaseAsync", method.Name);
            return Create();
            async Task<DatabaseWorkspaceDto> Create()
            {
                created = (CreateDatabaseRequest)args[0]!;
                if (args.Length == 4) await ((Func<CancellationToken, Task>)args[1]!)(default);
                if (args.Length == 4) await ((Func<CancellationToken, Task>)args[2]!)(default);
                return new DatabaseWorkspaceDto();
            }
        });
        var gates = new ConcurrencyLock();
        SiteDto? currentSite = null;
        var iis = Fake<IIisSiteService>((method, _) => method.Name switch
        {
            "GetSitesAsync" => Task.FromResult<IReadOnlyCollection<SiteDto>>(currentSite is null ? [] : [currentSite]),
            "GetSiteAsync" => Task.FromResult(currentSite ?? throw new AppException("sites.not_found", 404, 404)),
            _ => throw new NotSupportedException(method.Name)
        });
        var controller = new SitesController(iis,
            Fake<ISiteProvisioningService>((_, _) => SiteMutationGuard.RunMutationAsync(gates, site, true,
                () => Task.FromResult(currentSite = new SiteDto { Name = site }), default)),
            null!, null!, Fake<ICustomerScopeService>((_, _) => Task.FromResult<Guid?>(null)),
            null!, null!, null!, null!, Fake<IAuditService>((_, _) => Task.CompletedTask), gates,
            null!, NullLogger<SitesController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity([
                    new Claim(ClaimTypes.NameIdentifier, actor.Id.ToString()), new Claim(ClaimTypes.Role, "Administrator")], "test"))
            } }
        };
        var response = await controller.CreateSiteAsync(new CreateSiteRequest
        {
            Name = site, PhysicalPath = "", ProvisionDefaultServices = false,
            CreateDatabase = true, DatabaseEngine = "postgresql", DatabaseName = "fixture_db"
        }, provider, null!, null!, null!, Fake<IComponentBootstrapper>((_, _) => Task.FromResult(new SoftwareCatalogDto())), actors, default);
        var result = Assert.IsType<ApiResponse<SiteCreationResultDto>>(Assert.IsType<OkObjectResult>(response.Result).Value).Data!;
        Assert.Empty(result.Warnings);
        Assert.Equal("postgresql:fixture_db", Assert.Single(result.Created));
        Assert.NotNull(created);
        Assert.Equal(site, created.SiteName);
        // This is the authority consumed by cascade; an empty site silently orphaned admin-created DBs.
        Assert.Equal(["postgresql:fixture_db"], SiteCascadeDeletionService.SelectDeletableDatabases(site,
            [new SiteDatabaseLinkDto { Engine = created.Engine, DatabaseName = created.Name, SiteName = created.SiteName }], []));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CascadeQueuedDropRechecksOwnerAndSiteUnderResourceLease(bool changeOwner)
    {
        using var fixture = new Fixture();
        var owner = Guid.NewGuid();
        fixture.Seed(owner);
        fixture.Rows[fixture.Key] = ("postgresql", fixture.Name, owner, "site");
        var links = new List<SiteDatabaseLinkDto>();
        var cascade = Cascade(fixture, links);
        var lease = await DatabaseLifecycle.EnterResourceAsync("postgresql", fixture.Name);
        var deleting = Capture(() => cascade.DeleteSiteDatabaseAsync("site", "postgres", fixture.Name, default));
        Assert.False(deleting.IsCompleted);
        fixture.Rows[fixture.Key] = ("postgresql", fixture.Name, changeOwner ? Guid.NewGuid() : owner,
            changeOwner ? "site" : "other-site");
        lease.Dispose();
        Assert.IsType<AppException>(await deleting.WaitAsync(Timeout));
        Assert.Contains(fixture.Key, fixture.Databases.Keys);
        Assert.Single(fixture.Rows);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CascadeRemovesOwnershipOnlyAfterSuccessfulDrop(bool failDrop)
    {
        using var fixture = new Fixture();
        var owner = Guid.NewGuid();
        fixture.Seed(owner);
        fixture.Rows[fixture.Key] = ("postgresql", fixture.Name, owner, "site");
        fixture.BeforeDelete = () => failDrop ? Task.FromException(new IOException("provider unavailable")) : Task.CompletedTask;
        var error = await Capture(() => Cascade(fixture, []).DeleteSiteDatabaseAsync("site", "postgres", fixture.Name, default));
        if (failDrop)
        {
            Assert.IsType<IOException>(error);
            Assert.Single(fixture.Databases);
            Assert.Single(fixture.Rows);
        }
        else
        {
            Assert.Null(error);
            Assert.Empty(fixture.Databases);
            Assert.Empty(fixture.Rows);
        }
    }

    private static SiteCascadeDeletionService Cascade(Fixture fixture, List<SiteDatabaseLinkDto> links) => new(
        null!, null!, null!, null!, fixture.Service(),
        Fake<ISiteDatabaseLinkService>((method, args) => method.Name == "GetForSiteAsync"
            ? Task.FromResult<IReadOnlyList<SiteDatabaseLinkDto>>(links.Where(row => row.SiteName == (string)args[0]!).ToArray())
            : throw new NotSupportedException(method.Name)), fixture.Ownership(),
        null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, NullLogger<SiteCascadeDeletionService>.Instance, new ConcurrencyLock());

    private static async Task<Exception?> Capture(Func<Task> action)
    {
        try { await action(); return null; }
        catch (Exception exception) { return exception; }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly SqliteConnection connection = new("Data Source=:memory:");
        private readonly List<AdaPanelDbContext> contexts = [];
        public Fixture()
        {
            connection.Open();
            Context().Database.EnsureCreated();
        }
        private AdaPanelDbContext Context()
        {
            var context = new AdaPanelDbContext(new DbContextOptionsBuilder<AdaPanelDbContext>().UseSqlite(connection).Options);
            contexts.Add(context);
            return context;
        }
        public string Name { get; } = "race_" + Guid.NewGuid().ToString("N");
        public string Key => DatabaseResourceIdentity.Key("postgresql", Name);
        public ConcurrentDictionary<string, byte> Databases { get; } = new(StringComparer.Ordinal);
        public ConcurrentDictionary<string, Ownership> Rows { get; } = new(StringComparer.Ordinal);
        public Dictionary<Guid, Guid> Parents { get; } = [];
        public Func<CreateDatabaseRequest, Task>? BeforeCreate { get; set; }
        public Func<Task>? BeforeRecord { get; set; }
        public Func<Task>? BeforeDelete { get; set; }
        public IDatabaseOwnershipService Ownership() => Fake<IDatabaseOwnershipService>(OwnershipCall);
        public IDatabaseService Service() => Fake<IDatabaseService>(ServiceCall);

        public CreateDatabaseRequest Request(string suffix = "") => new() { Engine = "postgresql", Name = Name + suffix };

        public void Seed(Guid owner)
        {
            Databases[Key] = 0;
            Rows[Key] = ("postgresql", Name, owner, "");
        }

        public ResellerDatabasesController Controller(Guid actor, string role = "Customer")
        {
            var context = Context();
            foreach (var id in new[] { actor }.Concat(Parents.Values).Distinct())
            {
                if (!context.Users.Any(user => user.Id == id))
                    context.Users.Add(new PanelUser { Id = id, UserName = id.ToString("N"), Email = id.ToString("N") + "@example.test",
                        PasswordHash = "fixture", IsActive = true, Role = id == actor ? Enum.Parse<UserRole>(role) : UserRole.Customer });
            }
            context.SaveChanges();
            return new(
            Fake<IDatabaseService>(ServiceCall), Fake<IDatabaseOwnershipService>(OwnershipCall),
            Fake<ICustomerScopeService>((method, args) => method.Name switch
            {
                "GetParentSubscriptionAsync" => Task.FromResult<Guid?>(Parents.TryGetValue((Guid)args[0]!, out var parent) ? parent : null),
                "GetScopeAsync" => Task.FromResult(new CustomerScope([], 0, 0, 1, 0, 0)),
                "GetSubUserSectionsAsync" => Task.FromResult<IReadOnlyCollection<string>?>(null),
                _ => throw new NotSupportedException(method.Name)
            }), Fake<IAuditService>((_, _) => Task.CompletedTask), context)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity([
                        new Claim(ClaimTypes.NameIdentifier, actor.ToString()), new Claim(ClaimTypes.Role, role)], "test"))
                }
            }
        };
        }

        public void Dispose()
        {
            foreach (var context in contexts) context.Dispose();
            connection.Dispose();
        }

        private object ServiceCall(MethodInfo method, object?[] args) => method.Name switch
        {
            "CreateDatabaseAsync" => Create((CreateDatabaseRequest)args[0]!,
                args.Length == 4 ? (Func<CancellationToken, Task>)args[1]! : null,
                args.Length == 4 ? (Func<CancellationToken, Task>)args[2]! : null, (CancellationToken)args[^1]!),
            "DeleteDatabaseAsync" => Delete((string)args[0]!, (string)args[1]!,
                args.Length == 5 ? (Func<CancellationToken, Task>)args[2]! : null,
                args.Length == 5 ? (Func<CancellationToken, Task>)args[3]! : null, (CancellationToken)args[^1]!),
            _ => throw new NotSupportedException(method.Name)
        };

        private async Task<DatabaseWorkspaceDto> Create(CreateDatabaseRequest request, Func<CancellationToken, Task>? before,
            Func<CancellationToken, Task>? after, CancellationToken cancellationToken)
        {
            using var lease = await DatabaseLifecycle.EnterResourceAsync(request.Engine, request.Name, cancellationToken);
            if (before is not null) await before(cancellationToken);
            if (BeforeCreate is not null) await BeforeCreate(request);
            Databases[DatabaseResourceIdentity.Key(request.Engine, request.Name)] = 0;
            if (after is not null) await after(cancellationToken);
            return new DatabaseWorkspaceDto();
        }

        private async Task Delete(string engine, string name, Func<CancellationToken, Task>? before,
            Func<CancellationToken, Task>? after, CancellationToken cancellationToken)
        {
            using var lease = await DatabaseLifecycle.EnterResourceAsync(engine, name, cancellationToken);
            if (before is not null) await before(cancellationToken);
            if (BeforeDelete is not null) await BeforeDelete();
            Databases.TryRemove(DatabaseResourceIdentity.Key(engine, name), out _);
            if (after is not null) await after(cancellationToken);
        }

        private object OwnershipCall(MethodInfo method, object?[] args)
        {
            switch (method.Name)
            {
                case "GetOwnedAsync":
                    return Task.FromResult<IReadOnlyCollection<(string, string)>>(Rows.Values.Where(row => row.OwnerUserId == (Guid)args[0]!).Select(row => (row.Engine, row.DatabaseName)).ToArray());
                case "GetAllAsync": return Task.FromResult<IReadOnlyCollection<Ownership>>(Rows.Values.ToArray());
                case "CountForOwnerAsync": return Task.FromResult(Rows.Values.Count(row => row.OwnerUserId == (Guid)args[0]!));
                case "GetOwnerAsync":
                    return Task.FromResult<Guid?>(Rows.TryGetValue(DatabaseResourceIdentity.Key((string)args[0]!, (string)args[1]!), out var row) ? row.OwnerUserId : null);
                case "RecordAsync": return Record(args);
                case "RemoveAsync":
                    Rows.TryRemove(DatabaseResourceIdentity.Key((string)args[0]!, (string)args[1]!), out _);
                    return Task.CompletedTask;
                default: throw new NotSupportedException(method.Name);
            }
        }

        private async Task Record(object?[] args)
        {
            if (BeforeRecord is not null) await BeforeRecord();
            var engine = (string)args[0]!;
            var name = (string)args[1]!;
            Rows[DatabaseResourceIdentity.Key(engine, name)] = (engine, name, (Guid)args[2]!, (string?)args[3] ?? "");
        }
    }

    private static T Fake<T>(Func<MethodInfo, object?[], object?> handler) where T : class
    {
        var proxy = DispatchProxy.Create<T, StrictProxy>();
        ((StrictProxy)(object)proxy).Handler = handler;
        return proxy;
    }

    public class StrictProxy : DispatchProxy
    {
        public Func<MethodInfo, object?[], object?> Handler { get; set; } = null!;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handler(targetMethod!, args ?? []);
    }
}
