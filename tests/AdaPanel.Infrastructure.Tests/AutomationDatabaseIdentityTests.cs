using System.Reflection;
using AdaPanel.Core.Dtos.Backup;
using AdaPanel.Core.Dtos.Databases;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Automation;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.RunningJobs;
using AdaPanel.Infrastructure.Services;
using AdaPanel.Infrastructure.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class AutomationDatabaseIdentityTests
{
    [Theory]
    [InlineData("postgresql", "foo", "postgresql", "Foo", 1)]
    [InlineData("postgres", "foo", "postgresql", "foo", 0)]
    [InlineData("postgresql", "foo", "postgresql", "foo", 0)]
    [InlineData("mysql", "foo", "mysql", "FOO", 0)]
    [InlineData("mysql", "foo", "postgresql", "foo", 1)]
    public async Task ScheduledBackup_SkipsOnlyTheSameDatabaseAlreadyInSiteArchive(
        string siteEngine, string siteDatabase, string workspaceEngine, string workspaceDatabase, int expectedStandalone)
    {
        using var fixture = new Fixture(siteEngine, siteDatabase, workspaceEngine, workspaceDatabase);
        await fixture.Configure(workspaceEngine);
        var status = await fixture.Service.RunBackupNowAsync();
        Assert.Equal("completed", status.LastBackupStatus);
        var siteRequest = Assert.Single(fixture.SiteBackups);
        Assert.Equal(siteDatabase, Assert.Single(siteRequest.Databases).DatabaseName);
        Assert.Equal(expectedStandalone, fixture.DatabaseBackups.Count);
        if (expectedStandalone == 1)
        {
            Assert.Equal(workspaceDatabase, Assert.Single(fixture.DatabaseBackups).DatabaseName);
        }
    }

    [Fact]
    public async Task FailedSiteBackup_DoesNotSuppressStandaloneBackupOfThatDatabase()
    {
        using var fixture = new Fixture("postgresql", "foo", "postgresql", "foo", failSite: true);
        await fixture.Configure("postgresql");
        var status = await fixture.Service.RunBackupNowAsync();
        Assert.Equal("completed_with_errors", status.LastBackupStatus);
        Assert.Equal("foo", Assert.Single(fixture.DatabaseBackups).DatabaseName);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), $"adapanel-automation-db-identity-{Guid.NewGuid():N}");
        private readonly JsonFileStore store;
        public AutomationService Service { get; }
        public List<BackupSiteRequest> SiteBackups { get; } = [];
        public List<BackupDatabaseRequest> DatabaseBackups { get; } = [];

        public Fixture(string siteEngine, string siteDatabase, string workspaceEngine, string workspaceDatabase, bool failSite = false)
        {
            Directory.CreateDirectory(root);
            store = new JsonFileStore(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = root }));
            var jobs = new RunningJobService(store, NullLogger<RunningJobService>.Instance);
            var backups = Fake<IBackupService>((method, args) =>
            {
                switch (method.Name)
                {
                    case "BackupSiteAsync":
                        var siteRequest = (BackupSiteRequest)args[0]!;
                        SiteBackups.Add(siteRequest);
                        return failSite ? Task.FromException<BackupRecordDto>(new IOException("fake site failure"))
                            : Task.FromResult(AutomationBackupOutcomeTests.Result("site", siteRequest.SiteName, siteRequest.Databases));
                    case "BackupDatabaseAsync":
                        var databaseRequest = (BackupDatabaseRequest)args[0]!;
                        DatabaseBackups.Add(databaseRequest);
                        return Task.FromResult(AutomationBackupOutcomeTests.Result("database", $"{databaseRequest.Engine}:{databaseRequest.DatabaseName}",
                            [new BackupDatabaseRef { Engine = databaseRequest.Engine, DatabaseName = databaseRequest.DatabaseName }]));
                    case "PruneAsync":
                        return Task.FromResult(new BackupPruneResultDto());
                    default:
                        throw new NotSupportedException(method.Name);
                }
            });
            var sites = Fake<IIisSiteService>((method, _) => method.Name == "GetSitesAsync"
                ? Task.FromResult<IReadOnlyCollection<SiteDto>>([new() { Name = "site" }])
                : throw new NotSupportedException(method.Name));
            var ownership = Fake<IDatabaseOwnershipService>((method, _) => method.Name == "GetAllAsync"
                ? Task.FromResult<IReadOnlyCollection<(string, string, Guid, string)>>([(siteEngine, siteDatabase, Guid.NewGuid(), "site")])
                : throw new NotSupportedException(method.Name));
            var databases = Fake<IDatabaseService>((method, _) => method.Name == "GetWorkspaceAsync"
                ? Task.FromResult(new DatabaseWorkspaceDto { Engine = workspaceEngine, Databases = [new() { Name = workspaceDatabase }] })
                : throw new NotSupportedException(method.Name));
            Service = new AutomationService(store, backups, null!, null!, sites, null!, null!, null!, databases,
                null!, null!, null!, null!, null!, null!, null!, new ConcurrencyLock(), null!, ownership, jobs,
                NullLogger<AutomationService>.Instance);
        }

        public Task Configure(string engine) => store.WriteAsync("config/automation.json", new
        {
            autoBackupEnabled = true, backupSites = true, backupDatabases = true,
            backupMail = false, backupPanelConfig = false, backupDatabaseEngines = new[] { engine },
            backupTargetType = "local", autoSslRenewEnabled = false, updateCheckEnabled = false
        });

        public void Dispose()
        {
            var absolute = Path.GetFullPath(root);
            Assert.StartsWith(Path.Combine(Path.GetTempPath(), "adapanel-automation-db-identity-"), absolute, StringComparison.OrdinalIgnoreCase);
            Directory.Delete(absolute, recursive: true);
        }
    }

    private static T Fake<T>(Func<MethodInfo, object?[], object?> handler) where T : class
    {
        var proxy = DispatchProxy.Create<T, PostgresDatabaseIdentityConsumersTests.StrictProxy>();
        ((PostgresDatabaseIdentityConsumersTests.StrictProxy)(object)proxy).Handler = handler;
        return proxy;
    }
}
