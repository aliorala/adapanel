using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Text.Json;
using AdaPanel.Core.Dtos.Backup;
using AdaPanel.Core.Dtos.Databases;
using AdaPanel.Core.Dtos.Settings;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Backup;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Support;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class BackupScopeRetentionTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData("files")]
    [InlineData("config")]
    public async Task ARealSiteArchiveWithReducedScopeCannotReplaceTheOlderCompleteArchive(string omitted)
    {
        using var fixture = new Fixture();
        var complete = await fixture.Create("site");
        var reduced = await fixture.Create("site", files: omitted != "files", config: omitted != "config");
        using (var archive = ZipFile.OpenRead(complete.Path))
            Assert.NotNull(archive.GetEntry(omitted == "files" ? "files/index.txt" : "config/site.json"));
        using (var archive = ZipFile.OpenRead(reduced.Path))
            Assert.Null(archive.GetEntry(omitted == "files" ? "files/index.txt" : "config/site.json"));
        await fixture.Age((complete, 40), (reduced, 1));

        Assert.Equal(0, (await fixture.Prune("site")).Removed);
        await fixture.AssertPresent(complete, reduced);
    }

    [Fact]
    public async Task ARealSubscriptionWithoutFilesCannotReplaceTheOlderFullSubscription()
    {
        using var fixture = new Fixture();
        var complete = await fixture.Create("subscription");
        var reduced = await fixture.Create("subscription", files: false);
        using (var archive = ZipFile.OpenRead(reduced.Path))
            Assert.Null(archive.GetEntry("sites/site/files/index.txt"));
        await fixture.Age((complete, 40), (reduced, 1));
        Assert.Equal(0, (await fixture.Prune("subscription")).Removed);
        await fixture.AssertPresent(complete, reduced);
    }

    [Theory]
    [InlineData("site", false)]
    [InlineData("site", true)]
    [InlineData("subscription", false)]
    [InlineData("subscription", true)]
    public async Task TheLastIncrementalDumpScopeCannotBorrowCurrentCoverageFromItsOldBase(string type, bool excludeAll)
    {
        using var fixture = new Fixture();
        var complete = await fixture.Create(type);
        var chainBase = await fixture.Create(type, incremental: true);
        fixture.DatabaseNames = excludeAll ? [] : ["foo"];
        var recent = await fixture.Create(type, incremental: true);
        Assert.Equal("incremental", recent.BackupMode);
        Assert.Equal(chainBase.Id, recent.BaseBackupId);
        Assert.Equal(excludeAll ? [] : new[] { "foo" }, recent.IncludedDatabases!.Select(db => db.DatabaseName));
        using (var archive = ZipFile.OpenRead(recent.Path))
            Assert.DoesNotContain(archive.Entries, entry => entry.FullName.Contains("Foo", StringComparison.Ordinal));
        await fixture.Age((complete, 40), (chainBase, 20), (recent, 1));

        Assert.Equal(0, (await fixture.Prune(type)).Removed);
        await fixture.AssertPresent(complete, chainBase, recent);
    }

    [Theory]
    [InlineData("site")]
    [InlineData("subscription")]
    public async Task UnchangedIncrementalScopeStillReplacesAnOlderIndependentPoint(string type)
    {
        using var fixture = new Fixture();
        var complete = await fixture.Create(type);
        var chainBase = await fixture.Create(type, incremental: true);
        var recent = await fixture.Create(type, incremental: true);
        Assert.Equal("incremental", recent.BackupMode);
        // No changed file is needed to prove that the current file source was fully scanned.
        using (var archive = ZipFile.OpenRead(recent.Path))
            Assert.DoesNotContain(archive.Entries, entry => entry.FullName.EndsWith("files/index.txt", StringComparison.Ordinal));
        await fixture.Age((complete, 40), (chainBase, 20), (recent, 1));
        Assert.Equal(1, (await fixture.Prune(type)).Removed);
        await fixture.AssertPresent(chainBase, recent);
    }

    [Theory]
    [InlineData("site", false)]
    [InlineData("site", true)]
    [InlineData("subscription", false)]
    [InlineData("subscription", true)]
    public async Task ALocalReplacementCannotRemoveTheLastOffsiteArchive(string type, bool failedUpload)
    {
        using var fixture = new Fixture();
        var offsite = await fixture.Create(type, remote: true);
        Assert.Equal("created_remote_synced", offsite.Status);
        fixture.FailUpload = failedUpload;
        var local = await fixture.Create(type, remote: failedUpload);
        Assert.True(string.IsNullOrEmpty(local.RemoteUri));
        await fixture.Age((offsite, 40), (local, 1));

        Assert.Equal(0, (await fixture.Prune(type)).Removed);
        Assert.Equal(0, fixture.RemoteDeletes);
        await fixture.AssertPresent(offsite, local);
    }

    [Theory]
    [InlineData("site")]
    [InlineData("subscription")]
    public async Task AnOffsiteIncrementalLinkStillRequiresItsBaseOffsiteBeforeReplacingAnOffsitePoint(string type)
    {
        using var fixture = new Fixture();
        var offsite = await fixture.Create(type, remote: true);
        var localBase = await fixture.Create(type, incremental: true);
        var remoteDelta = await fixture.Create(type, incremental: true, remote: true);
        await fixture.Age((offsite, 40), (localBase, 20), (remoteDelta, 1));
        Assert.Equal(0, (await fixture.Prune(type)).Removed);
        Assert.Equal(0, fixture.RemoteDeletes);
        await fixture.AssertPresent(offsite, localBase, remoteDelta);
    }

    [Theory]
    [InlineData("site")]
    [InlineData("subscription")]
    public async Task ACompleteUploadedReplacementStillAllowsOffsiteRetention(string type)
    {
        using var fixture = new Fixture();
        var previous = await fixture.Create(type, remote: true);
        var replacement = await fixture.Create(type, remote: true);
        await fixture.Age((previous, 40), (replacement, 1));
        Assert.Equal(1, (await fixture.Prune(type)).Removed);
        Assert.Equal(1, fixture.RemoteDeletes);
        await fixture.AssertPresent(replacement);
    }

    [Theory]
    [InlineData("site")]
    [InlineData("subscription")]
    public async Task RealProducerReturnsAndPersistsMeasuredComponents(string type)
    {
        using var fixture = new Fixture();
        var result = await fixture.Create(type);
        var returned = Components(result);
        Assert.Contains(("files", "site"), returned);
        Assert.Contains(("config", "site"), returned);
        Assert.Equal(returned, Components(Assert.Single(await fixture.Service.GetBackupsAsync())));
    }

    [Fact]
    public async Task LegacyJsonWithoutComponentsRemainsExplicitlyUnknown()
    {
        using var fixture = new Fixture();
        await fixture.Store.WriteAsync("config/backups.json", new[] { new { id = "old", type = "site", source = "site" } });
        var legacy = JsonSerializer.SerializeToElement(Assert.Single(await fixture.Service.GetBackupsAsync()), WebJson);
        Assert.True(legacy.TryGetProperty("includedComponents", out var components));
        Assert.Equal(JsonValueKind.Null, components.ValueKind);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MeasuredAndUnknownComponentScopesCannotReplaceEachOther(bool previousUnknown)
    {
        using var fixture = new Fixture();
        var previous = await fixture.Create("site");
        var replacement = await fixture.Create("site");
        (previousUnknown ? previous : replacement).IncludedComponents = null;
        await fixture.Age((previous, 40), (replacement, 1));
        Assert.Equal(0, (await fixture.Prune("site")).Removed);
        await fixture.AssertPresent(previous, replacement);
    }

    [Theory]
    [InlineData("site")]
    [InlineData("subscription")]
    public async Task AnUploadLaterMeasuredMissingCannotReplaceTheLastOffsiteCopy(string type)
    {
        using var fixture = new Fixture();
        var previous = await fixture.Create(type, remote: true);
        var replacement = await fixture.Create(type, remote: true);
        replacement.RemoteState = BackupPresence.Missing;
        replacement.RemoteMissing = true;
        await fixture.Age((previous, 40), (replacement, 1));
        Assert.Equal(0, (await fixture.Prune(type)).Removed);
        Assert.Equal(0, fixture.RemoteDeletes);
        await fixture.AssertPresent(previous, replacement);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExplicitRemoteMeasurementHasAuthorityOverALegacyBooleanOnTheOldCopy(bool present)
    {
        using var fixture = new Fixture();
        var previous = await fixture.Create("site", remote: true);
        var replacement = await fixture.Create("site");
        previous.RemoteState = present ? BackupPresence.Present : BackupPresence.Missing;
        previous.RemoteMissing = present; // Deliberately contradictory legacy field.
        await fixture.Age((previous, 40), (replacement, 1));
        Assert.Equal(present ? 0 : 1, (await fixture.Prune("site")).Removed);
        await fixture.AssertPresent(present ? [previous, replacement] : [replacement]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExplicitRemoteMeasurementHasAuthorityOverALegacyBooleanOnTheReplacement(bool present)
    {
        using var fixture = new Fixture();
        var previous = await fixture.Create("site", remote: true);
        var replacement = await fixture.Create("site", remote: true);
        replacement.RemoteState = present ? BackupPresence.Present : BackupPresence.Missing;
        replacement.RemoteMissing = present;
        await fixture.Age((previous, 40), (replacement, 1));
        Assert.Equal(present ? 1 : 0, (await fixture.Prune("site")).Removed);
        await fixture.AssertPresent(present ? [replacement] : [previous, replacement]);
    }

    [Fact]
    public async Task ExpandingMeasuredScopeStillAllowsNormalRetention()
    {
        using var fixture = new Fixture();
        var previous = await fixture.Create("site", files: false);
        var replacement = await fixture.Create("site");
        await fixture.Age((previous, 40), (replacement, 1));
        Assert.Equal(1, (await fixture.Prune("site")).Removed);
        await fixture.AssertPresent(replacement);
    }

    [Fact]
    public async Task ACompletelyScannedEmptyDirectoryHasMeasuredFileCoverage()
    {
        using var fixture = new Fixture();
        File.Delete(Path.Combine(fixture.Root, "www", "index.txt"));
        var result = await fixture.Create("site");
        Assert.Contains(("files", "site"), Components(result));
        using var archive = ZipFile.OpenRead(result.Path);
        Assert.DoesNotContain(archive.Entries, entry => entry.FullName.StartsWith("files/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StandaloneDatabaseProducerMeasuresDatabaseScopeWithoutInventingFileComponents()
    {
        using var fixture = new Fixture();
        var result = await fixture.Service.BackupDatabaseAsync(new() { Engine = "postgresql", DatabaseName = "Foo" });
        Assert.Empty(Components(result));
        Assert.Equal("Foo", Assert.Single(result.IncludedDatabases!).DatabaseName);
        Assert.Empty(Components(Assert.Single(await fixture.Service.GetBackupsAsync())));
    }

    private static (string Kind, string Source)[] Components(BackupRecordDto record)
    {
        var json = JsonSerializer.SerializeToElement(record, WebJson);
        Assert.True(json.TryGetProperty("includedComponents", out var components), "The producer must persist measured component coverage.");
        Assert.Equal(JsonValueKind.Array, components.ValueKind);
        return components.EnumerateArray().Select(item => (item.GetProperty("kind").GetString()!, item.GetProperty("source").GetString()!)).ToArray();
    }

    private sealed class Fixture : IDisposable
    {
        private static readonly Guid Owner = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000014");
        public string Root { get; } = Path.Combine(Path.GetTempPath(), $"adapanel-scope-retention-{Guid.NewGuid():N}");
        public string[] DatabaseNames { get; set; } = ["foo", "Foo"];
        public bool FailUpload { get; set; }
        public int RemoteDeletes { get; private set; }
        public JsonFileStore Store { get; }
        public BackupService Service { get; }

        public Fixture()
        {
            Directory.CreateDirectory(Path.Combine(Root, "www"));
            File.WriteAllText(Path.Combine(Root, "www", "index.txt"), "A real site file that must remain recoverable.");
            Store = new JsonFileStore(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = Root }));
            var database = Fake<IDatabaseService>((method, args) => method.Name == "ExportDumpAsync"
                ? Export((DatabaseDumpRequest)args[0]!) : throw new NotSupportedException(method.Name));
            var site = new SiteDto { Name = "site", PhysicalPath = Path.Combine(Root, "www") };
            var sites = Fake<IIisSiteService>((method, _) => method.Name switch
            {
                "GetSiteAsync" => Task.FromResult(site),
                "GetSitesAsync" => Task.FromResult<IReadOnlyCollection<SiteDto>>([site]),
                _ => throw new NotSupportedException(method.Name)
            });
            var preferences = Fake<IPanelPreferencesService>((method, _) => method.Name == "GetAsync"
                ? Task.FromResult(new PanelPreferencesDto { DefaultBackupRoot = Path.Combine(Root, "backups") }) : throw new NotSupportedException(method.Name));
            var scopes = Fake<ICustomerScopeService>((method, _) => method.Name == "GetScopeAsync"
                ? Task.FromResult(new CustomerScope(["site"], 0, 0, 0, 0, 0)) : throw new NotSupportedException(method.Name));
            var ownership = Fake<IDatabaseOwnershipService>((method, _) => method.Name == "GetAllAsync"
                ? Task.FromResult<IReadOnlyCollection<(string, string, Guid, string)>>(DatabaseNames.Select(name => ("postgresql", name, Owner, "site")).ToArray())
                : throw new NotSupportedException(method.Name));
            var clients = Fake<IHttpClientFactory>((method, _) => method.Name == "CreateClient"
                ? new HttpClient(new FakeHttp(request =>
                {
                    if (request.Method == HttpMethod.Delete) { RemoteDeletes++; return HttpStatusCode.NoContent; }
                    return FailUpload ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.Created;
                })) : throw new NotSupportedException(method.Name));
            Service = new BackupService(sites, database, null!, preferences, null!, Store, clients,
                customerScopes: scopes, databaseOwnership: ownership, outboundResolver: SahteAgCozumleyici.Genel);
        }

        public Task<BackupRecordDto> Create(string type, bool files = true, bool config = true, bool incremental = false, bool remote = false)
        {
            BackupArchiveRequest request = type == "site"
                ? new BackupSiteRequest { SiteName = "site", IncludeFiles = files, IncludeConfig = config,
                    Databases = DatabaseNames.Select(name => new BackupDatabaseRef { Engine = "postgresql", DatabaseName = name }).ToList() }
                : new BackupSubscriptionRequest { UserId = Owner, IncludeFiles = files, IncludeMail = false, IncludeFtp = false };
            request.Incremental = incremental;
            request.FullBackupEveryDays = 365;
            request.TargetType = remote ? "webdav" : "local";
            request.RemoteUrl = remote ? "https://backup.example.test/dav/archives" : "";
            return request is BackupSiteRequest site ? Service.BackupSiteAsync(site) : Service.BackupSubscriptionAsync((BackupSubscriptionRequest)request);
        }

        public async Task Age(params (BackupRecordDto Record, int DaysOld)[] records)
        {
            foreach (var (record, days) in records) record.CreatedAtUtc = DateTime.UtcNow.AddDays(-days);
            await Store.WriteAsync("config/backups.json", records.Select(item => item.Record).ToArray());
        }

        public Task<BackupPruneResultDto> Prune(string type) => type == "site"
            ? Service.PruneAsync(14, new BackupPruneRemoteOptions())
            : Service.PruneSubscriptionAsync(Owner, retentionDays: 0, keepCount: 1, remote: new BackupPruneRemoteOptions());

        public async Task AssertPresent(params BackupRecordDto[] records)
        {
            var saved = await Service.GetBackupsAsync();
            Assert.Equal(records.Select(record => record.Id).Order(), saved.Select(record => record.Id).Order());
            Assert.All(records, record => Assert.True(File.Exists(record.Path), record.Id));
        }

        private static async Task<DatabaseDumpResultDto> Export(DatabaseDumpRequest request)
        {
            var contents = $"-- {request.DatabaseName} dump\nCREATE TABLE fixture (id int);\n";
            await File.WriteAllTextAsync(request.FilePath, contents);
            return new DatabaseDumpResultDto { Engine = request.Engine, DatabaseName = request.DatabaseName, FilePath = request.FilePath, Size = contents.Length };
        }

        public void Dispose()
        {
            var absolute = Path.GetFullPath(Root);
            Assert.StartsWith(Path.Combine(Path.GetTempPath(), "adapanel-scope-retention-"), absolute, StringComparison.OrdinalIgnoreCase);
            Directory.Delete(absolute, recursive: true);
        }
    }

    private sealed class FakeHttp(Func<HttpRequestMessage, HttpStatusCode> status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status(request)));
    }

    private static T Fake<T>(Func<MethodInfo, object?[], object?> handler) where T : class
    {
        var proxy = DispatchProxy.Create<T, PostgresDatabaseIdentityConsumersTests.StrictProxy>();
        ((PostgresDatabaseIdentityConsumersTests.StrictProxy)(object)proxy).Handler = handler;
        return proxy;
    }
}
