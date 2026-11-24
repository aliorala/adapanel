using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using AdaPanel.Core.Dtos.Databases;
using AdaPanel.Core.Dtos.Migration;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Migration.Full;
using AdaPanel.Infrastructure.Services;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class MigrationDatabaseTargetTests
{
    [Fact]
    public async Task PlanUsesMetadataWithoutCopyingLargePayload()
    {
        var area = new Area();
        var result = await area.Service.CheckAsync(area.Item, area.Manifest, area.Selection, default);
        Assert.Empty(result.Blockers);
        Assert.Equal(0, area.PayloadReads);
        Assert.Empty(area.Importer.LastPlan!.DumpPath);
        Assert.Equal(0, area.Importer.Imports);
    }

    [Fact]
    public async Task ImportCopiesImmutablePayloadUsesPreparedCredentialsAndDeletesTemporary()
    {
        var area = new Area();
        var before = area.Bytes.ToArray();
        var credentials = area.Service.PrepareCredentials(area.Item, area.Manifest);
        var result = await area.Service.ApplyAsync(area.Item, area.Manifest, area.Selection, credentials, default);
        Assert.True(result.Verified, result.ErrorCode);
        Assert.False(result.PartialStatePossible);
        Assert.Equal(before, area.Importer.ImportedBytes);
        Assert.Equal(before, area.Bytes);
        Assert.Equal(credentials[0].Password, area.Importer.LastImport!.TargetPassword);
        Assert.True(area.Importer.LastImport.AdaptDefiners);
        Assert.False(File.Exists(area.Importer.LastImport.DumpPath));
        Assert.Contains("migration.database_source_counts_unmeasured", result.RequiredActions);
        Assert.DoesNotContain(credentials[0].Password, string.Join(" ", result.Evidence.Concat(result.CreatedResources)));
    }

    [Theory]
    [InlineData("short")]
    [InlineData("long")]
    [InlineData("same-length")]
    public async Task CorruptPayloadCannotReachImporter(string change)
    {
        var area = new Area();
        area.Bytes = change == "short" ? [0] : change == "long" ? new byte[area.Bytes.Length + 1] : new byte[area.Bytes.Length];
        var result = await area.Apply();
        Assert.False(result.Verified);
        Assert.Equal("migration.payload_changed", result.ErrorCode);
        Assert.False(result.PartialStatePossible);
        Assert.Equal(0, area.Importer.Imports);
    }

    [Fact]
    public async Task PartialNativeImportIsNotSuccessOrRetried()
    {
        var area = new Area();
        area.Importer.Result = new() { PartialStatePossible = true, ErrorCode = "migration.database_import_failed",
            AttemptedResources = ["user:new_user"], CreatedResources = ["database:new_db"] };
        var result = await area.Apply();
        Assert.False(result.Verified);
        Assert.True(result.PartialStatePossible);
        Assert.Equal("migration.database_import_failed", result.ErrorCode);
        Assert.Contains("database:new_db", result.CreatedResources);
        Assert.Equal(1, area.Importer.Imports);
        Assert.False(File.Exists(area.Importer.LastImport!.DumpPath));
    }

    [Fact]
    public async Task ImportedButUnverifiedIsNotSuccessful()
    {
        var area = new Area();
        area.Importer.Result = new() { Imported = true, CreatedResources = ["database:new_db"] };
        var result = await area.Apply();
        Assert.False(result.Verified);
        Assert.True(result.PartialStatePossible);
        Assert.Equal("migration.database_verification_failed", result.ErrorCode);
    }

    [Fact]
    public async Task MissingPreparedCredentialsCannotMutate()
    {
        var area = new Area();
        var result = await area.Service.ApplyAsync(area.Item, area.Manifest, area.Selection, [], default);
        Assert.Equal("migration.database_credentials_missing", result.ErrorCode);
        Assert.Equal(0, area.PayloadReads);
        Assert.Equal(0, area.Importer.Imports);
    }

    [Fact]
    public async Task ExistingDatabaseOrMetadataBlocksBeforeRead()
    {
        var area = new Area();
        area.Importer.Plan = new() { Supported = true, Blockers = ["migration.target_database_exists"] };
        var result = await area.Apply();
        Assert.Equal("migration.target_database_exists", result.ErrorCode);
        Assert.Equal(0, area.PayloadReads);
        area.Importer.Plan = new() { Supported = true };
        area.Links.Add(new() { Engine = "mariadb", DatabaseName = "new_db", SiteName = "foreign.example" });
        result = await area.Apply();
        Assert.Equal("migration.target_database_link_exists", result.ErrorCode);
        Assert.Equal(0, area.Importer.Imports);
    }

    [Theory]
    [InlineData("postgresql", "sql")]
    [InlineData("mssql", "bak")]
    [InlineData("mariadb", "binary")]
    public async Task UnsupportedEngineOrFormatCannotAppearSuccessful(string engine, string format)
    {
        var area = new Area(engine, format);
        var result = await area.Apply();
        Assert.False(result.Verified);
        Assert.Equal(0, area.Importer.Imports);
        Assert.Equal(0, area.PayloadReads);
    }

    [Fact]
    public async Task SiteLinkIsWrittenAndReadBackAfterImport()
    {
        var area = new Area();
        area.LinkSite();
        var result = await area.Apply();
        Assert.True(result.Verified, result.ErrorCode);
        Assert.Equal("one.example", Assert.Single(area.Links).SiteName);
        Assert.Contains("migration.database_site_link_verified", result.Evidence);
    }

    [Fact]
    public async Task ReplacementSiteDuringImportIsNotAdopted()
    {
        var area = new Area();
        area.LinkSite();
        area.Importer.DuringImport = () => area.Site = new() { Id = 99, Name = "one.example", ApplicationRootPath = area.Site!.ApplicationRootPath,
            PhysicalPath = area.Site.PhysicalPath, ApplicationPoolName = "different" };
        var result = await area.Apply();
        Assert.False(result.Verified);
        Assert.True(result.PartialStatePossible);
        Assert.Empty(area.Links);
    }

    [Fact]
    public async Task WrongApplicationRootCannotAdoptExistingSite()
    {
        var area = new Area();
        area.LinkSite();
        area.Site = new() { Id = 42, Name = "one.example", ApplicationRootPath = Path.Combine(Path.GetTempPath(), "foreign"),
            PhysicalPath = area.Site!.PhysicalPath, ApplicationPoolName = "fixture" };
        var result = await area.Apply();
        Assert.Equal("migration.target_site_changed", result.ErrorCode);
        Assert.Equal(0, area.Importer.Imports);
    }

    [Fact]
    public async Task NativeFailureAfterMutationKeepsUncertainStateAndDoesNotExposeException()
    {
        var area = new Area();
        area.Importer.DuringImport = () => throw new IOException("SECRET SOURCE SQL");
        var result = await area.Apply();
        Assert.Equal("migration.database_import_failed", result.ErrorCode);
        Assert.True(result.PartialStatePossible);
        Assert.Equal(1, area.Importer.Imports);
        Assert.False(File.Exists(area.Importer.LastImport!.DumpPath));
    }

    [Theory]
    [InlineData("renamed_db")]
    [InlineData("NEW_DB")]
    public async Task RenameIsBlockedBeforeReadingOrImportingAnySql(string target)
    {
        var area = new Area();
        area.Item.Target.TargetName = target;
        var check = await area.Service.CheckAsync(area.Item, area.Manifest, area.Selection, default);
        Assert.Contains("migration.database_rename_unsupported", check.Blockers);
        var result = await area.Apply();
        Assert.False(result.Verified);
        Assert.False(result.PartialStatePossible);
        Assert.Equal("migration.database_rename_unsupported", result.ErrorCode);
        Assert.Null(area.Importer.LastPlan);
        Assert.Equal(0, area.PayloadReads);
        Assert.Equal(0, area.Importer.Imports);
    }

    private sealed class Area
    {
        public byte[] Bytes = Encoding.UTF8.GetBytes("CREATE TABLE t (id INT); INSERT INTO t VALUES(1);");
        public int PayloadReads;
        public FakeImporter Importer = new();
        public List<SiteDatabaseLinkDto> Links = [];
        public SiteDto? Site;
        public PortableMigrationManifest Manifest;
        public MigrationPlanItemDto Item;
        public MigrationPlanRequest Selection = new() { TargetBasePath = Path.Combine(Path.GetTempPath(), "migration-fixture-sites") };
        public MigrationDatabaseTarget Service;
        public Area(string engine = "mariadb", string format = "sql")
        {
            var id = Guid.NewGuid();
            Manifest = new() { PackageId = id, Databases = [new() { Id = "db", Name = "new_db", Engine = engine, Format = format, DumpPath = "databases/db.sql" }],
                Payloads = [new() { Path = "databases/db.sql", Size = Bytes.Length, Sha256 = Convert.ToHexString(SHA256.HashData(Bytes)) }] };
            Item = new() { Id = MigrationResourceKey.Create(id, "db"), PackageId = id, Kind = "database", SourceId = "db", SourceName = "new_db",
                Target = new() { TargetName = "new_db", DatabaseUserName = "new_user" } };
            var packages = Stub<IMigrationPackageStore>((method, args) =>
            { if (method.Name != nameof(IMigrationPackageStore.OpenPayloadAsync)) throw new InvalidOperationException();
                PayloadReads++; return Task.FromResult<Stream>(new MemoryStream(Bytes, false)); });
            var links = Stub<ISiteDatabaseLinkService>((method, args) => method.Name switch
            {
                nameof(ISiteDatabaseLinkService.GetAllAsync) => Task.FromResult<IReadOnlyList<SiteDatabaseLinkDto>>(Links.ToArray()),
                nameof(ISiteDatabaseLinkService.GetForSiteAsync) => Task.FromResult<IReadOnlyList<SiteDatabaseLinkDto>>(Links.Where(link => link.SiteName == (string)args![0]!).ToArray()),
                nameof(ISiteDatabaseLinkService.LinkAsync) => Link(args!),
                _ => throw new InvalidOperationException()
            });
            var sites = Stub<IIisSiteService>((method, args) => method.Name == nameof(IIisSiteService.GetSiteAsync)
                ? Task.FromResult(Site!) : throw new InvalidOperationException());
            Service = new(Importer, packages, links, sites, new ConcurrencyLock());
        }
        private Task Link(object?[] args)
        {
            Assert.Equal(1, Importer.Imports);
            Links.Add(new() { Engine = (string)args[0]!, DatabaseName = (string)args[1]!, SiteName = (string)args[2]! });
            return Task.CompletedTask;
        }
        public void LinkSite()
        {
            Manifest.Sites.Add(new() { Id = "site", Name = "one.example" });
            Manifest.Databases[0].SiteIds.Add("site");
            Selection.AllSites = true;
            var root = Path.Combine(Selection.TargetBasePath, "one.example");
            Site = new() { Id = 42, Name = "one.example", ApplicationRootPath = root, PhysicalPath = root, ApplicationPoolName = "fixture" };
        }
        public Task<MigrationComponentResult> Apply() => Service.ApplyAsync(Item, Manifest, Selection, Service.PrepareCredentials(Item, Manifest), default);
    }

    private sealed class FakeImporter : IMigrationDatabaseImporter
    {
        public MigrationDatabasePlan Plan = new() { Supported = true };
        public MigrationDatabaseImportResult Result = new() { Imported = true, Verified = true, CreatedResources = ["database:new_db"], ObjectInventory = ["BASE TABLE:t"] };
        public MigrationDatabaseImportRequest? LastPlan, LastImport;
        public byte[]? ImportedBytes;
        public int Imports;
        public Action? DuringImport;
        public Task<MigrationDatabasePlan> PlanAsync(MigrationDatabaseImportRequest request, CancellationToken token = default)
        { LastPlan = request; return Task.FromResult(Plan); }
        public async Task<MigrationDatabaseImportResult> ImportAsync(MigrationDatabaseImportRequest request, CancellationToken token = default)
        {
            Imports++; LastImport = request;
            ImportedBytes = await File.ReadAllBytesAsync(request.DumpPath, token);
            DuringImport?.Invoke(); return Result;
        }
    }
    private static T Stub<T>(Func<MethodInfo, object?[]?, object?> handler) where T : class
    { var proxy = DispatchProxy.Create<T, Proxy>(); ((Proxy)(object)proxy).Handler = handler; return proxy; }
    public class Proxy : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?> Handler = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => Handler(method!, args);
    }
}
