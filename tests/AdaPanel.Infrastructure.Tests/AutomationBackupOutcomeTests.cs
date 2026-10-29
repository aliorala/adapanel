using System.Reflection;
using System.Text.Json;
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

public sealed class AutomationBackupOutcomeTests
{
    [Theory]
    [InlineData("created")]
    [InlineData("created_with_errors")]
    public async Task Summary_CountsMeasuredSiteAndSeparateDatabasesExactlyOnce(string siteStatus)
    {
        using var fixture = new Fixture();
        fixture.WorkspaceNames = ["foo", "Foo", "standalone", "standalone"];
        fixture.SiteResult = _ => Task.FromResult(Result("site", "site",
            [new() { Engine = "postgres", DatabaseName = "foo" },
             new() { Engine = "postgresql", DatabaseName = "foo" },
             new() { Engine = "postgresql", DatabaseName = "Foo" }], siteStatus,
            warnings: siteStatus == "created_with_errors"
                ? [new() { MessageKey = "backup.source_file_unreadable", Target = "files/locked.txt" }] : []));
        await fixture.Configure(engines: ["postgres", "postgresql"]);

        var status = await fixture.Service.RunBackupNowAsync();

        Assert.Contains("3 veritabanı kopyası doğrulandı (site arşivlerinde: 2, ayrı dökümlerde: 1)", status.LastBackupDetails);
        Assert.Equal("standalone", Assert.Single(fixture.DatabaseCalls).DatabaseName);
        Assert.Equal(siteStatus == "created" ? "completed" : "completed_with_errors", status.LastBackupStatus);
    }

    [Fact]
    public async Task Summary_CountsMeasuredSiteDatabasesWhenIndependentBackupIsDisabled()
    {
        using var fixture = new Fixture();
        await fixture.Configure(backupDatabases: false);
        var status = await fixture.Service.RunBackupNowAsync();
        Assert.Contains("2 veritabanı kopyası doğrulandı (site arşivlerinde: 2, ayrı dökümlerde: 0)", status.LastBackupDetails);
        Assert.Empty(fixture.DatabaseCalls);
        Assert.Equal(0, fixture.EngineDiscoveryCalls);
    }

    [Fact]
    public async Task DiscoveryFailure_IsAnIncompleteRunWithRealErrorInsteadOfEmptySuccess()
    {
        using var fixture = new Fixture();
        fixture.EngineResult = () => Task.FromException<IReadOnlyCollection<DatabaseEngineStatusDto>>(new IOException("inventory unavailable"));
        await fixture.Configure(backupSites: false, engines: []);
        var status = await fixture.Service.RunBackupNowAsync();
        Assert.Equal("completed_with_errors", status.LastBackupStatus);
        Assert.Contains("engine discovery: inventory unavailable", status.LastBackupDetails);
        Assert.Empty(fixture.DatabaseCalls);
        Assert.Equal(2, fixture.PruneCalls);
    }

    [Fact]
    public async Task DiscoveryCancellation_IsNotSwallowedAndSkipsPostPruning()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        fixture.EngineResult = () =>
        {
            cancellation.Cancel();
            return Task.FromCanceled<IReadOnlyCollection<DatabaseEngineStatusDto>>(cancellation.Token);
        };
        await fixture.Configure(backupSites: false, engines: []);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Service.RunBackupNowAsync(cancellation.Token));
        Assert.Equal(1, fixture.PruneCalls);
        Assert.Empty(fixture.DatabaseCalls);
    }

    [Fact]
    public async Task InstalledUnavailableEngine_IsReportedWhileReadyEngineStillBacksUp()
    {
        using var fixture = new Fixture();
        fixture.EngineResult = () => Task.FromResult<IReadOnlyCollection<DatabaseEngineStatusDto>>(
            [new() { Engine = "mariadb", Installed = true, Ready = false },
             new() { Engine = "postgresql", Installed = true, Ready = true }]);
        await fixture.Configure(backupSites: false, engines: []);
        var status = await fixture.Service.RunBackupNowAsync();
        Assert.Equal("completed_with_errors", status.LastBackupStatus);
        Assert.Contains("engine mariadb:", status.LastBackupDetails);
        Assert.Contains("database.engine_not_ready", status.LastBackupDetails);
        Assert.Contains("2 veritabanı kopyası doğrulandı", status.LastBackupDetails);
        Assert.All(fixture.DatabaseCalls, call => Assert.Equal("postgresql", call.Engine));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmptyServer_DoesNotInventMissingEngineFailure(bool withUninstalledEngine)
    {
        using var fixture = new Fixture();
        fixture.EngineResult = () => Task.FromResult<IReadOnlyCollection<DatabaseEngineStatusDto>>(withUninstalledEngine
            ? [new() { Engine = "mariadb", Installed = false, Ready = false }] : []);
        await fixture.Configure(backupSites: false, engines: []);
        var status = await fixture.Service.RunBackupNowAsync();
        Assert.Equal("completed", status.LastBackupStatus);
        Assert.Contains("0 veritabanı", status.LastBackupDetails);
        Assert.Empty(fixture.DatabaseCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitOrDisabledBackup_DoesNotDiscoverOrEnableOtherEngines(bool enabled)
    {
        using var fixture = new Fixture();
        fixture.EngineResult = () => throw new InvalidOperationException("must not discover");
        await fixture.Configure(backupSites: false, backupDatabases: enabled, engines: enabled ? ["postgresql"] : []);
        var status = await fixture.Service.RunBackupNowAsync();
        Assert.Equal("completed", status.LastBackupStatus);
        Assert.Equal(0, fixture.EngineDiscoveryCalls);
        Assert.Equal(enabled ? 2 : 0, fixture.DatabaseCalls.Count);
    }

    [Fact]
    public async Task PartialSite_RetriesOnlyMissingDatabaseAndPreservesOriginalWarning()
    {
        using var fixture = new Fixture();
        fixture.SiteResult = _ => Task.FromResult(Result("site", "site", Included("foo"), "created_with_errors",
            warnings: [new() { MessageKey = "backup.database_dump_empty", Target = "postgresql/Foo" }]));
        await fixture.Configure();
        var status = await fixture.Service.RunBackupNowAsync();
        Assert.Equal("Foo", Assert.Single(fixture.DatabaseCalls).DatabaseName);
        Assert.Equal("completed_with_errors", status.LastBackupStatus);
        Assert.Contains("backup.database_dump_empty", status.LastBackupDetails);
        Assert.Contains("postgresql/Foo", status.LastBackupDetails);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("empty")]
    [InlineData("unknown")]
    [InlineData("unsound")]
    [InlineData("wrong_source")]
    [InlineData("wrong_type")]
    [InlineData("failed")]
    public async Task UnconfirmedSite_DoesNotSuppressIndependentBackups(string scenario)
    {
        using var fixture = new Fixture();
        fixture.SiteResult = _ => Task.FromResult(Result(
            scenario == "wrong_type" ? "database" : "site", scenario == "wrong_source" ? "other-site" : "site",
            scenario == "missing" ? null : scenario == "empty" ? [] : Included("foo", "Foo"),
            scenario == "failed" ? "failed" : "created",
            scenario is "unknown" or "unsound" ? scenario : "sound"));
        await fixture.Configure();
        var status = await fixture.Service.RunBackupNowAsync();
        Assert.Equal(["foo", "Foo"], fixture.DatabaseCalls.Select(call => call.DatabaseName));
        Assert.Equal("completed_with_errors", status.LastBackupStatus);
    }

    [Fact]
    public async Task HealthySite_UsesMeasuredInclusionWithPostgresAliasAndCase()
    {
        using var fixture = new Fixture();
        fixture.SiteResult = _ => Task.FromResult(Result("site", "site", [new() { Engine = "postgres", DatabaseName = "foo" }, new() { Engine = "postgresql", DatabaseName = "Foo" }]));
        await fixture.Configure();
        var status = await fixture.Service.RunBackupNowAsync();
        Assert.Empty(fixture.DatabaseCalls);
        Assert.Equal("completed", status.LastBackupStatus);
    }

    [Fact]
    public async Task FileOnlyWarning_DoesNotCauseDuplicateDatabaseDumps()
    {
        using var fixture = new Fixture();
        fixture.SiteResult = _ => Task.FromResult(Result("site", "site", Included("foo", "Foo"), "created_with_errors",
            warnings: [new() { MessageKey = "backup.source_file_unreadable", Target = "files/locked.txt" }]));
        await fixture.Configure();
        var status = await fixture.Service.RunBackupNowAsync();
        Assert.Empty(fixture.DatabaseCalls);
        Assert.Equal("completed_with_errors", status.LastBackupStatus);
        Assert.Contains("files/locked.txt", status.LastBackupDetails);
    }

    [Fact]
    public async Task RemoteFailure_IsReportedWhileMeasuredLocalDatabasesRemainIncluded()
    {
        using var fixture = new Fixture();
        fixture.SiteResult = _ => Task.FromResult(Result("site", "site", Included("foo", "Foo"), "created_remote_failed",
            warnings: [new() { MessageKey = "backup.remote_upload_failed", Target = "webdav" }]));
        await fixture.Configure();
        var status = await fixture.Service.RunBackupNowAsync();
        Assert.Empty(fixture.DatabaseCalls);
        Assert.Equal("completed_with_errors", status.LastBackupStatus);
        Assert.Contains("backup.remote_upload_failed", status.LastBackupDetails);
    }

    [Fact]
    public async Task ContradictoryEmptyDumpWarning_CannotConfirmThatDatabase()
    {
        using var fixture = new Fixture();
        fixture.SiteResult = _ => Task.FromResult(Result("site", "site", Included("foo", "Foo"), "created_with_errors",
            warnings: [new() { MessageKey = "backup.database_dump_empty", Target = "postgres/Foo" }]));
        await fixture.Configure();
        await fixture.Service.RunBackupNowAsync();
        Assert.Equal("Foo", Assert.Single(fixture.DatabaseCalls).DatabaseName);
    }

    [Theory]
    [InlineData(false, "postgresql")]
    [InlineData(true, "mysql")]
    public async Task MissingDatabases_DoNotOverrideDisabledOrExcludedEnginePolicy(bool enabled, string engine)
    {
        using var fixture = new Fixture();
        fixture.SiteResult = _ => Task.FromResult(Result("site", "site", [], "created_with_errors",
            warnings: [new() { MessageKey = "backup.database_dump_empty", Target = "postgresql/foo" }]));
        await fixture.Configure(backupDatabases: enabled, engines: [engine]);
        var status = await fixture.Service.RunBackupNowAsync();
        Assert.Empty(fixture.DatabaseCalls);
        Assert.Equal("completed_with_errors", status.LastBackupStatus);
    }

    [Fact]
    public async Task RepeatedEngineAliases_RetryEachPostgresIdentityAtMostOnce()
    {
        using var fixture = new Fixture();
        fixture.SiteResult = _ => Task.FromResult(Result("site", "site", []));
        await fixture.Configure(engines: ["postgresql", "postgres"]);
        await fixture.Service.RunBackupNowAsync();
        Assert.Equal(["foo", "Foo"], fixture.DatabaseCalls.Select(call => call.DatabaseName));
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("unknown")]
    [InlineData("unsound")]
    [InlineData("zero")]
    [InlineData("wrong_source")]
    [InlineData("missing_inclusion")]
    [InlineData("empty_inclusion")]
    [InlineData("null")]
    public async Task IndependentBackup_UnconfirmedOrFailedResultIsNotCountedAsSuccess(string scenario)
    {
        using var fixture = new Fixture();
        fixture.DatabaseResult = request => Task.FromResult(scenario == "null" ? null! : Result("database",
            scenario == "wrong_source" ? "postgresql:other" : $"{request.Engine}:{request.DatabaseName}",
            scenario == "missing_inclusion" ? null : scenario == "empty_inclusion" ? [] : Included(request.DatabaseName),
            scenario == "failed" ? "failed" : "created",
            scenario is "unknown" or "unsound" ? scenario : "sound", scenario == "zero" ? 0 : 12));
        await fixture.Configure(backupSites: false);
        var status = await fixture.Service.RunBackupNowAsync();
        Assert.Equal(2, fixture.DatabaseCalls.Count);
        Assert.Equal("completed_with_errors", status.LastBackupStatus);
        Assert.Contains("0 veritabanı", status.LastBackupDetails);
    }

    [Fact]
    public async Task IndependentBackup_ConfirmedResultIsCounted()
    {
        using var fixture = new Fixture();
        await fixture.Configure(backupSites: false);
        var status = await fixture.Service.RunBackupNowAsync();
        Assert.Equal("completed", status.LastBackupStatus);
        Assert.Contains("2 veritabanı", status.LastBackupDetails);
    }

    [Fact]
    public async Task IndependentBackup_LocalMeasuredCopiesAreCountedButRemoteUploadFailureRemainsAnError()
    {
        using var fixture = new Fixture();
        fixture.DatabaseResult = request => Task.FromResult(Result("database", $"{request.Engine}:{request.DatabaseName}",
            Included(request.DatabaseName), "created_remote_failed",
            warnings: [new() { MessageKey = "backup.remote_upload_failed", Target = "ftp", Detail = "connection refused" }]));
        await fixture.Configure(backupSites: false);
        var status = await fixture.Service.RunBackupNowAsync();
        Assert.Equal("completed_with_errors", status.LastBackupStatus);
        Assert.Contains("2 veritabanı kopyası doğrulandı (site arşivlerinde: 0, ayrı dökümlerde: 2)", status.LastBackupDetails);
        Assert.Contains("backup.remote_upload_failed", status.LastBackupDetails);
        Assert.Contains("connection refused", status.LastBackupDetails);
    }

    [Theory]
    [InlineData("mail", "created_with_errors")]
    [InlineData("mail", "created_remote_failed")]
    [InlineData("panel", "created_with_errors")]
    [InlineData("panel", "created_remote_failed")]
    public async Task MailAndPanel_FailedResultsAreNotReportedAsBackedUp(string type, string resultStatus)
    {
        using var fixture = new Fixture();
        fixture.OtherResult = kind => Task.FromResult(Result(kind, kind, null, resultStatus));
        await fixture.Configure(backupSites: false, backupDatabases: false, backupMail: type == "mail", backupPanel: type == "panel");
        var status = await fixture.Service.RunBackupNowAsync();
        Assert.Equal("completed_with_errors", status.LastBackupStatus);
        Assert.DoesNotContain($"{type}: yedeklendi", status.LastBackupDetails);
        Assert.Contains(resultStatus, status.LastBackupDetails);
    }

    [Theory]
    [InlineData("mail")]
    [InlineData("panel")]
    public async Task ConfirmedRemoteOnlyCopy_RemainsSuccessful(string type)
    {
        using var fixture = new Fixture();
        fixture.OtherResult = kind =>
        {
            var record = Result(kind, kind, null, "created_remote_synced");
            record.Path = "";
            record.LocalMissing = true;
            record.RemoteUri = "https://backup.example.test/confirmed.zip";
            record.RemoteState = BackupPresence.Present;
            return Task.FromResult(record);
        };
        await fixture.Configure(backupSites: false, backupDatabases: false, backupMail: type == "mail", backupPanel: type == "panel");
        var status = await fixture.Service.RunBackupNowAsync();
        Assert.Equal("completed", status.LastBackupStatus);
        Assert.Contains($"{type}: yedeklendi", status.LastBackupDetails);
    }

    [Theory]
    [InlineData("mail")]
    [InlineData("panel")]
    public async Task MailAndPanel_CancellationDoesNotContinueToPruning(string type)
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        fixture.OtherResult = _ =>
        {
            cancellation.Cancel();
            return Task.FromCanceled<BackupRecordDto>(cancellation.Token);
        };
        await fixture.Configure(backupSites: false, backupDatabases: false, backupMail: type == "mail", backupPanel: type == "panel");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Service.RunBackupNowAsync(cancellation.Token));
        Assert.Equal(1, fixture.PruneCalls); // Pre-production rotation ran; cancellation prevents the post pass.
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CallerCancellation_DoesNotStartIndependentWorkOrPruning(bool inSite)
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        Task<BackupRecordDto> Cancel()
        {
            cancellation.Cancel();
            return Task.FromCanceled<BackupRecordDto>(cancellation.Token);
        }
        if (inSite) fixture.SiteResult = _ => Cancel();
        else
        {
            fixture.WorkspaceNames = ["foo"];
            fixture.DatabaseResult = _ => Cancel();
        }
        await fixture.Configure(backupSites: inSite, backupDatabases: !inSite);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Service.RunBackupNowAsync(cancellation.Token));
        Assert.Equal(1, fixture.PruneCalls); // Only the safe pre-production pass ran.
        Assert.Equal(inSite ? 0 : 1, fixture.DatabaseCalls.Count);
    }

    internal static BackupRecordDto Result(string type, string source, IReadOnlyCollection<BackupDatabaseRef>? included,
        string status = "created", string integrity = "sound", long size = 12, List<BackupRestoreWarningDto>? warnings = null) =>
        JsonSerializer.Deserialize<BackupRecordDto>(JsonSerializer.Serialize(new
        {
            id = Guid.NewGuid().ToString("N"), type, source, path = "mock-archive", size, status,
            integrityState = integrity, includedDatabases = included, warnings = warnings ?? []
        }), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    private static BackupDatabaseRef[] Included(params string[] names) => names.Select(name => new BackupDatabaseRef { Engine = "postgresql", DatabaseName = name }).ToArray();

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), $"adapanel-backup-outcome-{Guid.NewGuid():N}");
        private readonly JsonFileStore store;
        public AutomationService Service { get; }
        public Func<BackupSiteRequest, Task<BackupRecordDto>> SiteResult { get; set; } = _ => Task.FromResult(Result("site", "site", Included("foo", "Foo")));
        public Func<BackupDatabaseRequest, Task<BackupRecordDto>> DatabaseResult { get; set; } = request => Task.FromResult(Result("database", $"{request.Engine}:{request.DatabaseName}", Included(request.DatabaseName)));
        public Func<string, Task<BackupRecordDto>> OtherResult { get; set; } = type => Task.FromResult(Result(type, type, null));
        public string[] WorkspaceNames { get; set; } = ["foo", "Foo"];
        public Func<Task<IReadOnlyCollection<DatabaseEngineStatusDto>>> EngineResult { get; set; } =
            () => Task.FromResult<IReadOnlyCollection<DatabaseEngineStatusDto>>([]);
        public int EngineDiscoveryCalls { get; private set; }
        public List<BackupDatabaseRequest> DatabaseCalls { get; } = [];
        public int PruneCalls { get; private set; }

        public Fixture()
        {
            Directory.CreateDirectory(root);
            store = new JsonFileStore(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = root }));
            var jobs = new RunningJobService(store, NullLogger<RunningJobService>.Instance);
            var backups = Fake<IBackupService>((method, args) =>
            {
                switch (method.Name)
                {
                    case "BackupSiteAsync": return SiteResult((BackupSiteRequest)args[0]!);
                    case "BackupDatabaseAsync":
                        var request = (BackupDatabaseRequest)args[0]!;
                        DatabaseCalls.Add(request);
                        return DatabaseResult(request);
                    case "BackupMailAsync": return OtherResult("mail");
                    case "BackupPanelAsync": return OtherResult("panel");
                    case "PruneAsync": PruneCalls++; return Task.FromResult(new BackupPruneResultDto());
                    default: throw new NotSupportedException(method.Name);
                }
            });
            var sites = Fake<IIisSiteService>((method, _) => method.Name == "GetSitesAsync"
                ? Task.FromResult<IReadOnlyCollection<SiteDto>>([new() { Name = "site" }]) : throw new NotSupportedException(method.Name));
            var ownership = Fake<IDatabaseOwnershipService>((method, _) => method.Name == "GetAllAsync"
                ? Task.FromResult<IReadOnlyCollection<(string, string, Guid, string)>>([("postgresql", "foo", Guid.NewGuid(), "site"), ("postgresql", "Foo", Guid.NewGuid(), "site")])
                : throw new NotSupportedException(method.Name));
            var databases = Fake<IDatabaseService>((method, args) => method.Name switch
            {
                "GetWorkspaceAsync" => Task.FromResult(new DatabaseWorkspaceDto { Engine = (string)args[0]!, Databases = (string)args[0]! == "mysql" ? [] : WorkspaceNames.Select(name => new DatabaseRecordDto { Name = name }).ToArray() }),
                "GetEnginesAsync" => DiscoverEngines(),
                _ => throw new NotSupportedException(method.Name)
            });
            Service = new AutomationService(store, backups, null!, null!, sites, null!, null!, null!, databases,
                null!, null!, null!, null!, null!, null!, null!, new ConcurrencyLock(), null!, ownership, jobs,
                NullLogger<AutomationService>.Instance);
        }

        private Task<IReadOnlyCollection<DatabaseEngineStatusDto>> DiscoverEngines()
        {
            EngineDiscoveryCalls++;
            return EngineResult();
        }

        public Task Configure(bool backupSites = true, bool backupDatabases = true, string[]? engines = null, bool backupMail = false, bool backupPanel = false) => store.WriteAsync("config/automation.json", new
        {
            autoBackupEnabled = true, backupSites, backupDatabases, backupMail, backupPanelConfig = backupPanel,
            backupDatabaseEngines = engines ?? ["postgresql"], backupTargetType = "local", autoSslRenewEnabled = false, updateCheckEnabled = false
        });

        public void Dispose()
        {
            var absolute = Path.GetFullPath(root);
            Assert.StartsWith(Path.Combine(Path.GetTempPath(), "adapanel-backup-outcome-"), absolute, StringComparison.OrdinalIgnoreCase);
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
