using System.IO.Compression;
using System.Reflection;
using AdaPanel.Core.Dtos.Backup;
using AdaPanel.Core.Dtos.Databases;
using AdaPanel.Core.Dtos.Notifications;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Dtos.System;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Automation;
using AdaPanel.Infrastructure.Backup;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Services;
using AdaPanel.Infrastructure.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class AutomationBackupMeasurementTests
{
    [Theory]
    [InlineData("partial")]
    [InlineData("content-warning")]
    [InlineData("unsound")]
    [InlineData("unknown")]
    [InlineData("legacy-unknown")]
    [InlineData("missing")]
    [InlineData("deleted")]
    [InlineData("remote-missing")]
    [InlineData("failed")]
    [InlineData("zero-size")]
    [InlineData("future")]
    public async Task UnconfirmedRecentRecordDoesNotHideTheAgeOfTheLastHealthyBackup(string scenario)
    {
        using var fixture = new Fixture();
        var old = fixture.Archive("healthy", 40);
        var recent = fixture.Archive("recent", 0);
        switch (scenario)
        {
            case "partial": recent.Status = "created_with_errors"; break;
            case "content-warning": recent.Warnings.Add(new() { MessageKey = "backup.source_file_unreadable" }); break;
            case "unsound": recent.IntegrityState = BackupIntegrity.Unsound; break;
            case "unknown": recent.IntegrityState = BackupIntegrity.Unknown; break;
            case "legacy-unknown": recent.IntegrityState = BackupIntegrity.Unknown; recent.IncludedDatabases = null; break;
            case "missing": File.Delete(recent.Path); break;
            case "deleted": recent.Status = "deleted_remote_failed"; break;
            case "remote-missing":
                File.Delete(recent.Path);
                recent.RemoteUri = "https://backup.example.test/recent.zip";
                recent.RemoteMissing = true;
                recent.RemoteState = BackupPresence.Missing;
                break;
            case "failed": recent.Status = "failed"; break;
            case "zero-size": recent.Size = 0; break;
            case "future": recent.CreatedAtUtc = DateTime.UtcNow.AddDays(1); break;
        }
        await fixture.ConfigureHealth(old, recent);

        await fixture.Service.RunDueJobsAsync();

        var alert = Assert.Single(fixture.Notifications);
        Assert.Contains("yedekleme saglikli degil", alert.Subject);
        Assert.Contains($"{old.CreatedAtUtc:yyyy-MM-dd HH:mm} UTC", alert.Body);
    }

    [Fact]
    public async Task OnlyUnconfirmedRecordsAreNotDescribedAsSuccessfulBackups()
    {
        using var fixture = new Fixture();
        var partial = fixture.Archive("partial", 0);
        partial.Status = "created_with_errors";
        await fixture.ConfigureHealth(partial);
        await fixture.Service.RunDueJobsAsync();
        Assert.Contains("HIC BASARILI YEDEK YOK", Assert.Single(fixture.Notifications).Body);
    }

    [Theory]
    [InlineData("local")]
    [InlineData("remote-only")]
    [InlineData("advisory")]
    [InlineData("legacy-sound")]
    public async Task AMeasuredCompleteRecentCopyStillKeepsHealthQuiet(string scenario)
    {
        using var fixture = new Fixture();
        var complete = fixture.Archive("complete", 0);
        if (scenario == "remote-only")
        {
            File.Delete(complete.Path);
            complete.RemoteUri = "https://backup.example.test/complete.zip";
            complete.Status = "created_remote_synced";
            // Fresh upload records can predate an explicit remote presence probe.
            complete.RemoteState = BackupPresence.Unknown;
        }
        if (scenario == "advisory") complete.Warnings.Add(new() { MessageKey = "backup.compression_failed" });
        if (scenario == "legacy-sound") complete.IncludedDatabases = null;
        await fixture.ConfigureHealth(complete);
        await fixture.Service.RunDueJobsAsync();
        Assert.Empty(fixture.Notifications);
    }

    [Fact]
    public async Task AHealthyRecentIncrementalLinkDoesNotHideItsIncompleteBase()
    {
        using var fixture = new Fixture();
        var healthy = fixture.Archive("healthy", 40);
        var partialBase = fixture.Archive("base", 20);
        partialBase.ChainId = "partial-chain";
        partialBase.Status = "created_with_errors";
        var latest = fixture.Archive("latest", 0);
        latest.ChainId = partialBase.ChainId;
        latest.ChainIndex = 1;
        latest.BackupMode = "incremental";
        latest.BaseBackupId = partialBase.Id;
        await fixture.ConfigureHealth(healthy, partialBase, latest);
        await fixture.Service.RunDueJobsAsync();
        Assert.Contains($"{healthy.CreatedAtUtc:yyyy-MM-dd HH:mm} UTC", Assert.Single(fixture.Notifications).Body);
    }

    [Fact]
    public async Task AnAdminSiteIncludesItsUnownedLinkedDatabasesWithExactPostgresIdentity()
    {
        using var fixture = new Fixture();
        fixture.Links = [Link("postgres", "foo"), Link("postgresql", "foo"), Link("postgresql", "Foo")];
        await fixture.ConfigureBackup();
        var status = await fixture.Service.RunBackupNowAsync();
        Assert.Equal("completed", status.LastBackupStatus);
        Assert.Equal(["foo", "Foo"], Assert.Single(fixture.SiteBackups).Databases.Select(database => database.DatabaseName));
        Assert.All(fixture.SiteBackups[0].Databases, database => Assert.Equal("postgresql", database.Engine));
        Assert.Equal(1, fixture.SiteOwnerReads);
    }

    [Theory]
    [InlineData("other-site")]
    [InlineData("")]
    public async Task AnExplicitDatabaseOwnerCannotBeOverriddenByAnAdminSiteLink(string ownedSite)
    {
        using var fixture = new Fixture();
        fixture.Links = [Link("postgres", "foo")];
        fixture.Ownership = [("postgresql", "foo", Guid.NewGuid(), ownedSite)];
        await fixture.ConfigureBackup();
        await fixture.Service.RunBackupNowAsync();
        Assert.Empty(Assert.Single(fixture.SiteBackups).Databases);
    }

    [Fact]
    public async Task AnUnownedLinkDoesNotGrantDatabaseOwnershipToACustomerSite()
    {
        using var fixture = new Fixture();
        fixture.Links = [Link("postgresql", "foo")];
        fixture.SiteOwner = Guid.NewGuid();
        await fixture.ConfigureBackup();
        await fixture.Service.RunBackupNowAsync();
        Assert.Empty(Assert.Single(fixture.SiteBackups).Databases);
    }

    [Fact]
    public async Task AForeignLowercasePostgresOwnerDoesNotHideTheAdminUppercaseDatabase()
    {
        using var fixture = new Fixture();
        fixture.Links = [Link("postgresql", "Foo")];
        fixture.Ownership = [("postgres", "foo", Guid.NewGuid(), "other-site")];
        await fixture.ConfigureBackup();
        await fixture.Service.RunBackupNowAsync();
        Assert.Equal("Foo", Assert.Single(Assert.Single(fixture.SiteBackups).Databases).DatabaseName);
    }

    [Theory]
    [InlineData("site-owner")]
    [InlineData("links")]
    public async Task AFailedAuthorityReadCannotProduceAnApparentlyCompleteSiteBackup(string failure)
    {
        using var fixture = new Fixture();
        fixture.Links = [Link("postgresql", "foo")];
        fixture.FailAuthorityRead = failure;
        await fixture.ConfigureBackup();
        var result = await fixture.Service.RunBackupNowAsync();
        Assert.Equal("failed", result.LastBackupStatus);
        Assert.Empty(fixture.SiteBackups);
        Assert.Equal(1, fixture.PruneCalls); // Existing eligible points were pruned before inventory failed.
    }

    [Fact]
    public async Task ExistingTenantOwnershipRemainsIncludedWithoutDuplicateLinkDumps()
    {
        using var fixture = new Fixture();
        fixture.SiteOwner = Guid.NewGuid();
        fixture.Ownership = [("postgresql", "foo", fixture.SiteOwner.Value, "site")];
        fixture.Links = [Link("postgres", "foo")];
        await fixture.ConfigureBackup();
        var result = await fixture.Service.RunBackupNowAsync();
        Assert.Equal("completed", result.LastBackupStatus);
        Assert.Equal("foo", Assert.Single(Assert.Single(fixture.SiteBackups).Databases).DatabaseName);
    }

    private static SiteDatabaseLinkDto Link(string engine, string database) => new() { Engine = engine, DatabaseName = database, SiteName = "site" };

    private sealed class Fixture : IDisposable
    {
        private readonly ServiceProvider provider;
        private readonly JsonFileStore store;
        private readonly string root = Path.Combine(Path.GetTempPath(), $"adapanel-automation-measurement-{Guid.NewGuid():N}");
        public AutomationService Service { get; }
        public List<(string Subject, string Body)> Notifications { get; } = [];
        public List<BackupSiteRequest> SiteBackups { get; } = [];
        public IReadOnlyList<SiteDatabaseLinkDto> Links { get; set; } = [];
        public IReadOnlyCollection<(string Engine, string DatabaseName, Guid OwnerUserId, string SiteName)> Ownership { get; set; } = [];
        public Guid? SiteOwner { get; set; }
        public int SiteOwnerReads { get; private set; }
        public int PruneCalls { get; private set; }
        public string? FailAuthorityRead { get; set; }

        public Fixture()
        {
            Directory.CreateDirectory(root);
            store = new JsonFileStore(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = root }));
            var catalog = new BackupService(null!, null!, null!, null!, null!, store, null!);
            var dependencies = new Dictionary<Type, object>
            {
                [typeof(JsonFileStore)] = store,
                [typeof(IConcurrencyLock)] = new ConcurrencyLock(),
                [typeof(ILogger<AutomationService>)] = NullLogger<AutomationService>.Instance,
                [typeof(IRunningJobService)] = new AdaPanel.Infrastructure.RunningJobs.RunningJobService(store,
                    NullLogger<AdaPanel.Infrastructure.RunningJobs.RunningJobService>.Instance),
                [typeof(IBackupService)] = Fake<IBackupService>((method, args) => method.Name switch
                {
                    "GetBackupsAsync" => catalog.GetBackupsAsync(),
                    "BackupSiteAsync" => Backup((BackupSiteRequest)args[0]!),
                    "PruneAsync" => Prune(),
                    _ => throw new NotSupportedException(method.Name)
                }),
                [typeof(IIisSiteService)] = Fake<IIisSiteService>((method, _) => method.Name == "GetSitesAsync"
                    ? Task.FromResult<IReadOnlyCollection<SiteDto>>([new() { Name = "site" }]) : throw new NotSupportedException(method.Name)),
                [typeof(IDatabaseOwnershipService)] = Fake<IDatabaseOwnershipService>((method, _) => method.Name == "GetAllAsync"
                    ? Task.FromResult(Ownership) : throw new NotSupportedException(method.Name)),
                [typeof(ISiteDatabaseLinkService)] = Fake<ISiteDatabaseLinkService>((method, _) => method.Name == "GetAllAsync"
                    ? FailAuthorityRead == "links" ? Task.FromException<IReadOnlyList<SiteDatabaseLinkDto>>(new IOException("links unavailable")) : Task.FromResult(Links)
                    : throw new NotSupportedException(method.Name)),
                [typeof(ICustomerScopeService)] = Fake<ICustomerScopeService>((method, _) => method.Name == "GetSiteOwnerUserIdAsync"
                    ? ReadSiteOwner() : throw new NotSupportedException(method.Name)),
                [typeof(ISystemMonitoringService)] = Fake<ISystemMonitoringService>((method, _) => method.Name switch
                {
                    "GetOverviewAsync" => Task.FromResult(new SystemOverviewDto()),
                    "RecordSampleAsync" => Task.CompletedTask,
                    _ => throw new NotSupportedException(method.Name)
                }),
                [typeof(INotificationService)] = Fake<INotificationService>((method, args) => method.Name switch
                {
                    "IsEmailConfiguredAsync" => Task.FromResult(true),
                    "GetSettingsAsync" => Task.FromResult(new NotificationSettingsDto()),
                    "SendEmailAsync" => Notify((string)args[0]!, (string)args[1]!),
                    _ => throw new NotSupportedException(method.Name)
                })
            };
            var services = new ServiceCollection();
            foreach (var parameter in typeof(AutomationService).GetConstructors().Single().GetParameters())
                if (!dependencies.ContainsKey(parameter.ParameterType))
                    dependencies[parameter.ParameterType] = DispatchProxy.Create(parameter.ParameterType, typeof(AutomationRunningJobTests.AsyncProxy));
            foreach (var dependency in dependencies) services.AddSingleton(dependency.Key, dependency.Value);
            services.AddTransient<AutomationService>();
            provider = services.BuildServiceProvider();
            Service = provider.GetRequiredService<AutomationService>();
        }

        private Task<BackupRecordDto> Backup(BackupSiteRequest request)
        {
            SiteBackups.Add(request);
            return Task.FromResult(AutomationBackupOutcomeTests.Result("site", request.SiteName, request.Databases));
        }
        private Task<BackupPruneResultDto> Prune() { PruneCalls++; return Task.FromResult(new BackupPruneResultDto()); }
        private Task<Guid?> ReadSiteOwner()
        {
            SiteOwnerReads++;
            return FailAuthorityRead == "site-owner" ? Task.FromException<Guid?>(new IOException("owner unavailable")) : Task.FromResult(SiteOwner);
        }
        private Task Notify(string subject, string body) { Notifications.Add((subject, body)); return Task.CompletedTask; }

        public BackupRecordDto Archive(string id, int daysOld)
        {
            var path = Path.Combine(root, $"{id}.zip");
            using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
            using (var writer = new StreamWriter(archive.CreateEntry("available.txt").Open())) writer.Write(id);
            return new BackupRecordDto
            {
                Id = id, Name = $"{id}.zip", Path = path, Type = "site", Source = "site", Format = "zip",
                Status = "created", IntegrityState = BackupIntegrity.Sound, IncludedDatabases = [],
                Size = new FileInfo(path).Length, CreatedAtUtc = DateTime.UtcNow.AddDays(-daysOld)
            };
        }

        public async Task ConfigureHealth(params BackupRecordDto[] records)
        {
            await store.WriteAsync("config/backups.json", records);
            await store.WriteAsync("config/automation.json", new
            {
                autoBackupEnabled = true, lastBackupRunUtc = DateTime.UtcNow, lastBackupStatus = "completed", backupRetentionDays = 14,
                alertsEnabled = true, serviceDownAlertsEnabled = false, autoSslRenewEnabled = false, updateCheckEnabled = false, logCleanupEnabled = false
            });
        }

        public Task ConfigureBackup() => store.WriteAsync("config/automation.json", new
        {
            autoBackupEnabled = true, backupSites = true, backupDatabases = false, backupMail = false, backupPanelConfig = false,
            alertsEnabled = false, autoSslRenewEnabled = false, updateCheckEnabled = false, logCleanupEnabled = false
        });

        public void Dispose()
        {
            provider.Dispose();
            var absolute = Path.GetFullPath(root);
            Assert.StartsWith(Path.Combine(Path.GetTempPath(), "adapanel-automation-measurement-"), absolute, StringComparison.OrdinalIgnoreCase);
            Directory.Delete(absolute, recursive: true);
        }
    }

    private static T Fake<T>(Func<MethodInfo, object?[], object?> handler) where T : class
    {
        var result = DispatchProxy.Create<T, PostgresDatabaseIdentityConsumersTests.StrictProxy>();
        ((PostgresDatabaseIdentityConsumersTests.StrictProxy)(object)result).Handler = handler;
        return result;
    }
}
