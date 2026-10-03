using System.Text.Json;
using System.Security.Claims;
using AdaPanel.Api.Security;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Databases;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Database;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Security;
using AdaPanel.Infrastructure.Support;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class DatabaseIdentityStoreTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "AdaPanel.DatabaseIdentity." + Guid.NewGuid().ToString("N"));
    private readonly JsonFileStore files;
    private readonly SiteDatabaseLinkService links;
    private readonly DatabaseCredentialStore credentials;
    private readonly DatabaseOwnershipService ownership;

    public DatabaseIdentityStoreTests()
    {
        files = new JsonFileStore(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = root }));
        links = new SiteDatabaseLinkService(files);
        credentials = new DatabaseCredentialStore(files, new Protector());
        ownership = new DatabaseOwnershipService(files);
    }

    [Fact]
    public async Task PostgresOwnersAndListedNamesRemainIndependent()
    {
        var lower = Guid.NewGuid();
        var upper = Guid.NewGuid();
        await ownership.RecordAsync("postgresql", "foo", lower, "lower.example");
        await ownership.RecordAsync("postgresql", "Foo", upper, "upper.example");

        Assert.Equal(lower, await ownership.GetOwnerAsync("postgresql", "foo"));
        Assert.Equal(upper, await ownership.GetOwnerAsync("postgresql", "Foo"));
        Assert.Equal(("postgresql", "foo"), Assert.Single(await ownership.GetOwnedAsync(lower)));
        Assert.Equal(("postgresql", "Foo"), Assert.Single(await ownership.GetOwnedAsync(upper)));
        Assert.Equal(1, await ownership.CountForOwnerAsync(lower));
        Assert.Equal(1, await ownership.CountForOwnerAsync(upper));
        Assert.Equal(2, (await ownership.GetAllAsync()).Count);
    }

    [Fact]
    public async Task RemovingPostgresOwnerDoesNotRemoveCaseSibling()
    {
        var lower = Guid.NewGuid();
        var upper = Guid.NewGuid();
        await ownership.RecordAsync("postgresql", "foo", lower, null);
        await ownership.RecordAsync("postgresql", "Foo", upper, null);
        await ownership.RemoveAsync("postgresql", "Foo");

        Assert.Null(await ownership.GetOwnerAsync("postgresql", "Foo"));
        Assert.Equal(lower, await ownership.GetOwnerAsync("postgresql", "foo"));
    }

    [Fact]
    public async Task PostgresOwnershipAliasesIdentifyTheSameDatabase()
    {
        var owner = Guid.NewGuid();
        await ownership.RecordAsync(" POSTGRES ", " Foo ", owner, "site");
        Assert.Equal(owner, await ownership.GetOwnerAsync("postgresql", "Foo"));
        Assert.Equal(("postgresql", "Foo"), Assert.Single(await ownership.GetOwnedAsync(owner)));
        await ownership.RemoveAsync("postgresql", "Foo");
        Assert.Empty(await ownership.GetOwnedAsync(owner));
    }

    [Theory]
    [InlineData("mysql")]
    [InlineData("mariadb")]
    [InlineData("mssql")]
    public async Task ExistingWindowsOwnershipCaseSemanticsRemain(string engine)
    {
        var owner = Guid.NewGuid();
        await ownership.RecordAsync(engine, "Foo", owner, null);
        Assert.Equal(owner, await ownership.GetOwnerAsync(engine, "foo"));
        await ownership.RemoveAsync(engine, "FOO");
        Assert.Empty(await ownership.GetOwnedAsync(owner));
    }

    [Fact]
    public async Task PostgresLinksKeepCaseSiblingSites()
    {
        await links.LinkAsync("postgresql", "foo", "lower.example");
        await links.LinkAsync("postgresql", "Foo", "upper.example");
        Assert.Equal("foo", Assert.Single(await links.GetForSiteAsync("lower.example")).DatabaseName);
        Assert.Equal("Foo", Assert.Single(await links.GetForSiteAsync("upper.example")).DatabaseName);

        await links.UnlinkAsync("postgresql", "Foo");
        Assert.Empty(await links.GetForSiteAsync("upper.example"));
        Assert.Equal("foo", Assert.Single(await links.GetAllAsync()).DatabaseName);
    }

    [Fact]
    public async Task PostgresLinkAliasUpdatesInsteadOfDuplicating()
    {
        await links.LinkAsync("postgres", "Foo", "old.example");
        await links.LinkAsync("postgresql", "Foo", "new.example");
        var link = Assert.Single(await links.GetAllAsync());
        Assert.Equal("postgresql", link.Engine);
        Assert.Equal("new.example", link.SiteName);
        await links.UnlinkAsync("postgres", "Foo");
        Assert.Empty(await links.GetAllAsync());
    }

    [Theory]
    [InlineData("mysql")]
    [InlineData("mariadb")]
    public async Task WindowsDatabaseLinksRemainCaseInsensitive(string engine)
    {
        await links.LinkAsync(engine, "Foo", "old.example");
        await links.LinkAsync(engine, "foo", "new.example");
        Assert.Equal("new.example", Assert.Single(await links.GetAllAsync()).SiteName);
        await links.UnlinkAsync(engine, "FOO");
        Assert.Empty(await links.GetAllAsync());
    }

    [Fact]
    public async Task PostgresCredentialsResolveExactDatabaseAndRemovalKeepsSibling()
    {
        await credentials.UpsertAsync("postgresql", "lower_user", "localhost", "foo", "lower-secret", default);
        await credentials.UpsertAsync("postgresql", "upper_user", "localhost", "Foo", "upper-secret", default);
        Assert.Equal("upper-secret", (await credentials.FindForDatabaseAsync("postgresql", "Foo", default))?.Password);
        Assert.Equal("lower-secret", (await credentials.FindForDatabaseAsync("postgresql", "foo", default))?.Password);

        await credentials.RemoveDatabaseAsync("postgresql", "Foo", default);
        Assert.Null(await credentials.FindForDatabaseAsync("postgresql", "Foo", default));
        Assert.Equal("lower-secret", (await credentials.FindForDatabaseAsync("postgresql", "foo", default))?.Password);
    }

    [Fact]
    public async Task PostgresCredentialUsersKeepQuotedCaseOnUpdateAndRemoval()
    {
        await credentials.UpsertAsync("postgresql", "user", "localhost", "lower", "lower-secret", default);
        await credentials.UpsertAsync("postgresql", "User", "localhost", "upper", "upper-secret", default);
        Assert.Equal("lower-secret", await credentials.FindPasswordAsync("postgresql", "user", default));
        Assert.Equal("upper-secret", await credentials.FindPasswordAsync("postgresql", "User", default));
        await credentials.UpsertAsync("postgresql", "User", "localhost", "", "changed-secret", default);
        Assert.Equal("changed-secret", (await credentials.FindForDatabaseAsync("postgresql", "upper", default))?.Password);
        Assert.Equal("lower-secret", (await credentials.FindForDatabaseAsync("postgresql", "lower", default))?.Password);

        await credentials.RemoveUserAsync("postgresql", "User", "localhost", default);
        Assert.False(await credentials.IsUserReferencedAsync("postgresql", "User", "localhost", default));
        Assert.True(await credentials.IsUserReferencedAsync("postgresql", "user", "localhost", default));
        Assert.Equal("lower-secret", await credentials.FindPasswordAsync("postgresql", "user", default));
    }

    [Fact]
    public async Task PostgresCredentialAliasUpdatesTheExistingAccount()
    {
        await credentials.UpsertAsync("postgres", "User", "localhost", "Foo", "old-secret", default);
        Assert.Equal("old-secret", (await credentials.FindForDatabaseAsync("postgresql", "Foo", default))?.Password);
        await credentials.UpsertAsync("postgresql", "User", "localhost", "", "new-secret", default);
        Assert.Equal("new-secret", (await credentials.FindForDatabaseAsync("postgres", "Foo", default))?.Password);
        Assert.Equal("new-secret", await credentials.FindPasswordAsync("postgres", "User", default));
        Assert.True(await credentials.IsUserReferencedAsync("postgres", "User", "localhost", default));
        Assert.Single(await files.ReadAsync("config/db-credentials.json", Array.Empty<DatabaseCredentialStore.Record>()));
        await credentials.RemoveUserAsync("postgres", "User", "localhost", default);
        Assert.False(await credentials.IsUserReferencedAsync("postgresql", "User", "localhost", default));
    }

    [Theory]
    [InlineData("mysql")]
    [InlineData("mariadb")]
    public async Task WindowsCredentialDatabaseLookupAndRemovalRemainCaseInsensitive(string engine)
    {
        await credentials.UpsertAsync(engine, "owner", "localhost", "Foo", "secret", default);
        Assert.Equal("secret", (await credentials.FindForDatabaseAsync(engine, "foo", default))?.Password);
        await credentials.RemoveDatabaseAsync(engine, "FOO", default);
        Assert.Null(await credentials.FindForDatabaseAsync(engine, "Foo", default));
    }

    [Fact]
    public async Task LegacyPostgresStoredSpellingRemainsExactAndReadingDoesNotRewriteFile()
    {
        var owner = Guid.NewGuid();
        await SeedOwnership(new() { ["postgresql|foo"] = new { ownerId = owner.ToString("N"), siteName = "original.example" } });
        var before = await File.ReadAllBytesAsync(files.ResolvePath("config/db-ownership.json"));

        Assert.Equal(owner, await ownership.GetOwnerAsync("postgresql", "foo"));
        Assert.Null(await ownership.GetOwnerAsync("postgresql", "Foo"));
        Assert.Equal(("postgresql", "foo"), Assert.Single(await ownership.GetOwnedAsync(owner)));
        Assert.Equal(before, await File.ReadAllBytesAsync(files.ResolvePath("config/db-ownership.json")));

        var newOwner = Guid.NewGuid();
        await ownership.RecordAsync("postgresql", "Foo", newOwner, null);
        Assert.Equal(owner, await ownership.GetOwnerAsync("postgresql", "foo"));
        Assert.Equal(newOwner, await ownership.GetOwnerAsync("postgresql", "Foo"));
        Assert.Equal("original.example", (await ownership.GetAllAsync()).Single(item => item.DatabaseName == "foo").SiteName);
    }

    [Fact]
    public async Task LegacyPostgresAliasIsReadableAndCanonicalizedOnlyOnNormalWrite()
    {
        var owner = Guid.NewGuid();
        await SeedOwnership(new() { ["postgres|foo"] = new { ownerId = owner.ToString("N"), siteName = "site" } });
        var before = await File.ReadAllBytesAsync(files.ResolvePath("config/db-ownership.json"));

        Assert.Equal(owner, await ownership.GetOwnerAsync("postgresql", "foo"));
        Assert.Equal(("postgresql", "foo"), Assert.Single(await ownership.GetOwnedAsync(owner)));
        Assert.Equal(before, await File.ReadAllBytesAsync(files.ResolvePath("config/db-ownership.json")));

        await ownership.RecordAsync("postgres", "foo", owner, "updated");
        var state = await files.ReadAsync("config/db-ownership.json", new Dictionary<string, JsonElement>());
        Assert.Equal("postgresql|foo", Assert.Single(state).Key);
    }

    [Fact]
    public async Task IdenticalLegacyAliasEntriesMergeWithoutDoubleCounting()
    {
        var owner = Guid.NewGuid();
        var entry = new { ownerId = owner.ToString("N"), siteName = "site" };
        await SeedOwnership(new() { ["postgres|foo"] = entry, ["postgresql|foo"] = entry });
        Assert.Equal(1, await ownership.CountForOwnerAsync(owner));
        Assert.Single(await ownership.GetAllAsync());

        await ownership.RecordAsync("postgresql", "foo", owner, "new-site");
        Assert.Single(await files.ReadAsync("config/db-ownership.json", new Dictionary<string, JsonElement>()));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ConflictingLegacyAliasesAreNotChosenOrPublished(bool differentOwner)
    {
        var owner = Guid.NewGuid();
        await SeedOwnership(new()
        {
            ["postgres|foo"] = new { ownerId = owner.ToString("N"), siteName = "first.example" },
            ["postgresql|foo"] = new { ownerId = (differentOwner ? Guid.NewGuid() : owner).ToString("N"), siteName = differentOwner ? "first.example" : "second.example" }
        });
        var before = await File.ReadAllBytesAsync(files.ResolvePath("config/db-ownership.json"));

        var readError = await Assert.ThrowsAsync<AppException>(() => ownership.GetOwnerAsync("postgresql", "foo"));
        Assert.Equal("database.ownership_conflict", readError.MessageKey);
        var writeError = await Assert.ThrowsAsync<AppException>(() => ownership.RecordAsync("mysql", "unrelated", Guid.NewGuid(), null));
        Assert.Equal("database.ownership_conflict", writeError.MessageKey);
        await Assert.ThrowsAsync<AppException>(() => ownership.RemoveAsync("postgresql", "foo"));
        Assert.Equal(before, await File.ReadAllBytesAsync(files.ResolvePath("config/db-ownership.json")));
        Assert.Empty(Directory.GetFiles(Path.Combine(root, "config"), "*.tmp*"));
    }

    [Fact]
    public async Task OwnershipSurvivesFreshServiceAndOnlyMatchingSiteIsRemoved()
    {
        var owner = Guid.NewGuid();
        await ownership.RecordAsync("postgresql", "foo", owner, "lower.example");
        await ownership.RecordAsync("postgresql", "Foo", owner, "upper.example");
        var reopened = new DatabaseOwnershipService(files);
        Assert.Equal(2, await reopened.CountForOwnerAsync(owner));
        await reopened.RemoveBySiteAsync("UPPER.EXAMPLE");
        Assert.Null(await reopened.GetOwnerAsync("postgresql", "Foo"));
        Assert.Equal(owner, await reopened.GetOwnerAsync("postgresql", "foo"));
    }

    [Fact]
    public async Task LegacyLinkAliasCanBeRemovedUsingCanonicalEngine()
    {
        await files.WriteAsync("config/site-databases.json", new[]
        {
            new SiteDatabaseLinkDto { Engine = "postgres", DatabaseName = "Foo", SiteName = "upper.example" },
            new SiteDatabaseLinkDto { Engine = "postgresql", DatabaseName = "foo", SiteName = "lower.example" }
        });
        await links.UnlinkAsync("postgresql", "Foo");
        Assert.Equal("foo", Assert.Single(await links.GetAllAsync()).DatabaseName);
    }

    [Fact]
    public async Task LegacyCredentialAliasUsesExactDatabaseAndAccountNames()
    {
        await files.WriteAsync("config/db-credentials.json", new[]
        {
            new DatabaseCredentialStore.Record { Engine = "postgres", DatabaseName = "Foo", UserName = "User", Host = "localhost", ProtectedPassword = "protected:upper-secret" },
            new DatabaseCredentialStore.Record { Engine = "postgresql", DatabaseName = "foo", UserName = "user", Host = "localhost", ProtectedPassword = "protected:lower-secret" }
        });
        Assert.Equal("upper-secret", (await credentials.FindForDatabaseAsync("postgresql", "Foo", default))?.Password);
        Assert.Equal("lower-secret", (await credentials.FindForDatabaseAsync("postgres", "foo", default))?.Password);
        await credentials.RemoveUserAsync("postgresql", "User", "LOCALHOST", default);
        Assert.Equal("lower-secret", await credentials.FindPasswordAsync("postgres", "user", default));
        Assert.Equal(string.Empty, await credentials.FindPasswordAsync("postgres", "User", default));
    }

    private Task SeedOwnership(Dictionary<string, object> state) => files.WriteAsync("config/db-ownership.json", state);

    [Theory]
    [InlineData("N")]
    [InlineData("D")]
    [InlineData("B")]
    [InlineData("P")]
    [InlineData("X")]
    public async Task ValidOwnerGuidFormatsStayVisibleWithoutRewritingOnRead(string format)
    {
        var owner = Guid.NewGuid();
        await SeedOwnership(new() { ["postgresql|Foo"] = new { ownerId = owner.ToString(format), siteName = "site" } });
        var before = await File.ReadAllBytesAsync(files.ResolvePath("config/db-ownership.json"));

        Assert.Equal(owner, await ownership.GetOwnerAsync("postgresql", "Foo"));
        Assert.Equal(owner, Assert.Single(await ownership.GetAllAsync()).OwnerUserId);
        Assert.Equal(("postgresql", "Foo"), Assert.Single(await ownership.GetOwnedAsync(owner)));
        Assert.Equal(1, await ownership.CountForOwnerAsync(owner));
        Assert.Equal(before, await File.ReadAllBytesAsync(files.ResolvePath("config/db-ownership.json")));
    }

    [Theory]
    [InlineData("N")]
    [InlineData("D")]
    [InlineData("B")]
    public async Task ForeignOwnerGuidFormatsCannotBecomeSiteNameFallback(string format)
    {
        var foreignOwner = Guid.NewGuid();
        var tenant = Guid.NewGuid();
        await SeedOwnership(new() { ["postgresql|Foo"] = new { ownerId = foreignOwner.ToString(format), siteName = "foreign.example" } });
        var scopes = await CreateScopeAsync(tenant, "Foo");

        var error = await Assert.ThrowsAsync<AppException>(() => CustomerAccess.EnsureDatabaseAsync(
            scopes, ownership, Customer(tenant), "postgresql", "Foo", default));
        Assert.Equal("database.not_owned", error.MessageKey);
        Assert.Equal(403, error.HttpStatusCode);

        // The actual owner remains authorized even without a same-named assigned site.
        await CustomerAccess.EnsureDatabaseAsync(scopes, ownership, Customer(foreignOwner), "postgresql", "Foo", default);
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("00000000000000000000000000000000")]
    public async Task InvalidOwnerCannotBecomeSiteNameFallbackOrBePublishedOver(string? storedOwner)
    {
        var tenant = Guid.NewGuid();
        await SeedOwnership(new() { ["postgresql|Foo"] = new { ownerId = storedOwner, siteName = "foreign.example" } });
        var before = await File.ReadAllBytesAsync(files.ResolvePath("config/db-ownership.json"));
        var scopes = await CreateScopeAsync(tenant, "Foo");

        var error = await Assert.ThrowsAsync<AppException>(() => CustomerAccess.EnsureDatabaseAsync(
            scopes, ownership, Customer(tenant), "postgresql", "Foo", default));
        Assert.Equal("database.ownership_invalid", error.MessageKey);
        Assert.Equal(409, error.HttpStatusCode);
        await Assert.ThrowsAsync<AppException>(() => ownership.GetOwnerAsync("postgresql", "Foo"));
        await Assert.ThrowsAsync<AppException>(() => ownership.GetOwnedAsync(tenant));
        await Assert.ThrowsAsync<AppException>(() => ownership.CountForOwnerAsync(tenant));
        await Assert.ThrowsAsync<AppException>(() => ownership.RecordAsync("postgresql", "Foo", tenant, null));
        Assert.Equal(before, await File.ReadAllBytesAsync(files.ResolvePath("config/db-ownership.json")));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("\"invalid\"")]
    public async Task InvalidRootDocumentCannotBecomeEmptyOwnershipOrBeReplaced(string json)
    {
        Directory.CreateDirectory(files.ResolvePath("config"));
        await File.WriteAllTextAsync(files.ResolvePath("config/db-ownership.json"), json);
        var tenant = Guid.NewGuid();
        var scopes = await CreateScopeAsync(tenant, "Foo");

        var error = await Assert.ThrowsAsync<AppException>(() => CustomerAccess.EnsureDatabaseAsync(
            scopes, ownership, Customer(tenant), "postgresql", "Foo", default));
        Assert.Equal("database.ownership_invalid", error.MessageKey);
        await Assert.ThrowsAsync<AppException>(() => ownership.RecordAsync("postgresql", "Foo", tenant, null));
        await Assert.ThrowsAsync<AppException>(() => ownership.RemoveAsync("postgresql", "Foo"));
        await Assert.ThrowsAsync<AppException>(() => ownership.RemoveBySiteAsync("site"));
        Assert.Equal(json, await File.ReadAllTextAsync(files.ResolvePath("config/db-ownership.json")));
    }

    [Fact]
    public async Task EmptyOwnerCannotBeRecorded()
    {
        var error = await Assert.ThrowsAsync<AppException>(() => ownership.RecordAsync("postgresql", "Foo", Guid.Empty, null));
        Assert.Equal("database.ownership_invalid", error.MessageKey);
        Assert.False(File.Exists(files.ResolvePath("config/db-ownership.json")));
    }

    [Fact]
    public async Task DifferentValidGuidFormatsForSameOwnerDoNotCreateAliasConflict()
    {
        var owner = Guid.NewGuid();
        await SeedOwnership(new()
        {
            ["postgres|foo"] = new { ownerId = owner.ToString("D"), siteName = "site" },
            ["postgresql|foo"] = new { ownerId = owner.ToString("N"), siteName = "site" }
        });
        var before = await File.ReadAllBytesAsync(files.ResolvePath("config/db-ownership.json"));
        Assert.Equal(owner, await ownership.GetOwnerAsync("postgresql", "foo"));
        Assert.Equal(1, await ownership.CountForOwnerAsync(owner));
        Assert.Equal(before, await File.ReadAllBytesAsync(files.ResolvePath("config/db-ownership.json")));

        await ownership.RecordAsync("postgresql", "foo", owner, "site");
        var state = await files.ReadAsync("config/db-ownership.json", new Dictionary<string, JsonElement>());
        Assert.Equal(owner.ToString("N"), Assert.Single(state).Value.GetProperty("ownerId").GetString());
    }

    [Fact]
    public async Task NullOwnershipEntryIsExplicitInvalidState()
    {
        await SeedOwnership(new() { ["postgresql|Foo"] = null! });
        var error = await Assert.ThrowsAsync<AppException>(() => ownership.GetAllAsync());
        Assert.Equal("database.ownership_invalid", error.MessageKey);
    }

    [Theory]
    [InlineData("postgresql")]
    [InlineData("|Foo")]
    [InlineData("postgresql|")]
    public async Task InvalidStoredResourceKeyIsNotSilentlySkipped(string key)
    {
        await SeedOwnership(new() { [key] = new { ownerId = Guid.NewGuid().ToString("N"), siteName = "site" } });
        var error = await Assert.ThrowsAsync<AppException>(() => ownership.GetAllAsync());
        Assert.Equal("database.ownership_invalid", error.MessageKey);
    }

    private async Task<CustomerScopeService> CreateScopeAsync(Guid tenant, string site)
    {
        await files.WriteAsync("config/customer-scopes.json", new Dictionary<string, object>
        {
            [tenant.ToString("N")] = new { sites = new[] { site } }
        });
        // This scenario reads assigned sites/parent identifiers only; no IIS or service is used.
        return new CustomerScopeService(files, null!, null!, null!);
    }

    private static ClaimsPrincipal Customer(Guid id) => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.NameIdentifier, id.ToString()), new Claim(ClaimTypes.Role, "Customer")], "test"));

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    private sealed class Protector : ISecretProtector
    {
        public string Protect(string value) => "protected:" + value;
        public string Unprotect(string value) => value["protected:".Length..];
    }
}
