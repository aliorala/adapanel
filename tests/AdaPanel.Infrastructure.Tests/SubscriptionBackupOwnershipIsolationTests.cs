using System.Security.Claims;
using AdaPanel.Api.Security;
using AdaPanel.Core.Common;
using AdaPanel.Infrastructure.Backup;
using Xunit;
using F = AdaPanel.Infrastructure.Tests.SubscriptionBackupMeasurementFixture;

namespace AdaPanel.Infrastructure.Tests;

public sealed class SubscriptionBackupOwnershipIsolationTests
{
    [Theory]
    [InlineData("mysql", "own", true, "owned.example.test", true)]
    [InlineData("mysql", "foreign", false, "owned.example.test", false)]
    [InlineData("postgres", "Foo", false, " OWNED.EXAMPLE.TEST ", false)]
    [InlineData("postgresql", "foo", false, "owned.example.test", false)]
    [InlineData("sqlserver", "Foreign", false, "owned.example.test", false)]
    [InlineData("mysql", "own", true, "foreign.example.test", false)]
    [InlineData("postgresql", "Foo", true, "foreign.example.test", false)]
    [InlineData("mysql", "own", true, "", true)]
    [InlineData("mysql", "foreign", false, "", false)]
    [InlineData("mysql", "foreign", false, "foreign.example.test", false)]
    public void Plan_RequiresExplicitOwnerAndCompatibleSite(string engine, string name, bool owned, string site, bool included)
    {
        var plan = SubscriptionBackupPlanner.BuildPlan(F.Owner, [F.Site], [], [(engine, name, owned ? F.Owner : F.Other, site)], []);
        Assert.Equal(included ? 1 : 0, plan.Databases.Count);
        if (included) Assert.Equal(DatabaseResourceIdentity.NormalizeEngine(engine), Assert.Single(plan.Databases).Engine);
    }

    [Theory]
    [InlineData("postgres", "postgresql")]
    [InlineData("postgresql", "postgres")]
    public void Plan_ForeignPgCaseSiblingCannotEnterThroughOwnedSite(string ownEngine, string foreignEngine)
    {
        var plan = SubscriptionBackupPlanner.BuildPlan(F.Owner, [F.Site], [],
            [(ownEngine, "foo", F.Owner, F.Site), (foreignEngine, "Foo", F.Other, F.Site)], []);
        var database = Assert.Single(plan.Databases);
        Assert.Equal("postgresql", database.Engine);
        Assert.Equal("foo", database.DatabaseName);
    }

    [Fact]
    public void Plan_OwnPgCaseDistinctDatabasesSurviveWhileAliasDuplicatesCollapse()
    {
        var plan = SubscriptionBackupPlanner.BuildPlan(F.Owner, [F.Site], [],
            [("postgres", "foo", F.Owner, F.Site), ("postgresql", "foo", F.Owner, F.Site),
             ("postgresql", "Foo", F.Owner, F.Site)], []);
        Assert.Equal(["Foo", "foo"], plan.Databases.Select(db => db.DatabaseName).Order(StringComparer.Ordinal));
        Assert.All(plan.Databases, db => Assert.Equal("postgresql", db.Engine));
    }

    [Theory]
    [InlineData("mysql", "owned")]
    [InlineData("postgres", "Foo")]
    public async Task ScopeReassignmentDoesNotTransferDatabaseOwnershipOrItsDump(string engine, string database)
    {
        using var fixture = new F();
        await fixture.Scopes.SetScopeAsync(F.Owner, [], 0, 0);
        await fixture.Scopes.SetScopeAsync(F.Other, [F.Site], 0, 0);
        await fixture.Ownership.RecordAsync(engine, database, F.Other, F.Site);
        // This is the same scope-only write used by UserManagementService.ApplyScopeAsync.
        await fixture.Scopes.SetScopeAsync(F.Other, [], 0, 0);
        await fixture.Scopes.SetScopeAsync(F.Owner, [F.Site], 0, 0);
        Assert.Equal(F.Other, Assert.Single(await fixture.Ownership.GetAllAsync()).OwnerUserId);
        var actor = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, F.Owner.ToString()), new Claim(ClaimTypes.Role, "Customer")], "test"));
        var access = await CustomerAccess.ResolveDatabaseScopeAsync(fixture.Scopes, fixture.Ownership, actor, default);
        Assert.NotNull(access);
        Assert.False(access.Icerir(engine, database));

        var record = await fixture.Service.BackupSubscriptionAsync(fixture.Request());

        Assert.Empty(fixture.Exported);
        Assert.Empty(record.IncludedDatabases!);
        Assert.Empty(Assert.Single(await fixture.Persisted()).IncludedDatabases!);
        Assert.DoesNotContain(F.Entries(record), entry => entry.StartsWith("databases/", StringComparison.Ordinal));
        Assert.Contains($"sites/{F.Site}/files/index.html", F.Entries(record));
        using var manifest = F.ReadEntry(record, "subscription.json");
        Assert.Empty(manifest.RootElement.GetProperty("databases").EnumerateArray());
        Assert.Equal(F.Owner.ToString("N"), record.SubscriptionUserId);
    }

    [Theory]
    [InlineData("postgres", "postgresql")]
    [InlineData("postgresql", "postgres")]
    public async Task Archive_ExportsOnlyOwnPgCaseSiblingAndPersistsExactCoverage(string ownEngine, string foreignEngine)
    {
        using var fixture = new F();
        await fixture.Ownership.RecordAsync(ownEngine, "foo", F.Owner, F.Site);
        await fixture.Ownership.RecordAsync(foreignEngine, "Foo", F.Other, F.Site);
        var record = await fixture.Service.BackupSubscriptionAsync(fixture.Request());
        Assert.Equal([("postgresql", "foo")], fixture.Exported);
        Assert.Equal("foo", Assert.Single(record.IncludedDatabases!).DatabaseName);
        Assert.Equal("foo", Assert.Single(Assert.Single(await fixture.Persisted()).IncludedDatabases!).DatabaseName);
        Assert.Contains("databases/postgresql-foo.sql", F.Entries(record));
        Assert.DoesNotContain("databases/postgresql-Foo.sql", F.Entries(record));
        using var manifest = F.ReadEntry(record, "subscription.json");
        Assert.Equal("postgresql:foo", Assert.Single(manifest.RootElement.GetProperty("databases").EnumerateArray()).GetString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("owned.example.test")]
    [InlineData("foreign.example.test")]
    public async Task Archive_OwnOwnerStillRequiresCompatibleSite(string site)
    {
        using var fixture = new F();
        await fixture.Ownership.RecordAsync("mysql", "own_database", F.Owner, site);
        var record = await fixture.Service.BackupSubscriptionAsync(fixture.Request());
        var allowed = site != F.ForeignSite;
        Assert.Equal(allowed ? 1 : 0, fixture.Exported.Count);
        Assert.Equal(allowed ? 1 : 0, record.IncludedDatabases!.Count);
        Assert.Equal("created", record.Status);
        Assert.Equal(allowed, F.Entries(record).Contains("databases/mysql-own_database.sql"));
    }
}
