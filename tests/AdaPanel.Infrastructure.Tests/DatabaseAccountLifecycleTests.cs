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
public sealed class DatabaseAccountLifecycleTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    [Theory]
    [InlineData(DbEngine.MySql, "mysql")]
    [InlineData(DbEngine.MariaDb, "mariadb")]
    [InlineData(DbEngine.PostgreSql, "postgres")]
    public async Task ImportCannotPublishAnAccountWhileAnotherDatabaseIsDroppingIt(DbEngine engine, string alias)
    {
        using var fixture = new Fixture(engine);
        await fixture.CreateOwnedAccount();
        fixture.Provider.GrantCalls = 0;
        var entered = Signal();
        var release = Signal();
        fixture.Provider.BeforeUserDelete = async token =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
        };
        using var cancellation = new CancellationTokenSource(Deadline);
        var deleting = fixture.Service().DeleteDatabaseAsync(fixture.EngineKey, "db_a", cancellation.Token);
        await entered.Task.WaitAsync(Deadline);
        var importing = Capture(() => fixture.Service().ImportDatabaseAsync(new ImportDatabaseRequest
        {
            Engine = alias, DatabaseName = "db_b", UserName = "shared", Host = "LOCALHOST", Password = "secret"
        }, cancellation.Token));
        try
        {
            // SQLite profile reads finish synchronously; the first asynchronous barrier is
            // the account lease (or the JSON write in the old, unsafe implementation).
            Assert.Equal(0, fixture.Provider.GrantCalls);
        }
        finally
        {
            release.TrySetResult();
            await deleting.WaitAsync(Deadline);
            var result = await importing.WaitAsync(Deadline);
            Assert.False(result is null && !fixture.Provider.UserExists,
                "Import reported success, but the shared account was deleted immediately afterwards.");
        }
        Assert.Null(await fixture.Credentials.FindForDatabaseAsync(fixture.EngineKey, "db_b", default));
    }

    [Theory]
    [InlineData("mysql")]
    [InlineData("mariadb")]
    [InlineData("postgresql")]
    public async Task CredentialPublicationPreservesEveryDatabaseReference(string engine)
    {
        using var fixture = new Fixture(DbEngine.MySql);
        await fixture.Credentials.UpsertAsync(engine, "shared", "localhost", "db_a", "old", default);
        await fixture.Credentials.UpsertAsync(engine, "shared", "LOCALHOST", "db_b", "old", default);
        Assert.NotNull(await fixture.Credentials.FindForDatabaseAsync(engine, "db_a", default));
        Assert.NotNull(await fixture.Credentials.FindForDatabaseAsync(engine, "db_b", default));
        await fixture.Credentials.UpsertAsync(engine, "shared", "localhost", "", "changed", default);
        Assert.Equal("changed", (await fixture.Credentials.FindForDatabaseAsync(engine, "db_a", default))?.Password);
        Assert.Equal("changed", (await fixture.Credentials.FindForDatabaseAsync(engine, "db_b", default))?.Password);
        await fixture.Credentials.RemoveDatabaseAsync(engine, "db_a", default);
        Assert.True(await fixture.Credentials.IsUserReferencedAsync(engine, "shared", "localhost", default));
    }

    [Theory]
    [InlineData("mysql")]
    [InlineData("mariadb")]
    [InlineData("postgresql")]
    public async Task CaseDistinctAccountsNeverOverwriteOrDeleteEachOthersCredentials(string engine)
    {
        using var fixture = new Fixture(DbEngine.MySql);
        await fixture.Credentials.UpsertAsync(engine, "Shared", "localhost", "upper", "upper-secret", default);
        await fixture.Credentials.UpsertAsync(engine, "shared", "LOCALHOST", "lower", "lower-secret", default);
        Assert.Equal("upper-secret", await fixture.Credentials.FindPasswordAsync(engine, "Shared", default));
        Assert.Equal("lower-secret", await fixture.Credentials.FindPasswordAsync(engine, "shared", default));
        await fixture.Credentials.RemoveUserAsync(engine, "shared", "localhost", default);
        Assert.Equal("upper-secret", await fixture.Credentials.FindPasswordAsync(engine, "Shared", default));
    }

    [Theory]
    [InlineData(DbEngine.MySql)]
    [InlineData(DbEngine.MariaDb)]
    [InlineData(DbEngine.PostgreSql)]
    public async Task GrantPublishesAReferenceBeforeOldDatabaseCleanup(DbEngine engine)
    {
        using var fixture = new Fixture(engine);
        await fixture.CreateOwnedAccount();
        await fixture.Service().GrantAccessAsync(new GrantDatabaseAccessRequest
        { Engine = fixture.EngineKey, DatabaseName = "db_b", UserName = "shared", AllowedHost = "localhost", Privileges = ["ALL PRIVILEGES"] });
        await fixture.Service().DeleteDatabaseAsync(fixture.EngineKey, "db_a");
        Assert.True(fixture.Provider.UserExists);
        Assert.Equal(0, fixture.Provider.UserDeleteCalls);
        Assert.Equal("secret", (await fixture.Credentials.FindForDatabaseAsync(fixture.EngineKey, "db_b", default))?.Password);
    }

    [Fact]
    public async Task GrantToAnotherHostDoesNotInventItsPassword()
    {
        using var fixture = new Fixture(DbEngine.MySql);
        await fixture.Credentials.UpsertAsync("mysql", "shared", "localhost", "db_a", "local-secret", default);
        await fixture.Service().GrantAccessAsync(new GrantDatabaseAccessRequest
        { Engine = "mysql", DatabaseName = "db_b", UserName = "shared", AllowedHost = "%", Privileges = ["SELECT"] });
        var saved = await fixture.Credentials.FindForDatabaseAsync("mysql", "db_b", default);
        Assert.NotNull(saved); Assert.Equal("%", saved.Value.Host); Assert.Equal("", saved.Value.Password);
    }

    [Fact]
    public async Task InvalidDatabaseIsRejectedBeforeCreatingUser()
    {
        using var fixture = new Fixture(DbEngine.MySql);
        await Assert.ThrowsAsync<AppException>(() => fixture.Service().CreateUserAsync(new CreateDatabaseUserRequest
        { Engine = "mysql", UserName = "new_user", DatabaseName = "invalid;db", Password = "secret" }));
        Assert.Equal(0, fixture.Provider.CreateCalls);
        Assert.Equal(0, fixture.Provider.UserDeleteCalls);
    }

    [Theory]
    [InlineData("grant")]
    [InlineData("workspace")]
    public async Task FailedNewUserSetupCompensatesOnlyConfirmedCreation(string failure)
    {
        using var fixture = new Fixture(DbEngine.MySql);
        fixture.Provider.Failure = failure;
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service().CreateUserAsync(new CreateDatabaseUserRequest
        { Engine = "mysql", UserName = "new_user", DatabaseName = "db_a", Password = "secret" }));
        Assert.False(fixture.Provider.UserExists);
        Assert.Equal(1, fixture.Provider.UserDeleteCalls);
        Assert.Null(await fixture.Credentials.FindForDatabaseAsync("mysql", "db_a", default));
    }

    [Fact]
    public async Task FailedCreateNeverDeletesAnExistingUser()
    {
        using var fixture = new Fixture(DbEngine.MySql);
        fixture.Provider.Failure = "exists";
        var error = await Assert.ThrowsAsync<AppException>(() => fixture.Service().CreateUserAsync(new CreateDatabaseUserRequest
        { Engine = "mysql", UserName = "shared", DatabaseName = "db_a", Password = "candidate" }));
        Assert.Equal("database.user_already_exists", error.MessageKey);
        Assert.True(fixture.Provider.UserExists); Assert.Equal(0, fixture.Provider.UserDeleteCalls);
    }

    [Fact]
    public async Task ConfirmedUserDropCleansReferencesEvenWhenRequestIsCancelled()
    {
        using var fixture = new Fixture(DbEngine.MySql);
        await fixture.Credentials.UpsertAsync("mysql", "shared", "localhost", "db_a", "secret", default);
        using var cancellation = new CancellationTokenSource();
        fixture.Provider.BeforeUserDelete = _ => { cancellation.Cancel(); return Task.CompletedTask; };
        await fixture.Service().DeleteUserAsync("mysql", "shared", "localhost", cancellation.Token);
        Assert.False(fixture.Provider.UserExists);
        Assert.Null(await fixture.Credentials.FindForDatabaseAsync("mysql", "db_a", default));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TransferNeverPublishesCandidatePasswordForAnExistingAccount(bool generated)
    {
        using var fixture = new Fixture(DbEngine.MySql);
        await fixture.Credentials.UpsertAsync("mysql", "shared", "localhost", "db_a", "actual", default);
        fixture.Provider.Failure = "exists";
        using var parent = await DatabaseLifecycle.EnterResourceAsync("mysql", "db_b");
        var result = await fixture.Service().ProvisionTransferUserAsync(parent, await fixture.Profile(), fixture.Provider,
            "db_b", "shared", "localhost", "candidate", generated, default);
        Assert.Equal(("shared", ""), result);
        Assert.Equal("actual", (await fixture.Credentials.FindForDatabaseAsync("mysql", "db_b", default))?.Password);
        Assert.Equal(0, fixture.Provider.UserDeleteCalls);
    }

    [Fact]
    public async Task TransferDoesNotTreatConnectionFailureAsExistingAccount()
    {
        using var fixture = new Fixture(DbEngine.MySql);
        fixture.Provider.Failure = "create";
        using var parent = await DatabaseLifecycle.EnterResourceAsync("mysql", "db_b");
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service().ProvisionTransferUserAsync(
            parent, new DatabaseConnectionProfile(DbEngine.MySql, "mysql", "", "", "", "", 0, "", "", true, ""), fixture.Provider,
            "db_b", "shared", "localhost", "candidate", true, default));
        Assert.Equal(0, fixture.Provider.GrantCalls);
        Assert.Equal(0, fixture.Provider.UserDeleteCalls);
        Assert.Null(await fixture.Credentials.FindForDatabaseAsync("mysql", "db_b", default));
    }

    [Fact]
    public async Task TransferPublishesGeneratedPasswordOnlyAfterNewAccountIsReady()
    {
        using var fixture = new Fixture(DbEngine.MySql);
        using var parent = await DatabaseLifecycle.EnterResourceAsync("mysql", "db_b");
        var result = await fixture.Service().ProvisionTransferUserAsync(parent, await fixture.Profile(), fixture.Provider,
            "db_b", "new_user", "localhost", "generated", true, default);
        Assert.Equal(("new_user", "generated"), result);
        Assert.Equal("generated", (await fixture.Credentials.FindForDatabaseAsync("mysql", "db_b", default))?.Password);
    }

    [Theory]
    [InlineData("create-database")]
    [InlineData("create-user")]
    [InlineData("delete-user")]
    [InlineData("password")]
    [InlineData("grant")]
    [InlineData("import")]
    public async Task EveryAccountPublisherWaitsForTheSameFamily(string operation)
    {
        using var fixture = new Fixture(DbEngine.MySql);
        using var cancellation = new CancellationTokenSource(Deadline);
        using var parent = await DatabaseLifecycle.EnterSharedEngineAsync("mysql", cancellation.Token);
        var held = await DatabaseLifecycle.EnterAccountAsync(parent, "mysql", "shared", cancellation.Token);
        var service = fixture.Service();
        async Task Run()
        {
            switch (operation)
            {
                case "create-database": await service.CreateDatabaseAsync(new CreateDatabaseRequest { Engine = "mysql", Name = "db_b", UserName = "shared", Password = "secret" }, cancellation.Token); break;
                case "create-user": await service.CreateUserAsync(new CreateDatabaseUserRequest { Engine = "mysql", UserName = "shared", DatabaseName = "db_b", Password = "secret" }, cancellation.Token); break;
                case "delete-user": await service.DeleteUserAsync("mysql", "shared", "localhost", cancellation.Token); break;
                case "password": await service.SetUserPasswordAsync(new SetDatabaseUserPasswordRequest { Engine = "mysql", UserName = "shared", AllowedHost = "localhost", Password = "secret" }, cancellation.Token); break;
                case "grant": await service.GrantAccessAsync(new GrantDatabaseAccessRequest { Engine = "mysql", DatabaseName = "db_b", UserName = "shared", AllowedHost = "localhost", Privileges = ["SELECT"] }, cancellation.Token); break;
                case "import": await service.ImportDatabaseAsync(new ImportDatabaseRequest { Engine = "mysql", DatabaseName = "db_b", UserName = "shared", Host = "localhost", Password = "secret" }, cancellation.Token); break;
            }
        }
        var pending = Run();
        try
        {
            await WaitForAccountWaiter("mysql", "shared", pending, cancellation.Token);
            Assert.False(pending.IsCompleted);
            Assert.Equal(0, fixture.Provider.MutationCalls);
        }
        finally { held.Dispose(); await pending; }
        Assert.True(fixture.Provider.MutationCalls > 0);
    }

    [Fact]
    public async Task DeleteDatabaseRechecksCredentialAssociationAfterWaitingForAccount()
    {
        using var fixture = new Fixture(DbEngine.MySql);
        await fixture.CreateOwnedAccount();
        using var cancellation = new CancellationTokenSource(Deadline);
        using var parent = await DatabaseLifecycle.EnterSharedEngineAsync("mysql", cancellation.Token);
        var held = await DatabaseLifecycle.EnterAccountAsync(parent, "mysql", "shared", cancellation.Token);
        var deleting = fixture.Service().DeleteDatabaseAsync("mysql", "db_a", cancellation.Token);
        try
        {
            await WaitForAccountWaiter("mysql", "shared", deleting, cancellation.Token);
            // A completed standalone account deletion removes its credential association.
            // Recreating that name must not make the old DB snapshot its deletion authority.
            await fixture.Credentials.RemoveUserAsync("mysql", "shared", "localhost", default);
            fixture.Provider.UserExists = true;
        }
        finally { held.Dispose(); await deleting; }
        Assert.True(fixture.Provider.UserExists);
        Assert.Equal(0, fixture.Provider.UserDeleteCalls);
    }

    [Fact]
    public async Task IncompleteNewUserCompensationReportsFailureAndKeepsRecoveryCredential()
    {
        using var fixture = new Fixture(DbEngine.MySql);
        fixture.Provider.Failure = "workspace";
        fixture.Provider.BeforeUserDelete = _ => throw new IOException("fixture-drop");
        var error = await Assert.ThrowsAsync<AppException>(() => fixture.Service().CreateUserAsync(new CreateDatabaseUserRequest
        { Engine = "mysql", UserName = "shared", DatabaseName = "db_b", Password = "secret" }));
        Assert.Equal("database.user_create_cleanup_incomplete", error.MessageKey);
        Assert.True(fixture.Provider.UserExists);
        Assert.Equal("secret", (await fixture.Credentials.FindForDatabaseAsync("mysql", "db_b", default))?.Password);
    }

    [Fact]
    public async Task TransferCannotSuppressIncompleteCompensation()
    {
        using var fixture = new Fixture(DbEngine.MySql);
        fixture.Provider.Failure = "grant";
        fixture.Provider.BeforeUserDelete = _ => throw new IOException("fixture-drop");
        using var parent = await DatabaseLifecycle.EnterResourceAsync("mysql", "db_b");
        var profile = await fixture.Profile();
        var error = await Assert.ThrowsAsync<AppException>(() => fixture.Service().ProvisionTransferUserAsync(
            parent, profile, fixture.Provider, "db_b", "shared", "localhost", "generated", true, default));
        Assert.Equal("database.user_create_cleanup_incomplete", error.MessageKey);
        Assert.False(DatabaseService.CanIgnoreTransferAccountFailure(error));
        Assert.True(fixture.Provider.UserExists);
    }

    [Fact]
    public void TransferCancellationIsNeverAnOptionalFailure()
    {
        Assert.False(DatabaseService.CanIgnoreTransferAccountFailure(new OperationCanceledException()));
        Assert.False(DatabaseService.CanIgnoreTransferAccountFailure(new TaskCanceledException()));
        Assert.True(DatabaseService.CanIgnoreTransferAccountFailure(new AppException("database.user_not_found", 404, 404)));
    }

    [Fact]
    public async Task RemoteStateDistinguishesCaseSensitiveAccountNames()
    {
        using var fixture = new Fixture(DbEngine.MySql);
        var service = new RemoteDbAccessService(null!, null!, null!, fixture.Credentials, [], null!, null!, fixture.Files);
        var write = typeof(RemoteDbAccessService).GetMethod("WriteUserStateAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        async Task Save(string user, string ip)
        {
            await (Task)write.Invoke(service, ["postgres", user, RemoteAccessMode.Allowlist,
                RemoteAccessOrigin.ParseAll([ip]), CancellationToken.None])!;
        }
        await Save("User", "192.0.2.1");
        await Save("user", "192.0.2.2");
        var state = await fixture.Files.ReadAsync("config/remote-db.json", new RemoteDbAccessService.RemoteDbState());
        Assert.Equal(2, state.Users.Count);
        Assert.Equal("192.0.2.1", Assert.Single(state.Users["postgresql|User"].Origins));
        Assert.Equal("192.0.2.2", Assert.Single(state.Users["postgresql|user"].Origins));
    }

    private static async Task WaitForAccountWaiter(string engine, string user, Task operation, CancellationToken token)
    {
        while (DatabaseLifecycle.AccountLeaseReferences(engine, user) < 2)
        {
            if (operation.IsCompleted) { await operation; Assert.Fail("Account mutation bypassed its family lease."); }
            await Task.Delay(1, token);
        }
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task<Exception?> Capture(Func<Task> action)
    {
        try { await action(); return null; }
        catch (Exception exception) { return exception; }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "AdaPanel.AccountLifecycle." + Guid.NewGuid().ToString("N"));
        private readonly SqliteConnection connection = new("Data Source=:memory:");
        private readonly List<AdaPanelDbContext> contexts = [];
        private readonly Protector protector = new();
        private readonly JsonFileStore files;
        public JsonFileStore Files => files;
        public Provider Provider { get; }
        public DatabaseCredentialStore Credentials { get; }
        public string EngineKey { get; }

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
            files = new JsonFileStore(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = root }));
            Credentials = new DatabaseCredentialStore(files, protector);
        }

        private AdaPanelDbContext Context()
        {
            var db = new AdaPanelDbContext(new DbContextOptionsBuilder<AdaPanelDbContext>().UseSqlite(connection).Options);
            contexts.Add(db);
            return db;
        }

        public DatabaseService Service() => new(Context(), protector, Credentials, new SiteDatabaseLinkService(files), [Provider]);
        public Task CreateOwnedAccount() => Service().CreateDatabaseAsync(new CreateDatabaseRequest
        { Engine = EngineKey, Name = "db_a", UserName = "shared", Password = "secret" });
        public Task<DatabaseConnectionProfile> Profile() => DatabaseProfiles.ResolveAsync(Context(), protector, DatabaseProfiles.ParseEngine(EngineKey), default);
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
        public bool UserExists = true;
        public int GrantCalls;
        public int CreateCalls;
        public int UserDeleteCalls;
        public int MutationCalls;
        public string Failure = "";
        public Func<CancellationToken, Task>? BeforeUserDelete;
        public Task<DatabaseWorkspaceDto> GetWorkspaceAsync(DatabaseConnectionProfile profile, CancellationToken token) =>
            Failure == "workspace" ? throw new InvalidOperationException("fixture-workspace") : Task.FromResult(new DatabaseWorkspaceDto());
        public Task CreateDatabaseAsync(DatabaseConnectionProfile profile, string name, CancellationToken token) { Interlocked.Increment(ref MutationCalls); return Task.CompletedTask; }
        public Task DeleteDatabaseAsync(DatabaseConnectionProfile profile, string name, CancellationToken token) { Interlocked.Increment(ref MutationCalls); return Task.CompletedTask; }
        public Task CreateUserAsync(DatabaseConnectionProfile profile, string user, string password, string host, CancellationToken token)
        {
            Interlocked.Increment(ref CreateCalls);
            Interlocked.Increment(ref MutationCalls);
            if (Failure == "exists") throw new AppException("database.user_already_exists", 409, 409);
            if (Failure == "create") throw new InvalidOperationException("fixture-create");
            UserExists = true; return Task.CompletedTask;
        }
        public Task SetUserPasswordAsync(DatabaseConnectionProfile profile, string user, string password, string host, CancellationToken token) { Interlocked.Increment(ref MutationCalls); return Task.CompletedTask; }
        public async Task DeleteUserAsync(DatabaseConnectionProfile profile, string user, string host, CancellationToken token)
        {
            Interlocked.Increment(ref UserDeleteCalls);
            Interlocked.Increment(ref MutationCalls);
            if (BeforeUserDelete is not null) await BeforeUserDelete(token);
            UserExists = false;
        }
        public Task GrantAccessAsync(DatabaseConnectionProfile profile, string database, string user, string host, IReadOnlyCollection<string> privileges, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Interlocked.Increment(ref GrantCalls);
            Interlocked.Increment(ref MutationCalls);
            if (Failure == "grant") throw new InvalidOperationException("fixture-grant");
            if (!UserExists) throw new AppException("database.user_not_found", 404, 404);
            return Task.CompletedTask;
        }
    }
}

[CollectionDefinition("Database account lifecycle", DisableParallelization = true)]
public sealed class DatabaseAccountLifecycleCollection { }
