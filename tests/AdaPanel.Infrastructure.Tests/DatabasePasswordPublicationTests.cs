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
using MySqlConnector;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

[Collection("Database account lifecycle")]
public sealed class DatabasePasswordPublicationTests
{
    [Theory]
    [InlineData("")]
    [InlineData("      ")]
    [InlineData("short")]
    [InlineData("  abc  ")]
    public async Task InvalidPasswordIsRejectedBeforeChangingTheAccount(string password)
    {
        using var fixture = new Fixture();
        await fixture.Seed();
        await Assert.ThrowsAsync<AppException>(() => fixture.Change(password));
        Assert.Equal(0, fixture.Provider.PasswordCalls);
        Assert.Equal("old-secret", fixture.Provider.Password);
        Assert.Equal("old-secret", await fixture.NewCredentials().FindPasswordAsync("mysql", "Shared", default));
    }

    [Fact]
    public async Task PasswordProtectionFailurePreventsTheSqlMutation()
    {
        using var fixture = new Fixture();
        await fixture.Seed();
        fixture.Protector.FailProtection = true;
        await Assert.ThrowsAsync<IOException>(() => fixture.Change());
        Assert.Equal(0, fixture.Provider.PasswordCalls);
        Assert.Equal("old-secret", fixture.Provider.Password);
    }

    [Fact]
    public async Task UnwritableCredentialFilePreventsTheSqlMutation()
    {
        using var fixture = new Fixture();
        await fixture.Seed();
        using (fixture.BlockReplacement())
            await AssertStorageFailure(() => fixture.Change());
        Assert.Equal(0, fixture.Provider.PasswordCalls);
        Assert.Equal("old-secret", await fixture.NewCredentials().FindPasswordAsync("mysql", "Shared", default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PostAlterWriteFailureCannotAdvertiseTheOldPasswordAfterRestart(bool panelCreated)
    {
        using var fixture = new Fixture();
        await fixture.Seed(panelCreated);
        FileStream? locked = null;
        fixture.Provider.AfterPassword = () => locked = fixture.BlockReplacement();
        try { await AssertStorageFailure(() => fixture.Change()); }
        finally { locked?.Dispose(); }
        Assert.Equal("new-secret", fixture.Provider.Password);
        var restarted = fixture.NewCredentials();
        Assert.Equal(string.Empty, await restarted.FindPasswordAsync("mysql", "Shared", default));
        Assert.Null(await restarted.FindForDatabaseAsync("mysql", "db_a", default));
        Assert.Null(await restarted.FindForDatabaseAsync("mysql", "db_b", default));
        Assert.True(await restarted.IsUserReferencedAsync("mysql", "Shared", "localhost", default));
        var cleanup = await restarted.FindCleanupAccountsAsync("mysql", "db_a", default);
        Assert.Equal(panelCreated ? 1 : 0, cleanup.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnUnknownSqlOutcomeHidesCredentialsUntilASuccessfulRetry(bool applyBeforeFailure)
    {
        using var fixture = new Fixture();
        await fixture.Seed();
        fixture.Provider.BeforePassword = () =>
        {
            if (!applyBeforeFailure) throw new IOException("fixture-response-lost-before-apply");
        };
        fixture.Provider.AfterPassword = () => throw new IOException("fixture-response-lost-after-apply");
        await Assert.ThrowsAsync<IOException>(() => fixture.Change());
        Assert.Equal(string.Empty, await fixture.NewCredentials().FindPasswordAsync("mysql", "Shared", default));
        Assert.Null(await fixture.NewCredentials().FindForDatabaseAsync("mysql", "db_a", default));
        fixture.Provider.BeforePassword = null;
        fixture.Provider.AfterPassword = null;
        await fixture.Change("retry-secret");
        Assert.Equal("retry-secret", fixture.Provider.Password);
        Assert.Equal("retry-secret", (await fixture.NewCredentials().FindForDatabaseAsync("mysql", "db_a", default))?.Password);
        Assert.Equal("retry-secret", (await fixture.NewCredentials().FindForDatabaseAsync("mysql", "db_b", default))?.Password);
    }

    [Fact]
    public async Task AConfirmedPasswordIsPublishedDespiteRequestCancellation()
    {
        using var fixture = new Fixture();
        await fixture.Seed();
        using var cancellation = new CancellationTokenSource();
        fixture.Provider.AfterPassword = cancellation.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Change(token: cancellation.Token));
        Assert.Equal("new-secret", await fixture.NewCredentials().FindPasswordAsync("mysql", "Shared", default));
    }

    [Fact]
    public async Task ValidPasswordPreservesSpacesAndOtherHostAndCaseSensitiveAccounts()
    {
        using var fixture = new Fixture();
        await fixture.Seed();
        await fixture.NewCredentials().UpsertAsync("mysql", "Shared", "%", "remote_db", "remote-secret", default);
        await fixture.NewCredentials().UpsertAsync("mysql", "shared", "localhost", "other_db", "other-secret", default);
        await fixture.Change("  valid secret  ");
        Assert.Equal("  valid secret  ", fixture.Provider.Password);
        Assert.Equal("  valid secret  ", (await fixture.NewCredentials().FindForDatabaseAsync("mysql", "db_a", default))?.Password);
        Assert.Equal("remote-secret", (await fixture.NewCredentials().FindForDatabaseAsync("mysql", "remote_db", default))?.Password);
        Assert.Equal("other-secret", (await fixture.NewCredentials().FindForDatabaseAsync("mysql", "other_db", default))?.Password);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RootFailureBeforeApplyingPasswordRestoresTheVerifiedOldConfiguration(bool cancel)
    {
        using var fixture = new Fixture(emptyAdmin: true);
        using var cancellation = new CancellationTokenSource();
        fixture.Provider.BeforeRootPassword = () =>
        {
            if (cancel) cancellation.Cancel();
            throw new IOException("fixture-root-not-applied");
        };
        await Assert.ThrowsAsync<IOException>(() => fixture.Service.HardenAdminAccountsAsync("mysql", cancellation.Token));
        Assert.Equal(string.Empty, (await fixture.Profile(allowEmpty: true)).AdminPassword);
        Assert.Equal(string.Empty, fixture.Provider.RootPassword);
        Assert.Equal(1, fixture.Provider.RootPasswordCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RootFailureAfterApplyingPasswordMustNotRestoreAnInvalidOldPassword(bool cancel)
    {
        using var fixture = new Fixture(emptyAdmin: true);
        using var cancellation = new CancellationTokenSource();
        fixture.Provider.AfterRootPassword = () =>
        {
            if (cancel) cancellation.Cancel();
            throw new IOException("fixture-root-verification-response-lost");
        };
        await Assert.ThrowsAsync<IOException>(() => fixture.Service.HardenAdminAccountsAsync("mysql", cancellation.Token));
        Assert.NotEmpty(fixture.Provider.RootPassword);
        Assert.Equal(fixture.Provider.RootPassword, (await fixture.Profile()).AdminPassword);
        Assert.Equal(1, fixture.Provider.RootPasswordCalls);
    }

    [Fact]
    public async Task AnUnverifiableRootOutcomeMustFailClosedAcrossServiceRestart()
    {
        using var fixture = new Fixture(emptyAdmin: true);
        fixture.Provider.AfterRootPassword = () => throw new IOException("fixture-root-result-unknown");
        fixture.Provider.VerificationFailure = true;
        await Assert.ThrowsAnyAsync<Exception>(() => fixture.Service.HardenAdminAccountsAsync("mysql"));
        await Assert.ThrowsAsync<AppException>(() => fixture.Profile(allowEmpty: true));
        var pending = await fixture.EngineStatus();
        Assert.True(pending.AdminPasswordPending);
        Assert.False(pending.Ready);
        Assert.DoesNotContain(fixture.Provider.RootPassword, JsonSerializer.Serialize(pending));
        fixture.Provider.VerificationFailure = false;
        fixture.Provider.AfterRootPassword = null;
        await fixture.NewService().HardenAdminAccountsAsync("mysql");
        Assert.Equal(fixture.Provider.RootPassword, (await fixture.Profile()).AdminPassword);
        Assert.Equal(1, fixture.Provider.RootPasswordCalls);
        var confirmed = await fixture.EngineStatus();
        Assert.False(confirmed.AdminPasswordPending);
        Assert.True(confirmed.Ready);
    }

    [Fact]
    public async Task SuccessfulHardeningKeepsTheGeneratedStrongPassword()
    {
        using var fixture = new Fixture(emptyAdmin: true);
        await fixture.Service.HardenAdminAccountsAsync("mysql");
        Assert.Equal(28, fixture.Provider.RootPassword.Length);
        Assert.Equal(fixture.Provider.RootPassword, (await fixture.Profile()).AdminPassword);
        await fixture.NewService().HardenAdminAccountsAsync("mysql");
        Assert.Equal(1, fixture.Provider.RootPasswordCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AddingAReferenceCannotRepublishAnUncertainPassword(bool throughUpsert)
    {
        using var fixture = new Fixture();
        await fixture.Seed(panelCreated: true);
        fixture.Provider.AfterPassword = () => throw new IOException("fixture-unknown-password-result");
        await Assert.ThrowsAsync<IOException>(() => fixture.Change());
        var credentials = fixture.NewCredentials();
        if (throughUpsert)
            await credentials.UpsertAsync("mysql", "Shared", "localhost", "db_c", "old-secret", default);
        else
        {
            await credentials.BeginReferenceAsync("mysql", "Shared", "localhost", "db_c", default);
            await credentials.ConfirmReferenceAsync("mysql", "Shared", "localhost", "db_c", default);
        }
        Assert.Null(await fixture.NewCredentials().FindForDatabaseAsync("mysql", "db_c", default));
        Assert.Equal(string.Empty, await fixture.NewCredentials().FindPasswordAsync("mysql", "Shared", default));
        Assert.Single(await credentials.FindCleanupAccountsAsync("mysql", "db_c", default));
        fixture.Provider.AfterPassword = null;
        await fixture.Change("retry-secret");
        Assert.Equal("retry-secret", (await credentials.FindForDatabaseAsync("mysql", "db_c", default))?.Password);
    }

    [Fact]
    public async Task AnUncertainHostPasswordCannotBeReplacedWithAnotherHostsPasswordForRemoteAccess()
    {
        using var fixture = new Fixture();
        await fixture.Seed();
        await fixture.NewCredentials().UpsertAsync("mysql", "Shared", "%", "remote_db", "remote-secret", default);
        fixture.Provider.AfterPassword = () => throw new IOException("fixture-unknown-password-result");
        await Assert.ThrowsAsync<IOException>(() => fixture.Change());
        Assert.Equal(string.Empty, await fixture.NewCredentials().FindPasswordAsync("mysql", "Shared", default));
        Assert.Equal("remote-secret", (await fixture.NewCredentials().FindForDatabaseAsync("mysql", "remote_db", default))?.Password);
    }

    [Theory]
    [InlineData(DbEngine.MySql, false)]
    [InlineData(DbEngine.MySql, true)]
    [InlineData(DbEngine.MariaDb, false)]
    [InlineData(DbEngine.MariaDb, true)]
    public async Task RegisterCredentialsOnlyResolvesPendingRootStateWithFreshAuthentication(DbEngine engine, bool correctPassword)
    {
        using var fixture = new Fixture(emptyAdmin: true, engine);
        var engineKey = DatabaseProfiles.ResolveDescriptor(engine).EngineKey;
        fixture.Provider.AfterRootPassword = () => throw new IOException("fixture-unknown-root-result");
        fixture.Provider.VerificationFailure = true;
        await Assert.ThrowsAsync<IOException>(() => fixture.Service.HardenAdminAccountsAsync(engineKey));
        fixture.Provider.VerificationFailure = false;
        fixture.Provider.AfterRootPassword = null;
        var request = new RegisterDatabaseCredentialsRequest
        { Engine = engineKey, Password = correctPassword ? fixture.Provider.RootPassword : "previously-pooled-wrong-password" };
        // GetWorkspace deliberately accepts every password, as an already-authenticated
        // pooled connection could. Only the new verification contract performs authentication.
        if (correctPassword)
        {
            await fixture.NewService().RegisterCredentialsAsync(request);
            Assert.Equal(fixture.Provider.RootPassword, (await fixture.Profile()).AdminPassword);
        }
        else
        {
            await Assert.ThrowsAsync<AppException>(() => fixture.NewService().RegisterCredentialsAsync(request));
            var beforeRead = await fixture.AdminJson();
            await Assert.ThrowsAsync<AppException>(() => fixture.Profile(allowEmpty: true));
            Assert.Equal(beforeRead, await fixture.AdminJson());
        }
    }

    [Theory]
    [InlineData(DbEngine.MySql, false)]
    [InlineData(DbEngine.MySql, true)]
    [InlineData(DbEngine.MariaDb, false)]
    [InlineData(DbEngine.MariaDb, true)]
    public async Task NormalRegistrationRequiresFreshAuthenticationEvenWithoutPendingState(DbEngine engine, bool correctPassword)
    {
        using var fixture = new Fixture(engine: engine);
        var engineKey = DatabaseProfiles.ResolveDescriptor(engine).EngineKey;
        var before = await fixture.AdminJson();
        var request = new RegisterDatabaseCredentialsRequest
        { Engine = engineKey, Password = correctPassword ? fixture.Provider.RootPassword : "previously-pooled-wrong-password" };
        if (correctPassword)
        {
            var result = await fixture.Service.RegisterCredentialsAsync(request);
            Assert.True(result.Ready);
            Assert.Equal(fixture.Provider.RootPassword, (await fixture.Profile()).AdminPassword);
            Assert.Equal(1, fixture.Provider.WorkspaceCalls);
        }
        else
        {
            await Assert.ThrowsAsync<AppException>(() => fixture.Service.RegisterCredentialsAsync(request));
            Assert.Equal(before, await fixture.AdminJson());
            Assert.Equal(0, fixture.Provider.WorkspaceCalls);
        }
        // Fake GetWorkspace accepts cached sessions. A separate authentication check
        // must happen even when no previous password change left pending metadata.
        Assert.Equal(1, fixture.Provider.VerificationCalls);
    }

    [Theory]
    [InlineData(DbEngine.MySql)]
    [InlineData(DbEngine.MariaDb)]
    public async Task RegistrationDoesNotConvertCallerCancellationIntoInvalidCredentials(DbEngine engine)
    {
        using var fixture = new Fixture(engine: engine);
        using var cancellation = new CancellationTokenSource();
        var before = await fixture.AdminJson();
        fixture.Provider.BeforeWorkspace = cancellation.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Service.RegisterCredentialsAsync(new RegisterDatabaseCredentialsRequest
        { Engine = DatabaseProfiles.ResolveDescriptor(engine).EngineKey, Password = fixture.Provider.RootPassword }, cancellation.Token));
        Assert.Equal(before, await fixture.AdminJson());
    }

    [Fact]
    public async Task RootConfirmationDatabaseFailureRetainsPendingStateInTheTrackedAndFreshContexts()
    {
        using var fixture = new Fixture(emptyAdmin: true);
        fixture.Provider.AfterRootPassword = fixture.RejectAdminWrites;
        await Assert.ThrowsAsync<DbUpdateException>(() => fixture.Service.HardenAdminAccountsAsync("mysql"));
        await Assert.ThrowsAsync<AppException>(() => fixture.Service.GetWorkspaceAsync("mysql"));
        await Assert.ThrowsAsync<AppException>(() => fixture.Profile(allowEmpty: true));
        fixture.AllowAdminWrites();
        fixture.Provider.AfterRootPassword = null;
        await fixture.NewService().HardenAdminAccountsAsync("mysql");
        Assert.Equal(fixture.Provider.RootPassword, (await fixture.Profile()).AdminPassword);
        Assert.Equal(1, fixture.Provider.RootPasswordCalls);
    }

    [Fact]
    public async Task RootVerificationRequiresANewUnpooledAuthentication()
    {
        using var fixture = new Fixture();
        var profile = await fixture.Profile();
        var connection = new MySqlConnectionStringBuilder(MySqlDatabaseProvider.PasswordVerificationConnectionString(profile));
        Assert.False(connection.Pooling);
        Assert.Equal(profile.AdminPassword, connection.Password);
        Assert.Equal(profile.AdminUserName, connection.UserID);
        Assert.Equal(profile.Host, connection.Server);
        Assert.Equal((uint)profile.Port, connection.Port);
    }

    [Fact]
    public async Task RegisterConfirmationWriteFailureCannotClearPendingStateInTheTrackedContext()
    {
        using var fixture = new Fixture(emptyAdmin: true);
        fixture.Provider.AfterRootPassword = () => throw new IOException("fixture-root-result-unknown");
        fixture.Provider.VerificationFailure = true;
        await Assert.ThrowsAsync<IOException>(() => fixture.Service.HardenAdminAccountsAsync("mysql"));
        fixture.Provider.VerificationFailure = false;
        fixture.RejectAdminWrites();
        var registrationService = fixture.NewService();
        await Assert.ThrowsAsync<DbUpdateException>(() => registrationService.RegisterCredentialsAsync(new RegisterDatabaseCredentialsRequest
            { Engine = "mysql", Password = fixture.Provider.RootPassword }));
        await Assert.ThrowsAsync<AppException>(() => registrationService.GetWorkspaceAsync("mysql"));
        await Assert.ThrowsAsync<AppException>(() => fixture.Profile(allowEmpty: true));
    }

    [Fact]
    public async Task APositivelyRecreatedAccountCanReplaceStaleUncertainPasswordMetadata()
    {
        using var fixture = new Fixture();
        await fixture.Seed();
        fixture.Provider.AfterPassword = () => throw new IOException("fixture-result-unknown");
        await Assert.ThrowsAsync<IOException>(() => fixture.Change());
        // Model the old physical account having been removed outside the panel. The
        // subsequent CREATE succeeds, which is stronger evidence than an import/upsert.
        fixture.Provider.Password = string.Empty;
        await fixture.Service.CreateUserAsync(new CreateDatabaseUserRequest
        { Engine = "mysql", UserName = "Shared", AllowedHost = "localhost", DatabaseName = "db_a", Password = "recreated-secret" });
        Assert.Equal("recreated-secret", fixture.Provider.Password);
        Assert.Equal("recreated-secret", (await fixture.NewCredentials().FindForDatabaseAsync("mysql", "db_a", default))?.Password);
    }

    private static async Task AssertStorageFailure(Func<Task> action)
    {
        var failure = await Record.ExceptionAsync(action);
        Assert.True(failure is IOException or UnauthorizedAccessException, failure?.ToString() ?? "Expected a storage failure.");
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "AdaPanel.PasswordPublication." + Guid.NewGuid().ToString("N"));
        private readonly SqliteConnection connection = new("Data Source=:memory:");
        private readonly List<AdaPanelDbContext> contexts = [];
        private readonly JsonFileStore files;
        private readonly DbEngine engine;
        public readonly Protector Protector = new();
        public readonly Provider Provider = new();
        public DatabaseService Service { get; }
        private string CredentialPath => files.ResolvePath("config/db-credentials.json");

        public Fixture(bool emptyAdmin = false, DbEngine engine = DbEngine.MySql)
        {
            this.engine = engine;
            connection.Open();
            var db = Context();
            db.Database.EnsureCreated();
            Provider.RootPassword = emptyAdmin ? string.Empty : "fixture-admin";
            db.InstalledComponents.Add(new InstalledComponent
            {
                ComponentId = DatabaseProfiles.ResolveDescriptor(engine).ComponentId, Status = ComponentInstallStatus.Installed,
                ProtectedConfigurationJson = JsonSerializer.Serialize(new Dictionary<string, string>
                    { [DatabaseProfiles.GeneratedPasswordKey] = Provider.RootPassword })
            });
            db.SaveChanges();
            files = new JsonFileStore(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = root }));
            files.EnsureDirectory("config");
            Service = NewService();
        }

        private AdaPanelDbContext Context()
        {
            var db = new AdaPanelDbContext(new DbContextOptionsBuilder<AdaPanelDbContext>().UseSqlite(connection).Options);
            contexts.Add(db); return db;
        }
        public DatabaseCredentialStore NewCredentials() => new(files, Protector);
        public DatabaseService NewService() => new(Context(), Protector, NewCredentials(), new SiteDatabaseLinkService(files), [Provider]);
        public FileStream BlockReplacement() => new(CredentialPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        public Task<DatabaseConnectionProfile> Profile(bool allowEmpty = false) => DatabaseProfiles.ResolveAsync(Context(), Protector, engine, default, allowEmpty);
        public async Task<string> AdminJson() => (await Context().InstalledComponents.SingleAsync()).ProtectedConfigurationJson;
        public async Task<DatabaseEngineStatusDto> EngineStatus()
        {
            var component = await Context().InstalledComponents.SingleAsync();
            return NewService().BuildEngineStatus(DatabaseProfiles.ResolveDescriptor(engine), component,
                DatabaseProfiles.ReadProtectedConfiguration(Protector, component));
        }
        public void RejectAdminWrites() => Context().Database.ExecuteSqlRaw("CREATE TRIGGER fixture_reject_admin_write BEFORE UPDATE ON installed_components BEGIN SELECT RAISE(ABORT, 'fixture-admin-store-unwritable'); END;");
        public void AllowAdminWrites() => Context().Database.ExecuteSqlRaw("DROP TRIGGER fixture_reject_admin_write;");
        public Task Change(string password = "new-secret", CancellationToken token = default) => Service.SetUserPasswordAsync(new SetDatabaseUserPasswordRequest
            { Engine = "mysql", UserName = "Shared", AllowedHost = "localhost", Password = password }, token);
        public async Task Seed(bool panelCreated = false)
        {
            var credentials = NewCredentials();
            if (panelCreated)
            {
                await credentials.RecordCreatedAsync("mysql", "Shared", "localhost", "db_a", "old-secret", default);
                await credentials.ConfirmReferenceAsync("mysql", "Shared", "localhost", "db_a", default);
            }
            else await credentials.UpsertAsync("mysql", "Shared", "localhost", "db_a", "old-secret", default);
            await credentials.BeginReferenceAsync("mysql", "Shared", "localhost", "db_b", default);
            await credentials.ConfirmReferenceAsync("mysql", "Shared", "localhost", "db_b", default);
        }
        public void Dispose()
        {
            foreach (var db in contexts) db.Dispose();
            connection.Dispose();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private sealed class Protector : ISecretProtector
    {
        public bool FailProtection;
        public string Protect(string value) => FailProtection ? throw new IOException("fixture-protection-failed") : value;
        public string Unprotect(string value) => value;
    }

    private sealed class Provider : IDatabaseEngineProvider
    {
        public DbEngine Engine => DbEngine.MySql;
        public string Password = "old-secret";
        public string RootPassword = string.Empty;
        public int PasswordCalls;
        public int RootPasswordCalls;
        public int WorkspaceCalls;
        public int VerificationCalls;
        public Action? BeforePassword;
        public Action? AfterPassword;
        public Action? BeforeRootPassword;
        public Action? AfterRootPassword;
        public Action? BeforeWorkspace;
        public bool VerificationFailure;
        public Task<DatabaseWorkspaceDto> GetWorkspaceAsync(DatabaseConnectionProfile profile, CancellationToken token)
        { token.ThrowIfCancellationRequested(); WorkspaceCalls++; BeforeWorkspace?.Invoke(); token.ThrowIfCancellationRequested(); return Task.FromResult(new DatabaseWorkspaceDto()); }
        public Task CreateDatabaseAsync(DatabaseConnectionProfile profile, string database, CancellationToken token) => Task.CompletedTask;
        public Task DeleteDatabaseAsync(DatabaseConnectionProfile profile, string database, CancellationToken token) => Task.CompletedTask;
        public Task CreateUserAsync(DatabaseConnectionProfile profile, string user, string password, string host, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Password = password; return Task.CompletedTask; }
        public Task DeleteUserAsync(DatabaseConnectionProfile profile, string user, string host, CancellationToken token) => Task.CompletedTask;
        public Task GrantAccessAsync(DatabaseConnectionProfile profile, string database, string user, string host, IReadOnlyCollection<string> privileges, CancellationToken token) => Task.CompletedTask;
        public Task SetUserPasswordAsync(DatabaseConnectionProfile profile, string user, string password, string host, CancellationToken token)
        { token.ThrowIfCancellationRequested(); PasswordCalls++; BeforePassword?.Invoke(); Password = password; AfterPassword?.Invoke(); return Task.CompletedTask; }
        public Task SetRootPasswordAsync(DatabaseConnectionProfile profile, string password, CancellationToken token)
        { token.ThrowIfCancellationRequested(); RootPasswordCalls++; BeforeRootPassword?.Invoke(); RootPassword = password; AfterRootPassword?.Invoke(); return Task.CompletedTask; }
        public Task<bool> VerifyAdminPasswordAsync(DatabaseConnectionProfile profile, CancellationToken token)
        { token.ThrowIfCancellationRequested(); VerificationCalls++; return VerificationFailure ? throw new IOException("fixture-verification-unavailable") : Task.FromResult(profile.AdminPassword == RootPassword); }
    }
}
