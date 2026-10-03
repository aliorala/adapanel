using System.Text.Json;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Databases;
using AdaPanel.Core.Entities;
using AdaPanel.Core.Enums;
using AdaPanel.Core.Interfaces;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Database;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Support;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

[Collection("Database account lifecycle")]
public sealed class DatabaseAccountPublicationTests
{
    [Theory]
    [InlineData(DbEngine.MySql, false)]
    [InlineData(DbEngine.MySql, true)]
    [InlineData(DbEngine.MariaDb, false)]
    [InlineData(DbEngine.MariaDb, true)]
    [InlineData(DbEngine.PostgreSql, false)]
    [InlineData(DbEngine.PostgreSql, true)]
    public async Task CancellationAfterGrantCannotExposeSharedAccountToOldDatabaseCleanup(DbEngine engine, bool importing)
    {
        using var fixture = new Fixture(engine);
        await fixture.CreateOwnedAccount();
        using var cancellation = new CancellationTokenSource();
        fixture.Provider.AfterGrant = database => { if (database == "db_b") cancellation.Cancel(); };

        var failure = await Capture(() => fixture.Publish(importing, cancellation.Token));
        Assert.IsAssignableFrom<OperationCanceledException>(failure);
        await fixture.Service.DeleteDatabaseAsync(fixture.EngineKey, "db_a");

        Assert.True(fixture.Provider.Users.Contains("shared"), "A cleanup deleted the account already granted to B.");
        Assert.True(fixture.Provider.HasGrant("db_b", "shared"));
        Assert.NotNull(await fixture.Credentials.FindForDatabaseAsync(fixture.EngineKey, "db_b", default));
        Assert.Equal(0, fixture.Provider.UserDeleteCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnwritableUsageStoreMustPreventGrantRatherThanForgetItsResult(bool importing)
    {
        using var fixture = new Fixture(DbEngine.MySql);
        await fixture.CreateOwnedAccount();
        Exception? failure;
        // Readers remain possible, but an atomic replacement cannot replace this handle.
        // This exercises real Windows file sharing, without changing ACLs or live services.
        using (fixture.BlockCredentialReplacement())
            failure = await Capture(() => fixture.Publish(importing));

        Assert.NotNull(failure);
        Assert.False(fixture.Provider.HasGrant("db_b", "shared"), "GRANT ran before its durable usage reference could be saved.");
        Assert.Null(await fixture.Credentials.FindForDatabaseAsync("mysql", "db_b", default));
        await fixture.Service.DeleteDatabaseAsync("mysql", "db_a");
        Assert.DoesNotContain("shared", fixture.Provider.Users);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PostGrantWriteFailureStillLeavesDurableUsageAfterServiceRecreation(bool importing)
    {
        using var fixture = new Fixture(DbEngine.MySql);
        await fixture.CreateOwnedAccount();
        FileStream? lockFile = null;
        fixture.Provider.AfterGrant = database =>
        {
            if (database == "db_b") lockFile = fixture.BlockCredentialReplacement();
        };
        try { _ = await Capture(() => fixture.Publish(importing)); }
        finally { lockFile?.Dispose(); }

        // Recreate the service/store: an in-memory "uncertain account" flag is insufficient.
        await fixture.NewService().DeleteDatabaseAsync("mysql", "db_a");
        Assert.True(fixture.Provider.Users.Contains("shared"), "The on-disk state lost B's confirmed GRANT.");
        Assert.True(fixture.Provider.HasGrant("db_b", "shared"));
        Assert.True(await fixture.Credentials.IsUserReferencedAsync("mysql", "shared", "localhost", default));
    }

    [Theory]
    [InlineData(DbEngine.MySql, false)]
    [InlineData(DbEngine.MySql, true)]
    [InlineData(DbEngine.MariaDb, false)]
    [InlineData(DbEngine.MariaDb, true)]
    [InlineData(DbEngine.PostgreSql, false)]
    [InlineData(DbEngine.PostgreSql, true)]
    public async Task AUsageReferenceNeverAuthorizesDeletingAnExternallyCreatedAccount(DbEngine engine, bool importing)
    {
        using var fixture = new Fixture(engine);
        fixture.Provider.Users.Add("shared");
        await fixture.Publish(importing);
        await fixture.Service.DeleteDatabaseAsync(fixture.EngineKey, "db_b");
        Assert.Contains("shared", fixture.Provider.Users);
        Assert.Equal(0, fixture.Provider.UserDeleteCalls);
        Assert.Null(await fixture.Credentials.FindForDatabaseAsync(fixture.EngineKey, "db_b", default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyCredentialAndPasswordUpdateDoNotProveAccountCreation(bool changePassword)
    {
        using var fixture = new Fixture(DbEngine.MySql);
        fixture.Provider.Users.Add("shared");
        // Deliberately write the historical JSON shape: no future creation-authority field.
        await File.WriteAllTextAsync(fixture.CredentialPath, """
            [{"engine":"mysql","userName":"shared","host":"localhost","databaseName":"db_b","protectedPassword":"old"}]
            """);
        if (changePassword)
            await fixture.Service.SetUserPasswordAsync(new SetDatabaseUserPasswordRequest
            { Engine = "mysql", UserName = "shared", AllowedHost = "localhost", Password = "new-secret" });
        await fixture.Service.DeleteDatabaseAsync("mysql", "db_b");
        Assert.Contains("shared", fixture.Provider.Users);
        Assert.Equal(0, fixture.Provider.UserDeleteCalls);
    }

    [Theory]
    [InlineData(DbEngine.MySql)]
    [InlineData(DbEngine.MariaDb)]
    [InlineData(DbEngine.PostgreSql)]
    public async Task PanelCreationAuthoritySurvivesMovingToTheFinalReference(DbEngine engine)
    {
        using var fixture = new Fixture(engine);
        await fixture.CreateOwnedAccount();
        await fixture.Publish(importing: false);
        await fixture.Service.DeleteDatabaseAsync(fixture.EngineKey, "db_a");
        Assert.Contains("shared", fixture.Provider.Users);
        await fixture.NewService().DeleteDatabaseAsync(fixture.EngineKey, "db_b");
        Assert.DoesNotContain("shared", fixture.Provider.Users);
        Assert.Equal(1, fixture.Provider.UserDeleteCalls);
    }

    [Fact]
    public async Task ExplicitUserDeletionStillRemovesAnExternalAccount()
    {
        using var fixture = new Fixture(DbEngine.MySql);
        fixture.Provider.Users.Add("shared");
        await fixture.Publish(importing: false);
        await fixture.Service.DeleteUserAsync("mysql", "shared", "localhost");
        Assert.DoesNotContain("shared", fixture.Provider.Users);
        Assert.Null(await fixture.Credentials.FindForDatabaseAsync("mysql", "db_b", default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingUsageIsNotAConfirmedLoginAndASuccessfulRetryReconcilesIt(bool importing)
    {
        using var fixture = new Fixture(DbEngine.MySql);
        await fixture.CreateOwnedAccount();
        FileStream? lockFile = null;
        fixture.Provider.AfterGrant = database => { if (database == "db_b") lockFile = fixture.BlockCredentialReplacement(); };
        try { Assert.NotNull(await Capture(() => fixture.Publish(importing))); }
        finally { lockFile?.Dispose(); fixture.Provider.AfterGrant = null; }

        Assert.Null(await fixture.Credentials.FindForDatabaseAsync("mysql", "db_b", default));
        Assert.True(await fixture.Credentials.IsUserReferencedAsync("mysql", "shared", "localhost", default));
        await fixture.Publish(importing);
        Assert.NotNull(await fixture.Credentials.FindForDatabaseAsync("mysql", "db_b", default));
        await fixture.NewService().DeleteDatabaseAsync("mysql", "db_a");
        Assert.Contains("shared", fixture.Provider.Users);
        await fixture.NewService().DeleteDatabaseAsync("mysql", "db_b");
        Assert.DoesNotContain("shared", fixture.Provider.Users);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("false")]
    public async Task NullOrFalseCreationMetadataNeverAuthorizesCleanup(string storedAuthority)
    {
        using var fixture = new Fixture(DbEngine.MySql);
        fixture.Provider.Users.Add("shared");
        await File.WriteAllTextAsync(fixture.CredentialPath,
            "[{\"engine\":\"mysql\",\"userName\":\"shared\",\"host\":\"localhost\",\"databaseName\":\"db_b\",\"protectedPassword\":\"secret\",\"createdByPanel\":" + storedAuthority + "}]");
        await fixture.Publish(importing: true);
        await fixture.Service.DeleteDatabaseAsync("mysql", "db_b");
        Assert.Contains("shared", fixture.Provider.Users);
        Assert.Equal(0, fixture.Provider.UserDeleteCalls);
    }

    [Fact]
    public async Task CancelledPasswordResponseStillPublishesTheConfirmedPasswordWithoutCreationAuthority()
    {
        using var fixture = new Fixture(DbEngine.MySql);
        fixture.Provider.Users.Add("shared");
        await fixture.Credentials.UpsertAsync("mysql", "shared", "localhost", "db_b", "old", default);
        using var cancellation = new CancellationTokenSource();
        fixture.Provider.AfterPassword = cancellation.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Service.SetUserPasswordAsync(new SetDatabaseUserPasswordRequest
        { Engine = "mysql", UserName = "shared", AllowedHost = "localhost", Password = "new-secret" }, cancellation.Token));
        Assert.Equal("new-secret", (await fixture.Credentials.FindForDatabaseAsync("mysql", "db_b", default))?.Password);
        await fixture.Service.DeleteDatabaseAsync("mysql", "db_b");
        Assert.Contains("shared", fixture.Provider.Users);
    }

    [Fact]
    public async Task SeveralOwnedAccountsUseTheSameLockOrderAcrossDatabaseCleanup()
    {
        using var fixture = new Fixture(DbEngine.MySql);
        await fixture.Service.CreateDatabaseAsync(new CreateDatabaseRequest
        { Engine = "mysql", Name = "db_a", UserName = "Zed", Password = "secret" });
        await fixture.Service.CreateUserAsync(new CreateDatabaseUserRequest
        { Engine = "mysql", DatabaseName = "db_a", UserName = "Amy", Password = "secret" });
        foreach (var user in new[] { "Amy", "Zed" })
            await fixture.Service.GrantAccessAsync(new GrantDatabaseAccessRequest
            { Engine = "mysql", DatabaseName = "db_b", UserName = user, AllowedHost = "localhost", Privileges = ["ALL PRIVILEGES"] });

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var parent = await DatabaseLifecycle.EnterSharedEngineAsync("mysql", cancellation.Token);
        var zed = await DatabaseLifecycle.EnterAccountAsync(parent, "mysql", "Zed", cancellation.Token);
        var first = fixture.NewService().DeleteDatabaseAsync("mysql", "db_a", cancellation.Token);
        Task second = Task.CompletedTask;
        try
        {
            while (DatabaseLifecycle.AccountLeaseReferences("mysql", "Zed") < 2)
                await Task.Delay(1, cancellation.Token);
            second = fixture.NewService().DeleteDatabaseAsync("mysql", "db_b", cancellation.Token);
            while (DatabaseLifecycle.AccountLeaseReferences("mysql", "Amy") < 2)
                await Task.Delay(1, cancellation.Token);
        }
        finally { zed.Dispose(); await Task.WhenAll(first, second); }
        Assert.Empty(fixture.Provider.Users);
        Assert.Equal(2, fixture.Provider.UserDeleteCalls);
        Assert.False(DatabaseLifecycle.HasAccountGate("mysql", "Amy"));
        Assert.False(DatabaseLifecycle.HasAccountGate("mysql", "Zed"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TransferCreationAuthorityDependsOnConfirmedCreateUser(bool existingUser)
    {
        using var fixture = new Fixture(DbEngine.MySql);
        if (existingUser) fixture.Provider.Users.Add("shared");
        using (var parent = await DatabaseLifecycle.EnterResourceAsync("mysql", "db_b"))
            await fixture.Service.ProvisionTransferUserAsync(parent, await fixture.Profile(), fixture.Provider,
                "db_b", "shared", "localhost", "candidate", passwordGenerated: true, default);
        await fixture.Service.DeleteDatabaseAsync("mysql", "db_b");
        Assert.Equal(existingUser, fixture.Provider.Users.Contains("shared"));
        Assert.Equal(existingUser ? 0 : 1, fixture.Provider.UserDeleteCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnUncertainGrantFailureKeepsItsDurableIntentUntilDatabaseCleanup(bool importing)
    {
        using var fixture = new Fixture(DbEngine.MySql);
        await fixture.CreateOwnedAccount();
        fixture.Provider.AfterGrant = database => { if (database == "db_b") throw new IOException("fixture-lost-response"); };
        await Assert.ThrowsAsync<IOException>(() => fixture.Publish(importing));
        await fixture.NewService().DeleteDatabaseAsync("mysql", "db_a");
        Assert.Contains("shared", fixture.Provider.Users);
        Assert.True(fixture.Provider.HasGrant("db_b", "shared"));
        Assert.Null(await fixture.Credentials.FindForDatabaseAsync("mysql", "db_b", default));
        await fixture.NewService().DeleteDatabaseAsync("mysql", "db_b");
        Assert.DoesNotContain("shared", fixture.Provider.Users);
    }

    [Fact]
    public async Task AStandalonePanelAccountRetainsItsIndependentLifetime()
    {
        using var fixture = new Fixture(DbEngine.MySql);
        await fixture.Service.CreateUserAsync(new CreateDatabaseUserRequest
        { Engine = "mysql", UserName = "shared", Password = "secret" });
        await fixture.Publish(importing: false);
        await fixture.Service.DeleteDatabaseAsync("mysql", "db_b");
        Assert.Contains("shared", fixture.Provider.Users);
        Assert.Equal(0, fixture.Provider.UserDeleteCalls);
    }

    private static async Task<Exception?> Capture(Func<Task> action)
    {
        try { await action(); return null; }
        catch (Exception exception) { return exception; }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "AdaPanel.AccountPublication." + Guid.NewGuid().ToString("N"));
        private readonly SqliteConnection connection = new("Data Source=:memory:");
        private readonly List<AdaPanelDbContext> contexts = [];
        private readonly Protector protector = new();
        private readonly JsonFileStore files;
        public Provider Provider { get; }
        public DatabaseCredentialStore Credentials { get; }
        public DatabaseService Service { get; }
        public string EngineKey { get; }
        public string CredentialPath => files.ResolvePath("config/db-credentials.json");

        public Fixture(DbEngine engine)
        {
            connection.Open();
            EngineKey = DatabaseProfiles.ResolveDescriptor(engine).EngineKey;
            Provider = new Provider(engine == DbEngine.MariaDb ? DbEngine.MySql : engine);
            var db = Context();
            db.Database.EnsureCreated();
            db.InstalledComponents.Add(new InstalledComponent
            {
                ComponentId = DatabaseProfiles.ResolveDescriptor(engine).ComponentId, Status = ComponentInstallStatus.Installed,
                ProtectedConfigurationJson = JsonSerializer.Serialize(new Dictionary<string, string> { [DatabaseProfiles.GeneratedPasswordKey] = "fixture-admin" })
            });
            db.SaveChanges();
            files = NewFiles();
            files.EnsureDirectory("config");
            Credentials = new DatabaseCredentialStore(files, protector);
            Service = new DatabaseService(Context(), protector, Credentials, new SiteDatabaseLinkService(files), [Provider]);
        }

        private JsonFileStore NewFiles() => new(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = root }));
        private AdaPanelDbContext Context()
        {
            var db = new AdaPanelDbContext(new DbContextOptionsBuilder<AdaPanelDbContext>().UseSqlite(connection).Options);
            contexts.Add(db); return db;
        }
        public DatabaseService NewService()
        {
            var freshFiles = NewFiles();
            return new DatabaseService(Context(), protector, new DatabaseCredentialStore(freshFiles, protector), new SiteDatabaseLinkService(freshFiles), [Provider]);
        }
        public FileStream BlockCredentialReplacement() => new(CredentialPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        public Task<DatabaseConnectionProfile> Profile() => DatabaseProfiles.ResolveAsync(Context(), protector, DatabaseProfiles.ParseEngine(EngineKey), default);
        public Task CreateOwnedAccount() => Service.CreateDatabaseAsync(new CreateDatabaseRequest
        { Engine = EngineKey, Name = "db_a", UserName = "shared", Password = "secret" });
        public Task Publish(bool importing, CancellationToken token = default) => importing
            ? Service.ImportDatabaseAsync(new ImportDatabaseRequest
            { Engine = EngineKey, DatabaseName = "db_b", UserName = "shared", Host = "localhost", Password = "secret" }, token)
            : Service.GrantAccessAsync(new GrantDatabaseAccessRequest
            { Engine = EngineKey, DatabaseName = "db_b", UserName = "shared", AllowedHost = "localhost", Privileges = ["ALL PRIVILEGES"] }, token);
        public void Dispose()
        {
            foreach (var db in contexts) db.Dispose();
            connection.Dispose();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private sealed class Protector : ISecretProtector
    {
        public string Protect(string value) => value;
        public string Unprotect(string value) => value;
    }

    private sealed class Provider(DbEngine engine) : IDatabaseEngineProvider
    {
        public DbEngine Engine => engine;
        public HashSet<string> Users { get; } = new(StringComparer.Ordinal);
        private readonly Dictionary<string, HashSet<string>> grants = new(StringComparer.Ordinal);
        public int UserDeleteCalls;
        public Action<string>? AfterGrant;
        public Action? AfterPassword;
        public bool HasGrant(string database, string user) => grants.TryGetValue(database, out var users) && users.Contains(user);
        public Task<DatabaseWorkspaceDto> GetWorkspaceAsync(DatabaseConnectionProfile profile, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return Task.FromResult(new DatabaseWorkspaceDto()); }
        public Task CreateDatabaseAsync(DatabaseConnectionProfile profile, string database, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return Task.CompletedTask; }
        public Task DeleteDatabaseAsync(DatabaseConnectionProfile profile, string database, CancellationToken token)
        { token.ThrowIfCancellationRequested(); grants.Remove(database); return Task.CompletedTask; }
        public Task CreateUserAsync(DatabaseConnectionProfile profile, string user, string password, string host, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (!Users.Add(user)) throw new AppException("database.user_already_exists", 409, 409);
            return Task.CompletedTask;
        }
        public Task SetUserPasswordAsync(DatabaseConnectionProfile profile, string user, string password, string host, CancellationToken token)
        { token.ThrowIfCancellationRequested(); AfterPassword?.Invoke(); return Task.CompletedTask; }
        public Task DeleteUserAsync(DatabaseConnectionProfile profile, string user, string host, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); UserDeleteCalls++; Users.Remove(user);
            foreach (var users in grants.Values) users.Remove(user);
            return Task.CompletedTask;
        }
        public Task GrantAccessAsync(DatabaseConnectionProfile profile, string database, string user, string host, IReadOnlyCollection<string> privileges, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (!Users.Contains(user)) throw new AppException("database.user_not_found", 404, 404);
            if (!grants.TryGetValue(database, out var users)) grants[database] = users = new(StringComparer.Ordinal);
            users.Add(user); AfterGrant?.Invoke(database); return Task.CompletedTask;
        }
    }
}
