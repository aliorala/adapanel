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

public sealed class DatabaseCreationRollbackTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "AdaPanel.CreateRollback." + Guid.NewGuid().ToString("N"));
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly AdaPanelDbContext db;
    private readonly JsonFileStore files;
    private readonly Protector protector = new();
    private readonly DatabaseCredentialStore credentials;
    private readonly Links links;
    private readonly Provider provider = new();
    private readonly DatabaseService service;
    private readonly CancellationTokenSource requestCancellation = new();
    private const string Password = "Fixture-create-secret!";

    public DatabaseCreationRollbackTests()
    {
        connection.Open();
        db = new AdaPanelDbContext(new DbContextOptionsBuilder<AdaPanelDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();
        db.InstalledComponents.Add(new InstalledComponent
        {
            ComponentId = "mysql-8", Status = ComponentInstallStatus.Installed,
            ProtectedConfigurationJson = JsonSerializer.Serialize(new Dictionary<string, string> { [DatabaseProfiles.GeneratedPasswordKey] = "fixture-admin" })
        });
        db.SaveChanges();
        files = new JsonFileStore(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = root }));
        credentials = new DatabaseCredentialStore(files, protector);
        links = new Links(new SiteDatabaseLinkService(files));
        service = new DatabaseService(db, protector, credentials, links, [provider]);
    }

    private static CreateDatabaseRequest Request(bool withUser = true) => new()
    {
        Engine = "mysql", Name = "new_database", UserName = withUser ? "new_user" : "",
        Password = withUser ? Password : "", SiteName = "new.example"
    };

    [Theory]
    [InlineData("grant")]
    [InlineData("credential")]
    [InlineData("link")]
    [InlineData("workspace")]
    public async Task EveryFailureAfterCreationRemovesOnlyTheResourcesCreatedByThisCall(string failure)
    {
        await SeedUnrelated();
        SetFailure(failure);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateDatabaseAsync(Request()));
        Assert.Equal("fixture-" + failure, error.Message);
        await AssertRolledBack();
        Assert.Equal("other-secret", (await credentials.FindForDatabaseAsync("mysql", "other_database", default))?.Password);
        Assert.Equal("other.example", Assert.Single(await links.GetForSiteAsync("other.example")).SiteName);
    }

    [Fact]
    public async Task AnExistingUserIsNeverDeletedWhenCreateUserFails()
    {
        provider.UserExists = true; provider.Failure = "user";
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateDatabaseAsync(Request()));
        Assert.False(provider.DatabaseExists); Assert.True(provider.UserExists); Assert.Equal(0, provider.UserDeleteCalls);
    }

    [Fact]
    public async Task InvalidUserIsRejectedBeforeDatabaseCreation()
    {
        var request = new CreateDatabaseRequest { Engine = "mysql", Name = "new_database", UserName = new string('a', 33), Password = Password };
        var error = await Assert.ThrowsAsync<AppException>(() => service.CreateDatabaseAsync(request));
        Assert.Equal("database.user_name_too_long", error.MessageKey);
        Assert.False(provider.DatabaseExists); Assert.False(provider.UserExists); Assert.Equal(0, provider.DatabaseDeleteCalls);
    }

    [Fact]
    public async Task OwnershipRecordingFailureRollsBackTheCreatedResources()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => ((IDatabaseService)service).CreateDatabaseAsync(
            Request(), _ => Task.CompletedTask, _ => throw new InvalidOperationException("fixture-ownership")));
        Assert.Equal("fixture-ownership", error.Message);
        await AssertRolledBack();
    }

    [Fact]
    public async Task ARejectedFinalAuthorityCheckHasNoProviderSideEffects()
    {
        var afterCalled = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => ((IDatabaseService)service).CreateDatabaseAsync(Request(),
            _ => throw new InvalidOperationException("fixture-authority"), _ => { afterCalled = true; return Task.CompletedTask; }));
        Assert.False(provider.DatabaseExists); Assert.False(provider.UserExists); Assert.False(afterCalled);
        Assert.Equal(0, provider.DatabaseDeleteCalls);
    }

    [Theory]
    [InlineData("grant")]
    [InlineData("link")]
    [InlineData("workspace")]
    public async Task RequestCancellationDoesNotCancelCompensation(string step)
    {
        provider.BeforeStep = stage => { if (stage == step) requestCancellation.Cancel(); };
        links.BeforeLink = () => { if (step == "link") requestCancellation.Cancel(); };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.CreateDatabaseAsync(Request(), requestCancellation.Token));
        await AssertRolledBack();
        Assert.All(provider.CleanupTokens, token => Assert.False(token.IsCancellationRequested));
    }

    [Theory]
    [InlineData("link")]
    [InlineData("workspace")]
    public async Task ADatabaseWithoutANewUserIsAlsoRolledBack(string failure)
    {
        SetFailure(failure);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateDatabaseAsync(Request(false)));
        Assert.False(provider.DatabaseExists); Assert.Equal(0, provider.UserDeleteCalls);
        Assert.Empty(await links.GetForSiteAsync("new.example"));
    }

    [Fact]
    public async Task FailedCreateDatabaseDoesNotDeleteAnExistingDatabaseOrItsCredentials()
    {
        provider.DatabaseExists = true; provider.Failure = "database";
        await credentials.UpsertAsync("mysql", "existing_user", "localhost", "new_database", "existing-secret", default);
        await links.Inner.LinkAsync("mysql", "new_database", "existing.example");
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateDatabaseAsync(Request()));
        Assert.True(provider.DatabaseExists); Assert.Equal(0, provider.DatabaseDeleteCalls); Assert.Equal(0, provider.UserDeleteCalls);
        Assert.Equal("existing-secret", (await credentials.FindForDatabaseAsync("mysql", "new_database", default))?.Password);
        Assert.Single(await links.GetForSiteAsync("existing.example"));
    }

    [Fact]
    public async Task FailedDatabaseCompensationIsReportedAndKeepsRecoveryMetadata()
    {
        provider.Failure = "workspace"; provider.CleanupFailure = "database";
        var error = await Assert.ThrowsAsync<AppException>(() => service.CreateDatabaseAsync(Request()));
        Assert.Equal("database.create_cleanup_incomplete", error.MessageKey);
        Assert.Contains("database", error.Details, StringComparison.Ordinal);
        Assert.DoesNotContain(Password, error.Details, StringComparison.Ordinal);
        Assert.True(provider.DatabaseExists); Assert.True(provider.UserExists);
        Assert.NotNull(await credentials.FindForDatabaseAsync("mysql", "new_database", default));
        Assert.Single(await links.GetForSiteAsync("new.example"));
    }

    [Fact]
    public async Task AUserCleanupFailureDoesNotPreventMetadataCleanup()
    {
        provider.Failure = "workspace"; provider.CleanupFailure = "user";
        var error = await Assert.ThrowsAsync<AppException>(() => service.CreateDatabaseAsync(Request()));
        Assert.Equal("database.create_cleanup_incomplete", error.MessageKey);
        Assert.False(provider.DatabaseExists); Assert.True(provider.UserExists);
        Assert.Null(await credentials.FindForDatabaseAsync("mysql", "new_database", default));
        Assert.Empty(await links.GetForSiteAsync("new.example"));
    }

    [Fact]
    public async Task ALinkCleanupFailureDoesNotPreventUserAndCredentialCleanup()
    {
        provider.Failure = "workspace"; links.FailUnlink = true;
        var error = await Assert.ThrowsAsync<AppException>(() => service.CreateDatabaseAsync(Request()));
        Assert.Equal("database.create_cleanup_incomplete", error.MessageKey);
        Assert.False(provider.DatabaseExists); Assert.False(provider.UserExists);
        Assert.Null(await credentials.FindForDatabaseAsync("mysql", "new_database", default));
        Assert.Single(await links.GetForSiteAsync("new.example"));
    }

    [Fact]
    public async Task ADatabaseAlreadyGoneAllowsRemainingCompensation()
    {
        provider.Failure = "workspace"; provider.CleanupFailure = "gone";
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateDatabaseAsync(Request()));
        await AssertRolledBack();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SuccessfulCreationKeepsItsDatabaseLinkAndOptionalAccount(bool withUser)
    {
        var workspace = await service.CreateDatabaseAsync(Request(withUser));
        Assert.Equal("mysql", workspace.Engine); Assert.True(provider.DatabaseExists);
        Assert.Equal(withUser, provider.UserExists); Assert.Equal(0, provider.DatabaseDeleteCalls);
        Assert.Single(await links.GetForSiteAsync("new.example"));
        Assert.Equal(withUser, await credentials.FindForDatabaseAsync("mysql", "new_database", default) is not null);
    }

    [Fact]
    public async Task CancellationAfterConfirmedDropStillRemovesMetadataAndOwnership()
    {
        await service.CreateDatabaseAsync(Request());
        provider.AfterDatabaseDelete = requestCancellation.Cancel;
        var ownershipRemoved = false;
        await service.DeleteDatabaseAsync("mysql", "new_database", _ => Task.CompletedTask, token =>
        {
            Assert.False(token.IsCancellationRequested);
            ownershipRemoved = true;
            return Task.CompletedTask;
        }, requestCancellation.Token);
        await AssertRolledBack();
        Assert.True(ownershipRemoved);
    }

    [Theory]
    [InlineData("user")]
    [InlineData("link")]
    public async Task FailedDeleteCleanupDoesNotSuppressIndependentCleanupOrOwnershipRemoval(string failure)
    {
        await service.CreateDatabaseAsync(Request());
        provider.CleanupFailure = failure == "user" ? "user" : "";
        links.FailUnlink = failure == "link";
        var ownershipRemoved = false;
        var error = await Assert.ThrowsAsync<AppException>(() => service.DeleteDatabaseAsync("mysql", "new_database",
            _ => Task.CompletedTask, _ => { ownershipRemoved = true; return Task.CompletedTask; }));
        Assert.Equal("database.delete_cleanup_incomplete", error.MessageKey);
        Assert.False(provider.DatabaseExists);
        Assert.True(ownershipRemoved);
        Assert.Null(await credentials.FindForDatabaseAsync("mysql", "new_database", default));
        Assert.Equal(failure == "user", provider.UserExists);
        Assert.Equal(failure == "link" ? 1 : 0, (await links.GetForSiteAsync("new.example")).Count);
    }

    [Fact]
    public async Task AFailedDropPreservesRecoveryMetadataAndDoesNotRemoveOwnership()
    {
        await service.CreateDatabaseAsync(Request());
        provider.CleanupFailure = "database";
        var ownershipRemoved = false;
        await Assert.ThrowsAsync<IOException>(() => service.DeleteDatabaseAsync("mysql", "new_database",
            _ => Task.CompletedTask, _ => { ownershipRemoved = true; return Task.CompletedTask; }));
        Assert.True(provider.DatabaseExists); Assert.True(provider.UserExists);
        Assert.False(ownershipRemoved);
        Assert.NotNull(await credentials.FindForDatabaseAsync("mysql", "new_database", default));
        Assert.Single(await links.GetForSiteAsync("new.example"));
    }

    [Fact]
    public async Task AlreadyGoneDatabaseStillRemovesMetadataAndOwnership()
    {
        await service.CreateDatabaseAsync(Request());
        provider.CleanupFailure = "gone";
        var ownershipRemoved = false;
        await service.DeleteDatabaseAsync("mysql", "new_database", _ => Task.CompletedTask,
            _ => { ownershipRemoved = true; return Task.CompletedTask; });
        await AssertRolledBack();
        Assert.True(ownershipRemoved);
    }

    [Fact]
    public async Task OwnershipCleanupFailureIsExplicitAfterOtherResourcesAreRemoved()
    {
        await service.CreateDatabaseAsync(Request());
        var error = await Assert.ThrowsAsync<AppException>(() => service.DeleteDatabaseAsync("mysql", "new_database",
            _ => Task.CompletedTask, _ => throw new InvalidOperationException("fixture-ownership")));
        Assert.Equal("database.delete_cleanup_incomplete", error.MessageKey);
        Assert.Contains("ownership:InvalidOperationException", error.Details);
        Assert.DoesNotContain("fixture-ownership", error.Details);
        await AssertRolledBack();
    }

    [Fact]
    public async Task DeletePreservesAnAccountStillUsedByAnotherDatabase()
    {
        await service.CreateDatabaseAsync(Request());
        await credentials.UpsertAsync("mysql", "new_user", "localhost", "other_database", Password, default);
        await service.DeleteDatabaseAsync("mysql", "new_database");
        Assert.False(provider.DatabaseExists); Assert.True(provider.UserExists); Assert.Equal(0, provider.UserDeleteCalls);
        Assert.NotNull(await credentials.FindForDatabaseAsync("mysql", "other_database", default));
        Assert.Null(await credentials.FindForDatabaseAsync("mysql", "new_database", default));
    }

    [Fact]
    public async Task RejectedDeleteAuthorityNeverDropsTheDatabase()
    {
        await service.CreateDatabaseAsync(Request());
        var ownershipRemoved = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DeleteDatabaseAsync("mysql", "new_database",
            _ => throw new InvalidOperationException("fixture-authority"),
            _ => { ownershipRemoved = true; return Task.CompletedTask; }));
        Assert.True(provider.DatabaseExists); Assert.True(provider.UserExists);
        Assert.Equal(0, provider.DatabaseDeleteCalls); Assert.False(ownershipRemoved);
    }

    [Fact]
    public async Task ResourceLeaseCoversCreationThroughOwnershipRecording()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var deletionChecked = false;
        var create = service.CreateDatabaseAsync(Request(), _ => Task.CompletedTask, async _ =>
        {
            entered.SetResult();
            await release.Task;
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var delete = service.DeleteDatabaseAsync("mysql", "NEW_DATABASE", _ =>
        {
            deletionChecked = true;
            return Task.CompletedTask;
        }, _ => Task.CompletedTask);
        try { Assert.False(deletionChecked); Assert.False(delete.IsCompleted); }
        finally { release.TrySetResult(); }
        await create.WaitAsync(TimeSpan.FromSeconds(5));
        await delete.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(deletionChecked);
        Assert.False(provider.DatabaseExists);
    }

    [Fact]
    public async Task ResourceLeaseCoversDeletionThroughOwnershipRemoval()
    {
        await service.CreateDatabaseAsync(Request());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var creationChecked = false;
        var delete = service.DeleteDatabaseAsync("mysql", "new_database", _ => Task.CompletedTask, async _ =>
        {
            entered.SetResult();
            await release.Task;
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var create = service.CreateDatabaseAsync(Request(), _ =>
        {
            creationChecked = true;
            return Task.CompletedTask;
        }, _ => Task.CompletedTask);
        try { Assert.False(creationChecked); Assert.False(create.IsCompleted); }
        finally { release.TrySetResult(); }
        await delete.WaitAsync(TimeSpan.FromSeconds(5));
        await create.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(creationChecked);
        Assert.True(provider.DatabaseExists);
    }

    [Theory]
    [InlineData("")]
    [InlineData("new_database")]
    public async Task SqlImportCannotBypassAnotherDatabaseLeaseBySpecifyingAnInitialDatabase(string initialDatabase)
    {
        Directory.CreateDirectory(root);
        var dump = Path.Combine(root, "fixture.sql");
        await File.WriteAllTextAsync(dump, "USE other_database; SELECT 1;");
        using var otherDatabase = await DatabaseLifecycle.EnterResourceAsync("mysql", "other_database");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ImportDumpAsync(new DatabaseDumpRequest
        {
            Engine = "mysql", DatabaseName = initialDatabase, FilePath = dump
        }, cancellation.Token));
    }

    private async Task SeedUnrelated()
    {
        await credentials.UpsertAsync("mysql", "other_user", "localhost", "other_database", "other-secret", default);
        await links.Inner.LinkAsync("mysql", "other_database", "other.example");
    }

    private void SetFailure(string failure)
    {
        if (failure == "credential") protector.FailCredential = true;
        else if (failure == "link") links.FailAfterLink = true;
        else provider.Failure = failure;
    }

    private async Task AssertRolledBack()
    {
        Assert.False(provider.DatabaseExists); Assert.False(provider.UserExists);
        Assert.Null(await credentials.FindForDatabaseAsync("mysql", "new_database", default));
        Assert.Empty(await links.GetForSiteAsync("new.example"));
        Assert.Equal(1, provider.DatabaseDeleteCalls);
    }

    public void Dispose()
    {
        requestCancellation.Dispose(); db.Dispose(); connection.Dispose();
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    private sealed class Protector : ISecretProtector
    {
        public bool FailCredential;
        public string Protect(string value) => FailCredential && value == Password ? throw new InvalidOperationException("fixture-credential") : value;
        public string Unprotect(string value) => value;
    }

    private sealed class Links(SiteDatabaseLinkService inner) : ISiteDatabaseLinkService
    {
        public SiteDatabaseLinkService Inner => inner;
        public bool FailAfterLink, FailUnlink;
        public Action? BeforeLink;
        public async Task LinkAsync(string engine, string databaseName, string siteName, CancellationToken cancellationToken = default)
        {
            BeforeLink?.Invoke(); cancellationToken.ThrowIfCancellationRequested();
            await inner.LinkAsync(engine, databaseName, siteName, cancellationToken);
            if (FailAfterLink) throw new InvalidOperationException("fixture-link");
        }
        public Task UnlinkAsync(string engine, string databaseName, CancellationToken cancellationToken = default) =>
            FailUnlink ? throw new InvalidOperationException("fixture-unlink") : inner.UnlinkAsync(engine, databaseName, cancellationToken);
        public Task<IReadOnlyList<SiteDatabaseLinkDto>> GetForSiteAsync(string siteName, CancellationToken cancellationToken = default) => inner.GetForSiteAsync(siteName, cancellationToken);
        public Task<IReadOnlyList<SiteDatabaseLinkDto>> GetAllAsync(CancellationToken cancellationToken = default) => inner.GetAllAsync(cancellationToken);
        public Task RemoveSiteAsync(string siteName, CancellationToken cancellationToken = default) => inner.RemoveSiteAsync(siteName, cancellationToken);
    }

    private sealed class Provider : IDatabaseEngineProvider
    {
        public DbEngine Engine => DbEngine.MySql;
        public bool DatabaseExists, UserExists;
        public string Failure = "", CleanupFailure = "";
        public int DatabaseDeleteCalls, UserDeleteCalls;
        public Action<string>? BeforeStep;
        public Action? AfterDatabaseDelete;
        public List<CancellationToken> CleanupTokens = [];
        private void Step(string step, CancellationToken token)
        {
            BeforeStep?.Invoke(step); token.ThrowIfCancellationRequested();
            if (Failure == step) throw new InvalidOperationException("fixture-" + step);
        }
        public Task CreateDatabaseAsync(DatabaseConnectionProfile profile, string name, CancellationToken token)
        { Step("database", token); DatabaseExists = true; return Task.CompletedTask; }
        public Task CreateUserAsync(DatabaseConnectionProfile profile, string user, string password, string host, CancellationToken token)
        { Step("user", token); UserExists = true; return Task.CompletedTask; }
        public Task GrantAccessAsync(DatabaseConnectionProfile profile, string name, string user, string host, IReadOnlyCollection<string> privileges, CancellationToken token)
        { Step("grant", token); return Task.CompletedTask; }
        public Task<DatabaseWorkspaceDto> GetWorkspaceAsync(DatabaseConnectionProfile profile, CancellationToken token)
        { Step("workspace", token); return Task.FromResult(new DatabaseWorkspaceDto { Engine = "mysql" }); }
        public Task DeleteDatabaseAsync(DatabaseConnectionProfile profile, string name, CancellationToken token)
        {
            CleanupTokens.Add(token); DatabaseDeleteCalls++; token.ThrowIfCancellationRequested();
            if (CleanupFailure == "database") throw new IOException("fixture-database-cleanup");
            DatabaseExists = false;
            AfterDatabaseDelete?.Invoke();
            if (CleanupFailure == "gone") throw AppException.Gone("database.database_not_found");
            return Task.CompletedTask;
        }
        public Task DeleteUserAsync(DatabaseConnectionProfile profile, string user, string host, CancellationToken token)
        {
            CleanupTokens.Add(token); UserDeleteCalls++; token.ThrowIfCancellationRequested();
            if (CleanupFailure == "user") throw new IOException("fixture-user-cleanup");
            UserExists = false; return Task.CompletedTask;
        }
        public Task SetUserPasswordAsync(DatabaseConnectionProfile profile, string user, string password, string host, CancellationToken token) => throw new NotSupportedException();
    }
}
