using System.Reflection;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Backup;
using AdaPanel.Core.Dtos.Databases;
using AdaPanel.Core.Dtos.Settings;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Backup;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Support;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class BackupPlannerSourceIdentityTests
{
    private static readonly DateTime Now = new(2026, 10, 3, 0, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("postgresql:Foo", "postgresql:foo")]
    [InlineData("POSTGRESQL:Foo", "postgresql:foo")]
    [InlineData("postgres:Foo", "postgresql:foo")]
    public void AnotherPostgresDatabaseDoesNotSatisfyCountLimitWithinTheAgeWindow(string oldSource, string recentSource)
    {
        var old = Record("old", oldSource, 10);
        var recent = Record("recent", recentSource, 1);
        Assert.Empty(BackupPrunePlanner.Select([old, recent], Now.AddDays(-14), 1, _ => true));
    }

    [Theory]
    [InlineData("postgresql:Foo", "postgresql:foo")]
    [InlineData("POSTGRES:Foo", "postgres:foo")]
    public void KeepCountProtectsEachDistinctPostgresDatabase(string oldSource, string recentSource)
    {
        var old = Record("old", oldSource, 10);
        var recent = Record("recent", recentSource, 1);
        Assert.Empty(BackupPrunePlanner.Select([old, recent], DateTime.MinValue, 1, _ => true));
    }

    [Theory]
    [InlineData("postgres:Foo", "postgresql:Foo")]
    [InlineData("POSTGRESQL:Foo", "postgres:Foo")]
    [InlineData("sqlserver:Orders", "mssql:orders")]
    [InlineData("MSSQL:Orders", "SQLSERVER:ORDERS")]
    [InlineData("mysql:Orders", "MYSQL:orders")]
    [InlineData("mariadb:Orders", "MARIADB:orders")]
    public void SameDatabaseAliasesAndExistingCaseInsensitiveEnginesStillAllowPruning(string oldSource, string recentSource)
    {
        var old = Record("old", oldSource, 40);
        var recent = Record("recent", recentSource, 1);
        Assert.Equal(["old"], BackupPrunePlanner.Select([old, recent], Now.AddDays(-14), 0, _ => true).Select(item => item.Id));
        Assert.Equal(["old"], BackupPrunePlanner.Select([old, recent], DateTime.MinValue, 1, _ => true).Select(item => item.Id));
    }

    [Fact]
    public void SiteSourceCaseFoldingAndTypeSeparationArePreserved()
    {
        var old = Record("old", "SHOP.Example", 40, type: "SITE");
        var recent = Record("recent", "shop.example", 1, type: "site");
        var unrelated = Record("database", "shop.example", 40);
        Assert.Equal(["old"], BackupPrunePlanner.Select([old, recent, unrelated], DateTime.MinValue, 1, _ => true).Select(item => item.Id));
    }

    [Fact]
    public void ExplicitAgeLimitExpiresEveryOldPointRegardlessOfMinimumKeepCount()
    {
        var records = new[] { 1, 20, 30, 40 }.Select(days => Record("backup-" + days, "postgresql:Foo", days)).ToArray();
        Assert.Equal(["backup-20", "backup-30", "backup-40"],
            BackupPrunePlanner.Select(records, Now.AddDays(-14), 3, _ => true).Select(item => item.Id));
        Assert.Equal(["backup-40"], BackupPrunePlanner.Select(records, DateTime.MinValue, 3, _ => true).Select(item => item.Id));
    }

    [Fact]
    public void AnOrphanCannotReplaceAFullBackupForCountPruningButDoesNotOverrideExplicitExpiry()
    {
        var old = Record("old-full", "postgresql:Foo", 40);
        var orphan = Record("orphan", "postgresql:Foo", 1, "broken-chain", 1);
        Assert.Equal([old.Id], BackupPrunePlanner.Select([old, orphan], Now.AddDays(-14), 0, _ => true).Select(item => item.Id));
        Assert.Empty(BackupPrunePlanner.Select([old, orphan], DateTime.MinValue, 1, _ => true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EqualTimestampDoesNotProveWhichRecoveryPointCanReplaceTheOther(bool reverseOrder)
    {
        var first = Record("first", "postgres:Foo", 1);
        var second = Record("second", "postgresql:Foo", 1);
        BackupRecordDto[] records = reverseOrder ? [second, first] : [first, second];
        Assert.Empty(BackupPrunePlanner.Select(records, DateTime.MinValue, 1, _ => true));
    }

    [Theory]
    [InlineData("duplicate-index")]
    [InlineData("wrong-parent")]
    [InlineData("incremental-base")]
    [InlineData("negative-index")]
    public void AmbiguousOrInvalidChainMetadataCannotBeRestored(string fault)
    {
        var records = BrokenChain(fault, (id, index) => Record(id, "postgresql:Foo", 10 - index, "shared", index));
        var chain = BackupChainPlanner.Resolve(records, "target", _ => true);
        Assert.True(chain.Broken);
    }

    [Theory]
    [InlineData("duplicate-index")]
    [InlineData("wrong-parent")]
    [InlineData("incremental-base")]
    [InlineData("negative-index")]
    public async Task RealRestoreRejectsInvalidChainMetadataBeforeImporting(string fault)
    {
        using var fixture = new Fixture();
        var records = BrokenChain(fault, (id, index) => fixture.Stored(id, "postgresql:Foo", 10 - index, "shared", index));
        await fixture.Save(records);
        var error = await Assert.ThrowsAsync<AppException>(() => fixture.Service().RestoreAsync(new RestoreBackupRequest { BackupId = "target" }));
        Assert.Equal("backup.chain_broken", error.MessageKey);
        Assert.Equal(0, fixture.Imports);
    }

    [Fact]
    public void ExtremeIndexDescriptorsCannotRequestUnboundedTraversal()
    {
        // Test the numeric guard directly; never run the former unbounded loop with
        // int.MaxValue, which could hang a test host even after a task timeout.
        Assert.Equal(3, BackupChainPlanner.SequenceInspectionLimit(int.MaxValue, 2));
        Assert.Equal(0, BackupChainPlanner.SequenceInspectionLimit(int.MinValue, 2));
        Assert.Equal(0, BackupChainPlanner.SequenceInspectionLimit(-1, 2));
        Assert.Equal(2, BackupChainPlanner.SequenceInspectionLimit(1, 2));
    }

    [Fact]
    public void SparseChainReportsMissingPositionsWithABoundedDescriptor()
    {
        var first = Record("base", "postgresql:Foo", 10, "shared", 0);
        var target = Record("target", "postgresql:Foo", 1, "shared", 100);
        var chain = BackupChainPlanner.Resolve([first, target], target.Id, _ => true);
        Assert.True(chain.Broken);
        Assert.InRange(chain.MissingLinks.Length, 1, 3);
        Assert.Contains("#1", chain.MissingLinks);
        Assert.Equal([first.Id, target.Id], chain.Links.Select(link => link.BackupId));
    }

    [Fact]
    public void LegacyBlankModeAndParentDescriptorsRemainRestorable()
    {
        var first = Record("base", "postgres:Foo", 10, "shared", 0);
        var target = Record("target", "postgresql:Foo", 1, "shared", 1);
        first.BackupMode = target.BackupMode = "";
        var chain = BackupChainPlanner.Resolve([first, target], target.Id, _ => true);
        Assert.False(chain.Broken);
        Assert.Equal([first.Id, target.Id], chain.Links.Select(link => link.BackupId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReusedChainIdCannotCombineDifferentPostgresSourcesForCountPruning(bool reverseOrder)
    {
        var lower = Record("lower-old", "postgresql:foo", 40, "shared", 0);
        var upper = Record("upper-old", "postgresql:Foo", 40, "shared", 0);
        var current = Record("lower-current", "postgresql:foo", 1);
        BackupRecordDto[] records = reverseOrder ? [upper, lower, current] : [lower, upper, current];
        Assert.Equal(["lower-old"], BackupPrunePlanner.Select(records, DateTime.MinValue, 1, _ => true).Select(item => item.Id));
    }

    [Fact]
    public void AForeignPostgresBaseDoesNotRepairTheTargetChain()
    {
        var foreignBase = Record("foreign-base", "postgresql:foo", 10, "shared", 0);
        var target = Record("target", "postgresql:Foo", 1, "shared", 1);
        var chain = BackupChainPlanner.Resolve([foreignBase, target], target.Id, _ => true);
        Assert.True(chain.Broken);
        Assert.Contains("#0", chain.MissingLinks);
        Assert.DoesNotContain(chain.Links, item => item.BackupId == foreignBase.Id);
    }

    [Theory]
    [InlineData("postgres:Foo", "postgresql:Foo")]
    [InlineData("mysql:FOO", "mysql:foo")]
    [InlineData("sqlserver:FOO", "mssql:foo")]
    public void AValidBaseIsSelectedWithoutMixingAnotherSource(string baseSource, string targetSource)
    {
        var foreignBase = Record("foreign-base", "postgresql:other", 10, "shared", 0);
        var ownBase = Record("own-base", baseSource, 10, "shared", 0);
        var target = Record("target", targetSource, 1, "shared", 1);
        var chain = BackupChainPlanner.Resolve([foreignBase, ownBase, target], target.Id, _ => true);
        Assert.False(chain.Broken);
        Assert.Equal([ownBase.Id, target.Id], chain.Links.Select(item => item.BackupId));
    }

    [Fact]
    public void DependentsAreScopedToTheSameSourceAndType()
    {
        var ownBase = Record("base", "postgres:Foo", 10, "shared", 0);
        var ownNext = Record("next", "postgresql:Foo", 1, "shared", 1);
        var foreign = Record("foreign", "postgresql:foo", 1, "shared", 2);
        var otherType = Record("site", "postgresql:Foo", 1, "shared", 2, "site");
        Assert.Equal([ownNext.Id], BackupChainPlanner.GetDependents([ownBase, ownNext, foreign, otherType], ownBase).Select(item => item.Id));
    }

    [Fact]
    public async Task RealDatabaseProducerPreservesQuotedSourcesForCountPruningWhileAgeLimitExpiresOldCopy()
    {
        using var fixture = new Fixture();
        var service = fixture.Service();
        var old = await service.BackupDatabaseAsync(new BackupDatabaseRequest { Engine = "postgres", DatabaseName = "Foo" });
        old.CreatedAtUtc = DateTime.UtcNow.AddDays(-40);
        var recent = await service.BackupDatabaseAsync(new BackupDatabaseRequest { Engine = "postgresql", DatabaseName = "foo" });
        await fixture.Save(old, recent);
        var stored = await service.GetBackupsAsync();
        Assert.Equal("postgresql:Foo", old.Source);
        Assert.Equal("postgresql:foo", recent.Source);
        Assert.Empty(BackupPrunePlanner.Select(stored, DateTime.MinValue, 1, record => File.Exists(record.Path)));
        Assert.True(File.Exists(old.Path));
        var result = await service.PruneAsync(14);
        Assert.Equal(1, result.Removed);
        Assert.False(File.Exists(old.Path));
        Assert.True(File.Exists(recent.Path));
        Assert.Equal(recent.Id, Assert.Single(await service.GetBackupsAsync()).Id);
    }

    [Theory]
    [InlineData("postgres:Foo", "postgresql:Foo")]
    [InlineData("sqlserver:Orders", "mssql:orders")]
    public async Task RealPruneRecognizesLegacyAliases(string oldSource, string recentSource)
    {
        using var fixture = new Fixture();
        var old = fixture.Stored("old", oldSource, 40);
        var recent = fixture.Stored("recent", recentSource, 1);
        await fixture.Save(old, recent);
        var result = await fixture.Service().PruneAsync(14);
        Assert.Equal(1, result.Removed);
        Assert.False(File.Exists(old.Path));
        Assert.True(File.Exists(recent.Path));
    }

    [Fact]
    public async Task RealChainInspectionRejectsForeignBase()
    {
        using var fixture = new Fixture();
        var foreign = fixture.Stored("foreign", "postgresql:foo", 10, "shared", 0);
        var target = fixture.Stored("target", "postgresql:Foo", 1, "shared", 1);
        await fixture.Save(foreign, target);
        var chain = await fixture.Service().GetChainAsync(target.Id);
        Assert.True(chain.Broken);
        Assert.DoesNotContain(chain.Links, item => item.BackupId == foreign.Id);
    }

    [Fact]
    public async Task RealRestoreRejectsForeignBaseBeforeCallingDatabaseImporter()
    {
        using var fixture = new Fixture();
        var foreign = fixture.Stored("foreign", "postgresql:foo", 10, "shared", 0);
        var target = fixture.Stored("target", "postgresql:Foo", 1, "shared", 1);
        await fixture.Save(foreign, target);
        var error = await Assert.ThrowsAsync<AppException>(() => fixture.Service().RestoreAsync(new RestoreBackupRequest { BackupId = target.Id }));
        Assert.Equal("backup.chain_broken", error.MessageKey);
        Assert.Equal(0, fixture.Imports);
    }

    [Fact]
    public async Task RealDeleteChainKeepsAnotherQuotedDatabaseWithTheSameChainId()
    {
        using var fixture = new Fixture();
        var own = fixture.Stored("own", "postgresql:Foo", 10, "shared", 0);
        var foreign = fixture.Stored("foreign", "postgresql:foo", 1, "shared", 1);
        await fixture.Save(own, foreign);
        var result = await fixture.Service().DeleteBackupChainAsync(own.Id);
        Assert.Equal([own.Id], result.DeletedIds);
        Assert.True(File.Exists(foreign.Path));
        Assert.Equal(foreign.Id, Assert.Single(await fixture.Service().GetBackupsAsync()).Id);
    }

    [Fact]
    public async Task RealSingleDeleteIsNotBlockedByAnotherSourcesChainLink()
    {
        using var fixture = new Fixture();
        var own = fixture.Stored("own", "postgresql:Foo", 10, "shared", 0);
        var foreign = fixture.Stored("foreign", "postgresql:foo", 1, "shared", 1);
        await fixture.Save(own, foreign);
        await fixture.Service().DeleteBackupAsync(own.Id);
        Assert.False(File.Exists(own.Path));
        Assert.True(File.Exists(foreign.Path));
    }

    private static BackupRecordDto Record(string id, string source, int days, string chain = "", int index = 0, string type = "database") => new()
    {
        Id = id, Name = id + ".sql", Type = type, Source = source, Format = "sql", Status = "created",
        CreatedAtUtc = Now.AddDays(-days), ChainId = chain, ChainIndex = index,
        BackupMode = index == 0 ? "full" : "incremental"
    };

    private static BackupRecordDto[] BrokenChain(string fault, Func<string, int, BackupRecordDto> create)
    {
        var first = create("base", 0);
        var target = create("target", 1);
        target.BaseBackupId = first.Id;
        switch (fault)
        {
            case "duplicate-index": return [first, create("duplicate", 0), target];
            case "wrong-parent": target.BaseBackupId = "unrelated-base"; break;
            case "incremental-base": first.BackupMode = "incremental"; break;
            case "negative-index": target.ChainIndex = -1; return [target];
        }
        return [first, target];
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "AdaPanel.BackupIdentity." + Guid.NewGuid().ToString("N"));
        private readonly JsonFileStore files;
        public int Imports { get; private set; }

        public Fixture()
        {
            files = new JsonFileStore(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = root }));
            Directory.CreateDirectory(root);
        }

        public BackupRecordDto Stored(string id, string source, int days, string chain = "", int index = 0)
        {
            var record = Record(id, source, days, chain, index);
            record.CreatedAtUtc = DateTime.UtcNow.AddDays(-days);
            record.Path = Path.Combine(root, id + ".sql");
            File.WriteAllText(record.Path, "-- Fixture dump\nCREATE TABLE fixture (id int);\n");
            record.Size = new FileInfo(record.Path).Length;
            record.IntegrityState = BackupIntegrity.Sound;
            var sourceParts = source.Split(':', 2);
            record.IncludedDatabases = [new() { Engine = sourceParts[0], DatabaseName = sourceParts[1] }];
            return record;
        }

        public Task Save(params BackupRecordDto[] records) => files.WriteAsync("config/backups.json", records);

        public BackupService Service() => new(null!, Fake<IDatabaseService>((method, args) =>
        {
            var request = (DatabaseDumpRequest)args[0]!;
            if (method.Name == nameof(IDatabaseService.ExportDumpAsync))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(request.FilePath)!);
                File.WriteAllText(request.FilePath, "-- Fixture dump\nCREATE TABLE fixture (id int);\n");
            }
            else if (method.Name == nameof(IDatabaseService.ImportDumpAsync)) Imports++;
            else throw new NotSupportedException(method.Name);
            return Task.FromResult(new DatabaseDumpResultDto
            {
                Engine = DatabaseResourceIdentity.NormalizeEngine(request.Engine), DatabaseName = request.DatabaseName,
                FilePath = request.FilePath, Size = new FileInfo(request.FilePath).Length
            });
        }), null!, Fake<IPanelPreferencesService>((method, _) => method.Name == nameof(IPanelPreferencesService.GetAsync)
            ? Task.FromResult(new PanelPreferencesDto { DefaultBackupRoot = Path.Combine(root, "backups") })
            : throw new NotSupportedException(method.Name)), null!, files, null!);

        public void Dispose() => Directory.Delete(root, recursive: true);
    }

    private static T Fake<T>(Func<MethodInfo, object?[], object?> handler) where T : class
    {
        var proxy = DispatchProxy.Create<T, Proxy>();
        ((Proxy)(object)proxy).Handler = handler;
        return proxy;
    }

    public class Proxy : DispatchProxy
    {
        public Func<MethodInfo, object?[], object?> Handler { get; set; } = null!;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handler(targetMethod!, args ?? []);
    }
}
