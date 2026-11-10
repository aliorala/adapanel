using System.Text.Json;
using System.Security.AccessControl;
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

public sealed class MigrationDatabaseImporterTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "AdaPanel.MigrationImport." + Guid.NewGuid().ToString("N"));
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly AdaPanelDbContext db;
    private readonly Provider provider = new();
    private readonly Client client = new();
    private readonly DatabaseService service;
    private readonly DatabaseCredentialStore credentials;
    private string Dump => Path.Combine(root, "source.sql");
    public MigrationDatabaseImporterTests()
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "mysql.exe"), "fixture-only-never-executed");
        File.WriteAllText(Dump, "CREATE TABLE t (id int);");
        connection.Open();
        db = new(new DbContextOptionsBuilder<AdaPanelDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();
        foreach (var component in new[] { "mysql-8", "mariadb-11" })
            db.InstalledComponents.Add(new InstalledComponent
            {
                ComponentId = component, Status = ComponentInstallStatus.Installed, InstallDir = root,
                ProtectedConfigurationJson = JsonSerializer.Serialize(new Dictionary<string, string> { [DatabaseProfiles.GeneratedPasswordKey] = "ADMIN-SECRET" })
            });
        db.SaveChanges();
        var files = new JsonFileStore(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = root }));
        credentials = new(files, new Protector());
        service = new(db, new Protector(), credentials, new SiteDatabaseLinkService(files), [provider]) { MigrationClient = client };
    }
    private MigrationDatabaseImportRequest Request(string engine = "mysql", bool adapt = true) => new()
    {
        Engine = engine, TargetDatabase = "fresh_database", TargetUserName = "fresh_user",
        TargetPassword = "TARGET-SECRET", DumpPath = Dump, AdaptDefiners = adapt
    };

    [Theory]
    [InlineData("mysql")]
    [InlineData("mariadb")]
    public async Task ImportUsesOnlyFreshScopedUserAndPublishesVerifiedInventory(string engine)
    {
        var result = await service.ImportAsync(Request(engine));
        Assert.True(result.Imported); Assert.True(result.Verified); Assert.Empty(result.ErrorCode);
        Assert.Equal(["BASE TABLE:t"], result.ObjectInventory);
        Assert.Equal("fresh_user", client.Profile!.AdminUserName);
        Assert.Equal("TARGET-SECRET", client.Profile.AdminPassword);
        Assert.Equal("localhost", provider.CreatedHost);
        Assert.DoesNotContain("ALL PRIVILEGES", provider.Privileges);
        Assert.DoesNotContain("FILE", provider.Privileges);
        Assert.DoesNotContain("CREATE USER", provider.Privileges);
        Assert.DoesNotContain("SUPER", provider.Privileges);
        Assert.Equal(1, client.Calls);
        Assert.NotNull(await credentials.FindForDatabaseAsync(engine, "fresh_database", default));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task ExistingTargetsBlockPlanAndApplyWithoutAnyMutations(bool database, bool user)
    {
        provider.DatabaseExists = database; provider.UserExists = user;
        Assert.False((await service.PlanAsync(Request())).CanImport);
        var result = await service.ImportAsync(Request());
        Assert.False(result.Imported); Assert.False(result.Verified);
        Assert.Empty(provider.Mutations); Assert.Equal(0, client.Calls);
    }

    [Fact]
    public async Task ApplyRechecksAfterSuccessfulPlan()
    {
        Assert.True((await service.PlanAsync(Request())).CanImport);
        provider.DatabaseExists = true;
        Assert.Equal("migration.target_database_exists", (await service.ImportAsync(Request())).ErrorCode);
        Assert.Empty(provider.Mutations);
    }

    [Fact]
    public async Task ApplyWaitsForExistingResourceLeaseBeforeReadingOrMutating()
    {
        using var lease = await DatabaseLifecycle.EnterResourceAsync("mysql", "fresh_database");
        using var cancel = new CancellationTokenSource();
        var operation = service.ImportAsync(Request(), cancel.Token);
        Assert.False(operation.IsCompleted); Assert.Empty(provider.Mutations);
        cancel.Cancel();
        var result = await operation.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(result.Cancelled); Assert.False(result.PartialStatePossible);
        Assert.Empty(result.AttemptedResources); Assert.Empty(provider.Mutations);
    }

    [Fact]
    public async Task GeneratedPasswordIsPublishedOnlyInDedicatedCredentialPayload()
    {
        var result = await service.ImportAsync(new()
        {
            Engine = "mysql", TargetDatabase = "fresh_database", TargetUserName = "fresh_user", DumpPath = Dump
        });
        Assert.True(result.Verified); Assert.NotNull(result.Credentials);
        Assert.True(result.Credentials.Password.Length >= 48);
        Assert.Equal(result.Credentials.Password, client.Profile!.AdminPassword);
        Assert.DoesNotContain(result.Credentials.Password, result.ErrorCode + string.Join(',', result.CreatedResources));
    }

    [Fact]
    public async Task AtomicCreateCollisionCannotGrantOrImportIntoAnExistingDatabase()
    {
        provider.CreateCollision = true;
        var result = await service.ImportAsync(Request());
        Assert.False(result.Imported); Assert.False(result.Verified);
        Assert.Equal(["user:fresh_user@localhost"], result.CreatedResources);
        Assert.Equal(["create-user", "create-database"], provider.Mutations);
        Assert.Equal(0, client.Calls);
    }

    [Fact]
    public async Task FailedClientNeverRetriesAndNeverReturnsRawErrorsOrSecretsInDetails()
    {
        File.WriteAllText(Dump, "USE other; CREATE USER evil; SELECT 'TARGET-SECRET';");
        client.Exit = 1;
        var result = await service.ImportAsync(Request());
        Assert.False(result.Imported); Assert.False(result.Verified); Assert.Equal(1, client.Calls);
        Assert.Equal("migration.database_import_failed", result.ErrorCode);
        Assert.DoesNotContain("SECRET", result.ErrorCode + string.Join(',', result.CreatedResources));
        Assert.Equal(2, result.CreatedResources.Count);
    }

    [Fact]
    public async Task FailedVerificationCannotBecomeSuccess()
    {
        client.VerifyFailure = true;
        var result = await service.ImportAsync(Request());
        Assert.True(result.Imported); Assert.False(result.Verified); Assert.NotEmpty(result.ErrorCode);
        Assert.Empty(result.ObjectInventory); Assert.Equal(1, client.Calls);
    }

    [Fact]
    public async Task CancellationRetainsPartialResourcesAndDoesNotRetry()
    {
        client.Cancel = true;
        var result = await service.ImportAsync(Request());
        Assert.True(result.Cancelled); Assert.False(result.Verified); Assert.False(result.Imported);
        Assert.Equal(2, result.CreatedResources.Count); Assert.Equal(1, client.Calls);
        Assert.True(result.PartialStatePossible); Assert.Equal(2, result.AttemptedResources.Count);
        Assert.NotNull(result.Credentials);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DefinerAdaptationUsesExistingLexerAndPreservesSource(bool adapt)
    {
        var sql = "CREATE DEFINER=`root`@`localhost` VIEW v AS SELECT 1;";
        File.WriteAllText(Dump, sql);
        var result = await service.ImportAsync(Request(adapt: adapt));
        Assert.True(result.Verified);
        Assert.Equal(sql, File.ReadAllText(Dump));
        Assert.Equal(adapt, client.Input!.Contains("CURRENT_USER", StringComparison.OrdinalIgnoreCase));
        if (adapt) Assert.DoesNotContain("`root`@`localhost`", client.Input);
        if (adapt && OperatingSystem.IsWindows()) Assert.True(client.InputAclProtected);
    }

    [Theory]
    [InlineData("NO_BACKSLASH_ESCAPES")]
    [InlineData("ANSI_QUOTES")]
    public async Task UnsafeSqlModeCannotExecuteDump(string mode)
    {
        client.Mode = mode;
        var result = await service.ImportAsync(Request());
        Assert.False(result.Imported); Assert.Equal(0, client.Calls); Assert.NotEmpty(result.ErrorCode);
    }

    [Theory]
    [InlineData("postgresql")]
    [InlineData("mssql")]
    public async Task UnsupportedEngineNeverUsesLegacyAdminImport(string engine)
    {
        Assert.False((await service.PlanAsync(Request(engine))).Supported);
        Assert.Equal("migration.database_engine_unsupported", (await service.ImportAsync(Request(engine))).ErrorCode);
        Assert.Empty(provider.Mutations); Assert.Equal(0, client.Calls);
    }

    [Fact]
    public void NativeArgumentsDisableLocalCommandsReconnectDefaultsAndNeverContainPassword()
    {
        var profile = new DatabaseConnectionProfile(DbEngine.MySql, "mysql", "MySQL", "mysql-8", "", "127.0.0.1", 3307, "fresh_user", "TARGET-SECRET", true, root);
        var args = MigrationMySqlClient.Arguments(profile, "fresh_database");
        Assert.StartsWith("--no-defaults ", args);
        Assert.Contains("--binary-mode", args); Assert.Contains("--local-infile=0", args);
        Assert.Contains("--skip-reconnect", args); Assert.DoesNotContain("--force", args);
        Assert.DoesNotContain("SECRET", args); Assert.DoesNotContain("source", args);
    }

    public void Dispose()
    {
        db.Dispose(); connection.Dispose(); Directory.Delete(root, true);
    }
    private sealed class Protector : ISecretProtector
    {
        public string Protect(string value) => value;
        public string Unprotect(string value) => value;
    }
    private sealed class Client : IMigrationMySqlClient
    {
        public int Calls, Exit;
        public bool Cancel, VerifyFailure;
        public bool InputAclProtected;
        public string Mode = "", Input = "";
        public DatabaseConnectionProfile? Profile;
        public Task<string> ReadSqlModeAsync(DatabaseConnectionProfile profile, string database, CancellationToken token) => Task.FromResult(Mode);
        public Task<int> ImportAsync(string executable, DatabaseConnectionProfile profile, string database, string dump, CancellationToken token)
        {
            Calls++; Profile = profile; Input = File.ReadAllText(dump);
            if (OperatingSystem.IsWindows()) InputAclProtected = new FileInfo(dump).GetAccessControl().AreAccessRulesProtected;
            if (Cancel) throw new OperationCanceledException();
            return Task.FromResult(Exit);
        }
        public Task<IReadOnlyList<string>> VerifyAsync(DatabaseConnectionProfile profile, string database, CancellationToken token) =>
            VerifyFailure ? throw new InvalidDataException("TARGET-SECRET") : Task.FromResult<IReadOnlyList<string>>(["BASE TABLE:t"]);
    }
    private sealed class Provider : IDatabaseEngineProvider
    {
        public DbEngine Engine => DbEngine.MySql;
        public bool DatabaseExists, UserExists, CreateCollision;
        public string CreatedHost = "";
        public List<string> Mutations = [];
        public IReadOnlyCollection<string> Privileges = [];
        public Task<DatabaseWorkspaceDto> GetWorkspaceAsync(DatabaseConnectionProfile profile, CancellationToken token) =>
            Task.FromResult(new DatabaseWorkspaceDto
            {
                Databases = DatabaseExists ? [new() { Name = "FRESH_DATABASE" }] : [],
                Users = UserExists ? [new() { UserName = "FRESH_USER", Host = "%" }] : []
            });
        public Task CreateDatabaseAsync(DatabaseConnectionProfile profile, string database, CancellationToken token)
        {
            Mutations.Add("create-database");
            if (CreateCollision) throw new AppException("database.database_already_exists", 409, 409);
            DatabaseExists = true; return Task.CompletedTask;
        }
        public Task CreateUserAsync(DatabaseConnectionProfile profile, string user, string password, string host, CancellationToken token)
        { Mutations.Add("create-user"); UserExists = true; CreatedHost = host; return Task.CompletedTask; }
        public Task GrantAccessAsync(DatabaseConnectionProfile profile, string database, string user, string host, IReadOnlyCollection<string> privileges, CancellationToken token)
        { Mutations.Add("grant"); Privileges = privileges; return Task.CompletedTask; }
        public Task DeleteDatabaseAsync(DatabaseConnectionProfile p, string d, CancellationToken t) => throw new InvalidOperationException("MUST NEVER DROP");
        public Task DeleteUserAsync(DatabaseConnectionProfile p, string u, string h, CancellationToken t) => throw new InvalidOperationException("MUST NEVER DROP");
        public Task SetUserPasswordAsync(DatabaseConnectionProfile p, string u, string s, string h, CancellationToken t) => throw new InvalidOperationException("MUST NEVER ALTER PASSWORD");
    }
}
