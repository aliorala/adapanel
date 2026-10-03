using System.Reflection;
using System.Security.Claims;
using AdaPanel.Api.Controllers;
using AdaPanel.Api.Security;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Databases;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Entities;
using AdaPanel.Core.Interfaces;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Backup;
using AdaPanel.Infrastructure.Database;
using AdaPanel.Infrastructure.Iis;
using AdaPanel.Infrastructure.Monitoring;
using AdaPanel.Infrastructure.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Ownership = (string Engine, string DatabaseName, System.Guid OwnerUserId, string SiteName);

namespace AdaPanel.Infrastructure.Tests;

// No database engines, IIS or external services: only metadata, in-memory SQLite and strict interface fakes.
public sealed class PostgresDatabaseIdentityConsumersTests
{
    private static readonly Guid Owner = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid Other = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

    [Theory]
    [InlineData("postgresql", "foo", true)]
    [InlineData("postgres", "foo", true)]
    [InlineData("postgresql", "Foo", false)]
    [InlineData("postgres", "Foo", false)]
    public async Task Scope_DistinguishesPostgresCaseAndAcceptsEngineAlias(string engine, string name, bool allowed)
    {
        var scope = await Scope([("postgresql", "foo", Owner, "")]);
        Assert.Equal(allowed, scope.Icerir(engine, name));
    }

    [Theory]
    [InlineData("postgresql", "foo", true)]
    [InlineData("postgresql", "Foo", false)]
    [InlineData("mysql", "FOO", true)]
    public async Task Scope_UnownedLegacySiteFallbackUsesEngineIdentity(string engine, string name, bool allowed)
    {
        var scope = await Scope([], ["foo"]);
        Assert.Equal(allowed, scope.Icerir(engine, name));
    }

    [Theory]
    [InlineData("postgresql", "foo", "postgresql", "foo", false)]
    [InlineData("postgres", "foo", "postgresql", "foo", false)]
    [InlineData("mysql", "FOO", "mysql", "foo", false)]
    [InlineData("postgresql", "Foo", "postgresql", "foo", true)]
    [InlineData("mysql", "foo", "postgresql", "foo", true)]
    public async Task Scope_ExplicitForeignOwnershipOverridesSiteNameFallback(
        string foreignEngine, string foreignName, string requestedEngine, string requestedName, bool allowed)
    {
        var scope = await Scope([(foreignEngine, foreignName, Other, "other-site")], ["foo"]);
        Assert.Equal(allowed, scope.Icerir(requestedEngine, requestedName));
    }

    [Fact]
    public async Task Scope_ResellerRetainsOwnedCustomerAccess()
    {
        var scope = await CustomerAccess.ResolveDatabaseScopeAsync(Scopes(["unrelated"]),
            OwnershipService([("postgres", "Foo", Other, "other-site")]), User("Reseller"), default);
        Assert.NotNull(scope);
        Assert.True(scope.Icerir("postgresql", "Foo"));
        Assert.False(scope.Icerir("postgresql", "foo"));
    }

    [Fact]
    public async Task Scope_AdministratorDoesNotReadOwnership()
    {
        var scope = await CustomerAccess.ResolveDatabaseScopeAsync(Scopes([]),
            Fake<IDatabaseOwnershipService>((_, _) => throw new InvalidOperationException("Unexpected metadata read")),
            User("Administrator"), default);
        Assert.Null(scope);
    }

    [Fact]
    public async Task Scope_FailedOwnershipReadCannotEnableLegacyFallback()
    {
        var ownership = Fake<IDatabaseOwnershipService>((method, _) => method.Name switch
        {
            "GetOwnedAsync" => Task.FromResult<IReadOnlyCollection<(string, string)>>([]),
            "GetAllAsync" => Task.FromException<IReadOnlyCollection<Ownership>>(new IOException("unreadable metadata")),
            _ => throw new NotSupportedException(method.Name)
        });
        await Assert.ThrowsAsync<IOException>(() => CustomerAccess.ResolveDatabaseScopeAsync(Scopes(["foo"]), ownership, User(), default));
    }

    [Fact]
    public void Cascade_KeepsBothPostgresNamesAndDeduplicatesCanonicalAlias()
    {
        var result = SiteCascadeDeletionService.SelectDeletableDatabases("site",
            [Link("postgres", "foo", "site"), Link("postgresql", "Foo", "site")],
            [("postgresql", "foo", Owner, "site")]);
        Assert.Equal(["postgresql:foo", "postgresql:Foo"], result);
    }

    [Theory]
    [InlineData("postgresql", "Foo", 0)]
    [InlineData("postgresql", "foo", 1)]
    [InlineData("mysql", "FOO", 1)]
    public void Cascade_ProbableNameMatchingRespectsEngine(string engine, string name, int count)
    {
        Assert.Equal(count, SiteCascadeDeletionService.SelectProbableDatabases("foo", [(engine, name)], []).Count);
    }

    [Fact]
    public void Cascade_ProbableUppercaseIsNotHiddenByDifferentDeletedDatabase()
    {
        var result = SiteCascadeDeletionService.SelectProbableDatabases("Foo", [("postgresql", "Foo")], ["postgresql:foo"]);
        Assert.Equal(["postgresql:Foo"], result);
    }

    [Fact]
    public void Cascade_ProbableCanonicalAliasIsAlreadyOwned()
    {
        Assert.Empty(SiteCascadeDeletionService.SelectProbableDatabases("foo", [("postgres", "foo")], ["postgresql:foo"]));
    }

    [Fact]
    public async Task Cascade_PreservesOwnerOfSurvivingCaseDistinctDatabase()
    {
        var recorded = new List<(string Engine, string Name, Guid Owner, string? Site)>();
        var ownership = Fake<IDatabaseOwnershipService>((method, args) =>
        {
            if (method.Name == "GetAllAsync")
                return Task.FromResult<IReadOnlyCollection<Ownership>>([("postgresql", "Foo", Other, "site")]);
            Assert.Equal("RecordAsync", method.Name);
            recorded.Add(((string)args[0]!, (string)args[1]!, (Guid)args[2]!, (string?)args[3]));
            return Task.CompletedTask;
        });
        var service = new SiteCascadeDeletionService(null!, null!, null!, null!, null!, null!, ownership,
            null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, NullLogger<SiteCascadeDeletionService>.Instance, new ConcurrencyLock());
        var result = new SiteDeletionResultDto();
        await InvokeTask(service, "DetachSurvivingDatabasesAsync", result, "site",
            new (string, string, Guid)[] { ("postgresql", "foo", Owner), ("postgresql", "Foo", Other) },
            new[] { "postgresql:foo" }, true, CancellationToken.None);
        Assert.Equal(("postgresql", "Foo", Other, null), Assert.Single(recorded));
    }

    [Theory]
    [InlineData("postgresql", "Foo", "upper-site")]
    [InlineData("postgres", "Foo", "upper-site")]
    [InlineData("postgresql", "FOO", "")]
    [InlineData("mysql", "FOO", "mysql-site")]
    public async Task Backup_ResolvesSiteByExactDatabaseIdentity(string engine, string name, string expected)
    {
        var ownership = OwnershipService([
            ("postgresql", "foo", Owner, "lower-site"),
            ("postgresql", "Foo", Other, "upper-site"),
            ("mysql", "foo", Owner, "mysql-site")]);
        var service = new BackupService(null!, null!, null!, null!, null!, null!, null!, databaseOwnership: ownership);
        Assert.Equal(expected, await Invoke<string>(service, "ResolveDatabaseSiteAsync", engine, name, CancellationToken.None));
    }

    [Fact]
    public void SubscriptionBackup_IncludesBothPostgresNamesOncePerCanonicalIdentity()
    {
        var result = SubscriptionBackupPlanner.BuildPlan(Owner, ["site"], [],
            [("postgresql", "foo", Owner, "site"), ("postgresql", "Foo", Owner, "site"), ("postgres", "foo", Owner, "site")], []);
        Assert.Equal(2, result.Databases.Count);
        Assert.Contains(result.Databases, item => item.DatabaseName == "foo" && item.Engine == "postgresql");
        Assert.Contains(result.Databases, item => item.DatabaseName == "Foo" && item.Engine == "postgresql");
    }

    [Fact]
    public async Task ResourceAttribution_SiteMapPreservesCaseAndAlias()
    {
        var service = new ResourceAttributionService(null!, null!, Links([
            Link("postgresql", "foo", "lower-site"), Link("postgres", "Foo", "upper-site")]),
            OwnershipService([]), NullLogger<ResourceAttributionService>.Instance);
        var map = await Invoke<IReadOnlyDictionary<string, string>>(service, "BuildSiteMapAsync", CancellationToken.None);
        Assert.Equal(2, map.Count);
        Assert.Equal("lower-site", ResourceAttributionService.ResolveSiteForDatabase(map, "postgres", "foo"));
        Assert.Equal("upper-site", ResourceAttributionService.ResolveSiteForDatabase(map, "postgresql", "Foo"));
        Assert.Null(ResourceAttributionService.ResolveSiteForDatabase(map, "postgresql", "FOO"));
    }

    [Fact]
    public async Task ResourceAttribution_OwnerMapPreservesBothDatabaseOwners()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new AdaPanelDbContext(new DbContextOptionsBuilder<AdaPanelDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        db.Users.AddRange(new PanelUser { Id = Owner, UserName = "lower", Email = "lower@test" },
            new PanelUser { Id = Other, UserName = "upper", Email = "upper@test" });
        await db.SaveChangesAsync();
        var service = new ResourceAttributionService(db, null!, Links([]),
            OwnershipService([("postgresql", "foo", Owner, ""), ("postgresql", "Foo", Other, "")]),
            NullLogger<ResourceAttributionService>.Instance);
        var map = await Invoke<IReadOnlyDictionary<string, string>>(service, "BuildOwnerMapAsync", CancellationToken.None);
        Assert.Equal(2, map.Count);
        Assert.Equal("lower", ResourceAttributionService.ResolveOwnerForDatabase(map, "postgres", "foo"));
        Assert.Equal("upper", ResourceAttributionService.ResolveOwnerForDatabase(map, "postgresql", "Foo"));
    }

    [Theory]
    [InlineData("postgresql", "Foo", "upper-site")]
    [InlineData("postgres", "Foo", "upper-site")]
    [InlineData("postgresql", "FOO", "")]
    [InlineData("mysql", "FOO", "mysql-site")]
    public async Task DatabaseDetail_LinkLookupRespectsEngineIdentity(string engine, string name, string expected)
    {
        var service = new DatabaseService(null!, null!, null!, Links([
            Link("postgresql", "foo", "lower-site"), Link("postgresql", "Foo", "upper-site"), Link("mysql", "foo", "mysql-site")]), []);
        Assert.Equal(expected, await Invoke<string>(service, "ResolveLinkedSiteAsync", engine, name, CancellationToken.None));
    }

    [Fact]
    public async Task SiteDatabaseList_ExplicitLinkDoesNotAlsoLinkOtherCase()
    {
        var result = await SiteDatabases("site", [Link("postgres", "foo", "site")],
            [("postgresql", "foo", Owner, "site"), ("postgresql", "Foo", Owner, "")], ["foo", "Foo"]);
        Assert.Equal("foo", Assert.Single(result.Linked).Name);
        Assert.Equal("Foo", Assert.Single(result.Candidates).Name);
    }

    [Fact]
    public async Task SiteDatabaseList_NameFallbackHidesExplicitForeignOwner()
    {
        var result = await SiteDatabases("foo", [], [("postgresql", "foo", Other, "other-site")], ["foo"]);
        Assert.Empty(result.Linked);
        Assert.Empty(result.Candidates);
    }

    [Fact]
    public async Task SiteDatabaseList_UnownedLegacyExactNameStaysVisible()
    {
        var result = await SiteDatabases("foo", [], [], ["foo", "Foo"]);
        Assert.Equal("foo", Assert.Single(result.Linked).Name);
        Assert.Empty(result.Candidates);
    }

    private static async Task<SiteDatabasesDto> SiteDatabases(string site, SiteDatabaseLinkDto[] links, Ownership[] ownership, string[] names)
    {
        var controller = new SitesController(null!, null!, null!, null!, Scopes([site]), OwnershipService(ownership), Links(links),
            null!, null!, null!, null!, null!, NullLogger<SitesController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = User() } }
        };
        var databases = Fake<IDatabaseService>((method, _) => method.Name switch
        {
            "GetEnginesAsync" => Task.FromResult<IReadOnlyCollection<DatabaseEngineStatusDto>>([new() { Engine = "postgresql", Installed = true }]),
            "GetWorkspaceAsync" => Task.FromResult(new DatabaseWorkspaceDto { Databases = names.Select(name => new DatabaseRecordDto { Name = name }).ToArray() }),
            _ => throw new NotSupportedException(method.Name)
        });
        var action = await controller.GetSiteDatabasesAsync(site, databases, default);
        return Assert.IsType<ApiResponse<SiteDatabasesDto>>(Assert.IsType<OkObjectResult>(action.Result).Value).Data!;
    }

    private static async Task<TenantDatabaseScope> Scope(Ownership[] rows, string[]? sites = null) =>
        (await CustomerAccess.ResolveDatabaseScopeAsync(Scopes(sites ?? ["unrelated"]), OwnershipService(rows), User(), default))!;

    private static ClaimsPrincipal User(string role = "Customer") => new(new ClaimsIdentity([
        new Claim(ClaimTypes.NameIdentifier, Owner.ToString()), new Claim(ClaimTypes.Role, role)], "test"));

    private static ICustomerScopeService Scopes(string[] sites) => Fake<ICustomerScopeService>((method, _) => method.Name switch
    {
        "GetAssignedSitesAsync" or "GetResellerSitesAsync" => Task.FromResult<IReadOnlyCollection<string>>(sites),
        "GetParentSubscriptionAsync" => Task.FromResult<Guid?>(null),
        "GetOwnedUserIdsAsync" => Task.FromResult<IReadOnlyCollection<Guid>>([Other]),
        _ => throw new NotSupportedException(method.Name)
    });

    private static IDatabaseOwnershipService OwnershipService(Ownership[] rows) => Fake<IDatabaseOwnershipService>((method, args) => method.Name switch
    {
        "GetAllAsync" => Task.FromResult<IReadOnlyCollection<Ownership>>(rows),
        "GetOwnedAsync" => Task.FromResult<IReadOnlyCollection<(string, string)>>(rows.Where(row => row.OwnerUserId == (Guid)args[0]!).Select(row => (row.Engine, row.DatabaseName)).ToArray()),
        _ => throw new NotSupportedException(method.Name)
    });

    private static SiteDatabaseLinkDto Link(string engine, string name, string site) => new() { Engine = engine, DatabaseName = name, SiteName = site };

    private static ISiteDatabaseLinkService Links(SiteDatabaseLinkDto[] rows) => Fake<ISiteDatabaseLinkService>((method, args) => method.Name switch
    {
        "GetAllAsync" => Task.FromResult<IReadOnlyList<SiteDatabaseLinkDto>>(rows),
        "GetForSiteAsync" => Task.FromResult<IReadOnlyList<SiteDatabaseLinkDto>>(rows.Where(row => row.SiteName == (string)args[0]!).ToArray()),
        _ => throw new NotSupportedException(method.Name)
    });

    private static Task<T> Invoke<T>(object service, string method, params object[] args) =>
        (Task<T>)service.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(service, args)!;

    private static Task InvokeTask(object service, string method, params object[] args) =>
        (Task)service.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(service, args)!;

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
