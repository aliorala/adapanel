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

public sealed class BackupDatabaseInclusionTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task SiteBackup_OnlyActualNonemptyDumpsAreReturnedAndPersisted()
    {
        using var fixture = new Fixture();
        fixture.EmptyName = "Foo";
        var result = await fixture.Service.BackupSiteAsync(fixture.SiteRequest());
        Assert.Equal("created_with_errors", result.Status);
        Assert.Equal([("postgresql", "foo")], Inclusion(result));
        Assert.Equal(Inclusion(result), Inclusion(Assert.Single(await fixture.Service.GetBackupsAsync())));
        using var archive = ZipFile.OpenRead(result.Path);
        Assert.Contains(archive.Entries, entry => entry.FullName == "databases/postgres-foo.sql");
        Assert.DoesNotContain(archive.Entries, entry => entry.FullName == "databases/postgresql-Foo.sql");
    }

    [Fact]
    public async Task SiteWithNoRequestedDatabases_HasMeasuredEmptyList()
    {
        using var fixture = new Fixture();
        var request = fixture.SiteRequest();
        request.Databases = [];
        var result = await fixture.Service.BackupSiteAsync(request);
        Assert.Empty(Inclusion(result));
        Assert.Empty(Inclusion(Assert.Single(await fixture.Service.GetBackupsAsync())));
    }

    [Fact]
    public async Task IncludedIdentities_AreSnapshotValuesIndependentOfRequestMutation()
    {
        using var fixture = new Fixture();
        var request = fixture.SiteRequest();
        var result = await fixture.Service.BackupSiteAsync(request);
        request.Databases[0].DatabaseName = "changed";
        Assert.Equal([("postgresql", "foo"), ("postgresql", "Foo")], Inclusion(result));
        Assert.Equal(Inclusion(result), Inclusion(Assert.Single(await fixture.Service.GetBackupsAsync())));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemoteOutcome_DoesNotDropIncludedDatabaseMetadata(bool failRemote)
    {
        using var fixture = new Fixture(failRemote);
        var request = fixture.SiteRequest();
        request.TargetType = "webdav";
        request.RemoteUrl = "https://backup.example.test/dav/archives";
        var result = await fixture.Service.BackupSiteAsync(request);
        Assert.Equal(failRemote ? "created_remote_failed" : "created_remote_synced", result.Status);
        Assert.Equal([("postgresql", "foo"), ("postgresql", "Foo")], Inclusion(result));
        Assert.Equal(Inclusion(result), Inclusion(Assert.Single(await fixture.Service.GetBackupsAsync())));
        Assert.True(fixture.HttpCalls > 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SubscriptionArchive_OnlySuccessfulDumpsAreMeasured(bool throwOnUpper)
    {
        using var fixture = new Fixture();
        if (throwOnUpper) fixture.ThrowName = "Foo";
        else fixture.EmptyName = "Foo";
        var result = await fixture.Service.BackupSubscriptionAsync(new BackupSubscriptionRequest
        {
            UserId = Fixture.Owner, IncludeDatabases = true, IncludeFiles = false, IncludeMail = false, IncludeFtp = false
        });
        Assert.Equal("created_with_errors", result.Status);
        Assert.Equal([("postgresql", "foo")], Inclusion(result));
        Assert.Equal(Inclusion(result), Inclusion(Assert.Single(await fixture.Service.GetBackupsAsync())));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StandaloneDatabase_InclusionMatchesMeasuredNonemptyDump(bool empty)
    {
        using var fixture = new Fixture();
        if (empty) fixture.EmptyName = "Foo";
        var result = await fixture.Service.BackupDatabaseAsync(new BackupDatabaseRequest { Engine = "postgres", DatabaseName = "Foo" });
        Assert.Equal(empty ? [] : new[] { ("postgresql", "Foo") }, Inclusion(result));
        Assert.Equal(Inclusion(result), Inclusion(Assert.Single(await fixture.Service.GetBackupsAsync())));
    }

    [Fact]
    public async Task OldJsonWithoutMeasurement_RoundTripsAsUnknownRatherThanEmpty()
    {
        using var fixture = new Fixture();
        var path = Path.Combine(fixture.Root, "legacy.sql");
        await File.WriteAllTextAsync(path, "-- old dump");
        await fixture.Store.WriteAsync("config/backups.json", new[] { new { id = "legacy", type = "database", source = "postgresql:foo", path, status = "created" } });
        var result = Assert.Single(await fixture.Service.GetBackupsAsync());
        var json = JsonSerializer.SerializeToElement(result, WebJson);
        Assert.True(json.TryGetProperty("includedDatabases", out var included));
        Assert.Equal(JsonValueKind.Null, included.ValueKind);
    }

    private static (string Engine, string Name)[] Inclusion(BackupRecordDto record)
    {
        var json = JsonSerializer.SerializeToElement(record, WebJson);
        Assert.True(json.TryGetProperty("includedDatabases", out var included), "The result must carry explicit database inclusion evidence.");
        Assert.Equal(JsonValueKind.Array, included.ValueKind);
        return included.EnumerateArray().Select(item => (item.GetProperty("engine").GetString()!, item.GetProperty("databaseName").GetString()!)).ToArray();
    }

    private sealed class Fixture : IDisposable
    {
        public static readonly Guid Owner = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000012");
        public string Root { get; } = Path.Combine(Path.GetTempPath(), $"adapanel-db-inclusion-{Guid.NewGuid():N}");
        public string? EmptyName { get; set; }
        public string? ThrowName { get; set; }
        public int HttpCalls { get; private set; }
        public JsonFileStore Store { get; }
        public BackupService Service { get; }

        public Fixture(bool failRemote = false)
        {
            Directory.CreateDirectory(Root);
            Store = new JsonFileStore(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = Root }));
            var database = Fake<IDatabaseService>((method, args) => method.Name == "ExportDumpAsync"
                ? Export((DatabaseDumpRequest)args[0]!) : throw new NotSupportedException(method.Name));
            var sites = Fake<IIisSiteService>((method, _) => method.Name == "GetSiteAsync"
                ? Task.FromResult(new SiteDto { Name = "site", PhysicalPath = Root }) : throw new NotSupportedException(method.Name));
            var preferences = Fake<IPanelPreferencesService>((method, _) => method.Name == "GetAsync"
                ? Task.FromResult(new PanelPreferencesDto { DefaultBackupRoot = Path.Combine(Root, "backups") }) : throw new NotSupportedException(method.Name));
            var scopes = Fake<ICustomerScopeService>((method, _) => method.Name == "GetScopeAsync"
                ? Task.FromResult(new CustomerScope(["site"], 0, 0, 0, 0, 0)) : throw new NotSupportedException(method.Name));
            var ownership = Fake<IDatabaseOwnershipService>((method, _) => method.Name == "GetAllAsync"
                ? Task.FromResult<IReadOnlyCollection<(string, string, Guid, string)>>([("postgres", "foo", Owner, "site"), ("postgresql", "Foo", Owner, "site")])
                : throw new NotSupportedException(method.Name));
            var clients = Fake<IHttpClientFactory>((method, _) => method.Name == "CreateClient"
                ? new HttpClient(new FakeHttp(() => { HttpCalls++; return failRemote ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.Created; }))
                : throw new NotSupportedException(method.Name));
            Service = new BackupService(sites, database, null!, preferences, null!, Store, clients,
                customerScopes: scopes, databaseOwnership: ownership, outboundResolver: SahteAgCozumleyici.Genel);
        }

        public BackupSiteRequest SiteRequest() => new()
        {
            SiteName = "site", IncludeFiles = false, IncludeConfig = true,
            Databases = [new() { Engine = "postgres", DatabaseName = "foo" }, new() { Engine = "postgresql", DatabaseName = "Foo" }]
        };

        private async Task<DatabaseDumpResultDto> Export(DatabaseDumpRequest request)
        {
            if (request.DatabaseName == ThrowName) throw new IOException("fake dump failure");
            var contents = request.DatabaseName == EmptyName ? "" : $"-- {request.DatabaseName} dump";
            await File.WriteAllTextAsync(request.FilePath, contents);
            return new DatabaseDumpResultDto { Engine = request.Engine, DatabaseName = request.DatabaseName, FilePath = request.FilePath, Size = contents.Length };
        }

        public void Dispose()
        {
            var absolute = Path.GetFullPath(Root);
            Assert.StartsWith(Path.Combine(Path.GetTempPath(), "adapanel-db-inclusion-"), absolute, StringComparison.OrdinalIgnoreCase);
            Directory.Delete(absolute, recursive: true);
        }
    }

    private sealed class FakeHttp(Func<HttpStatusCode> status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status()));
    }

    private static T Fake<T>(Func<MethodInfo, object?[], object?> handler) where T : class
    {
        var proxy = DispatchProxy.Create<T, PostgresDatabaseIdentityConsumersTests.StrictProxy>();
        ((PostgresDatabaseIdentityConsumersTests.StrictProxy)(object)proxy).Handler = handler;
        return proxy;
    }
}
