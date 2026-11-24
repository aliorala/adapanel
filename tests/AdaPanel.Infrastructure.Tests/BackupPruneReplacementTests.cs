using System.IO.Compression;
using AdaPanel.Core.Dtos.Backup;
using AdaPanel.Infrastructure.Backup;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Support;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class BackupPruneReplacementTests
{
    public static TheoryData<bool, string> IncompleteCases => new()
    {
        { false, "empty-dump" }, { true, "empty-dump" },
        { false, "failed-dump" }, { true, "failed-dump" },
        { false, "skipped-file" }, { true, "skipped-file" },
        { false, "status-only" }, { true, "status-only" },
        { false, "contradictory-status" }, { true, "contradictory-status" },
        { false, "failed-status" }, { true, "failed-status" }
    };

    [Theory]
    [MemberData(nameof(IncompleteCases))]
    public async Task IncompleteReplacementBlocksCountPruningButNotExplicitAgeExpiry(bool countPolicy, string failure)
    {
        using var fixture = new Fixture(countPolicy);
        var complete = fixture.Record("complete", 40, Included("foo", "Foo"));
        var incomplete = fixture.Record("incomplete", 1,
            failure is "empty-dump" or "failed-dump" ? Included("foo") : Included("foo", "Foo"));
        incomplete.Status = failure == "failed-status" ? "failed" : "created_with_errors";
        if (failure != "status-only" && failure != "failed-status")
        {
            incomplete.Warnings.Add(new BackupRestoreWarningDto
            {
                MessageKey = failure switch
                {
                    "failed-dump" => "backup.database_dump_failed",
                    "skipped-file" => "backup.source_file_unreadable",
                    _ => "backup.database_dump_empty"
                },
                Target = failure == "skipped-file" ? "files/important.txt" : "postgresql/Foo"
            });
        }
        if (failure == "contradictory-status") incomplete.Status = "created";
        await fixture.Save(complete, incomplete);

        var result = await fixture.Prune();

        Assert.Equal(countPolicy ? 0 : 1, result.Removed);
        await fixture.AssertPresent(countPolicy ? [complete, incomplete] : [incomplete]);
    }

    [Theory]
    [InlineData(false, "reduced")]
    [InlineData(true, "reduced")]
    [InlineData(false, "empty")]
    [InlineData(true, "empty")]
    [InlineData(false, "unknown")]
    [InlineData(true, "unknown")]
    [InlineData(false, "legacy-to-empty")]
    [InlineData(true, "legacy-to-empty")]
    [InlineData(false, "empty-to-legacy")]
    [InlineData(true, "empty-to-legacy")]
    public async Task CoverageReplacementIsRequiredOnlyForCountPruning(bool countPolicy, string scenario)
    {
        using var fixture = new Fixture(countPolicy);
        var complete = fixture.Record("complete", 40, scenario == "legacy-to-empty" ? null : scenario == "empty-to-legacy" ? [] : Included("foo", "Foo"));
        var smaller = fixture.Record("smaller", 1, scenario switch
        {
            "reduced" => Included("foo"),
            "unknown" or "empty-to-legacy" => null,
            _ => []
        });
        await fixture.Save(complete, smaller);

        var result = await fixture.Prune();

        Assert.Equal(countPolicy ? 0 : 1, result.Removed);
        await fixture.AssertPresent(countPolicy ? [complete, smaller] : [smaller]);
    }

    [Theory]
    [InlineData(false, "measured")]
    [InlineData(true, "measured")]
    [InlineData(false, "empty")]
    [InlineData(true, "empty")]
    [InlineData(false, "legacy")]
    [InlineData(true, "legacy")]
    [InlineData(false, "advisory")]
    [InlineData(true, "advisory")]
    [InlineData(false, "remote-failed-local")]
    [InlineData(true, "remote-failed-local")]
    [InlineData(false, "remote-only")]
    [InlineData(true, "remote-only")]
    public async Task ACompleteReplacementStillAllowsNormalRetention(bool countPolicy, string scenario)
    {
        using var fixture = new Fixture(countPolicy);
        var complete = fixture.Record("complete", 40, scenario == "legacy" ? null : scenario == "empty" ? [] : Included("foo", "Foo"));
        var replacement = fixture.Record("replacement", 1, complete.IncludedDatabases);
        if (scenario == "legacy")
        {
            complete.IntegrityState = BackupIntegrity.Unknown;
            replacement.IntegrityState = BackupIntegrity.Unknown;
        }
        if (scenario == "measured") replacement.IncludedDatabases![0].Engine = "postgres";
        if (scenario == "advisory") replacement.Warnings.Add(new() { MessageKey = "backup.compression_failed" });
        if (scenario == "remote-failed-local")
        {
            replacement.Status = "created_remote_failed";
            replacement.Warnings.Add(new() { MessageKey = "backup.remote_upload_failed" });
        }
        if (scenario == "remote-only")
        {
            File.Delete(replacement.Path);
            replacement.RemoteUri = "https://backup.example.test/replacement.zip";
            replacement.Status = "created_remote_synced";
            replacement.RemoteState = BackupPresence.Present;
        }
        await fixture.Save(complete, replacement);

        var result = await fixture.Prune();

        Assert.Equal(1, result.Removed);
        Assert.False(File.Exists(complete.Path));
        Assert.Equal(replacement.Id, Assert.Single(await fixture.Service.GetBackupsAsync()).Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IncompleteActiveChainCannotReplaceForCountPruningButDoesNotOverrideIndependentExpiry(bool countPolicy)
    {
        using var fixture = new Fixture(countPolicy);
        var complete = fixture.Record("complete", 40, Included("foo", "Foo"));
        var chainBase = fixture.Record("chain-base", 20, Included("foo"));
        chainBase.Status = "created_with_errors";
        chainBase.Warnings.Add(new() { MessageKey = "backup.source_file_unreadable", Target = "files/important.txt" });
        chainBase.ChainId = "partial-chain";
        var recent = fixture.Record("chain-latest", 1, Included("foo", "Foo"));
        recent.ChainId = chainBase.ChainId;
        recent.ChainIndex = 1;
        recent.BackupMode = "incremental";
        recent.BaseBackupId = chainBase.Id;
        await fixture.Save(complete, chainBase, recent);

        var result = await fixture.Prune();

        Assert.Equal(countPolicy ? 0 : 1, result.Removed);
        await fixture.AssertPresent(countPolicy ? [complete, chainBase, recent] : [chainBase, recent]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyChainCoverageBlocksCountPruningButNotWholeChainAgeExpiry(bool countPolicy)
    {
        using var fixture = new Fixture(countPolicy);
        var chainBase = fixture.Record("legacy-base", 40, null);
        chainBase.ChainId = "legacy-chain";
        var measured = fixture.Record("measured-delta", 39, Included("foo", "Foo"));
        measured.ChainId = chainBase.ChainId;
        measured.ChainIndex = 1;
        measured.BackupMode = "incremental";
        measured.BaseBackupId = chainBase.Id;
        var replacement = fixture.Record("unknown", 1, null);
        await fixture.Save(chainBase, measured, replacement);
        Assert.Equal(countPolicy ? 0 : 2, (await fixture.Prune()).Removed);
        await fixture.AssertPresent(countPolicy ? [chainBase, measured, replacement] : [replacement]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidCoverageBlocksCountReplacementButNotExplicitAgeExpiry(bool countPolicy)
    {
        using var fixture = new Fixture(countPolicy);
        var previous = fixture.Record("invalid-reference", 40, []);
        previous.IncludedDatabases = [null!];
        var replacement = fixture.Record("empty", 1, []);
        await fixture.Save(previous, replacement);
        Assert.Equal(countPolicy ? 0 : 1, (await fixture.Prune()).Removed);
        await fixture.AssertPresent(countPolicy ? [previous, replacement] : [replacement]);
    }

    [Fact]
    public async Task APartialArchiveStillHasAnAvailableChainAndCanRestoreItsPresentFiles()
    {
        using var fixture = new Fixture(false);
        var partial = fixture.Record("partial", 1, []);
        partial.Status = "created_with_errors";
        partial.Warnings.Add(new() { MessageKey = "backup.database_dump_empty", Target = "postgresql/missing" });
        await fixture.Save(partial);

        var chain = await fixture.Service.GetChainAsync(partial.Id);
        Assert.False(chain.Broken);
        Assert.True(Assert.Single(chain.Links).Available);
        var restored = await fixture.Service.RestoreAsync(new RestoreBackupRequest { BackupId = partial.Id });
        Assert.Equal(partial.Path, restored.Path);
        Assert.Equal("partial", await File.ReadAllTextAsync(Path.Combine(restored.RestoredPath, "available.txt")));
    }

    private static List<BackupDatabaseRef> Included(params string[] names) =>
        names.Select(name => new BackupDatabaseRef { Engine = "postgresql", DatabaseName = name }).ToList();

    private sealed class Fixture : IDisposable
    {
        private static readonly Guid Owner = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000013");
        private readonly bool countPolicy;
        private readonly JsonFileStore store;
        public string Root { get; } = Path.Combine(Path.GetTempPath(), $"adapanel-prune-replacement-{Guid.NewGuid():N}");
        public BackupService Service { get; }

        public Fixture(bool countPolicy)
        {
            this.countPolicy = countPolicy;
            Directory.CreateDirectory(Root);
            store = new JsonFileStore(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = Root }));
            Service = new BackupService(null!, null!, null!, null!, null!, store, null!);
        }

        public BackupRecordDto Record(string id, int daysOld, IReadOnlyCollection<BackupDatabaseRef>? included)
        {
            var path = Path.Combine(Root, $"{id}.zip");
            using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
            using (var writer = new StreamWriter(archive.CreateEntry("available.txt").Open())) writer.Write(id);
            return new BackupRecordDto
            {
                Id = id, Name = $"{id}.zip", Path = path, Format = "zip",
                Type = countPolicy ? "subscription" : "site",
                Source = countPolicy ? Owner.ToString("N") : "site.test",
                SubscriptionUserId = countPolicy ? Owner.ToString("N") : string.Empty,
                Status = "created", IntegrityState = BackupIntegrity.Sound, Size = new FileInfo(path).Length,
                CreatedAtUtc = DateTime.UtcNow.AddDays(-daysOld),
                IncludedDatabases = included?.Select(db => new BackupDatabaseRef { Engine = db.Engine, DatabaseName = db.DatabaseName }).ToList()
            };
        }

        public Task Save(params BackupRecordDto[] records) => store.WriteAsync("config/backups.json", records);
        public Task<BackupPruneResultDto> Prune() => countPolicy
            ? Service.PruneSubscriptionAsync(Owner, retentionDays: 0, keepCount: 1)
            : Service.PruneAsync(14);

        public async Task AssertPresent(params BackupRecordDto[] records)
        {
            var saved = await Service.GetBackupsAsync();
            Assert.Equal(records.Select(record => record.Id).Order(), saved.Select(record => record.Id).Order());
            Assert.All(records, record => Assert.True(File.Exists(record.Path), record.Id));
        }

        public void Dispose()
        {
            var absolute = Path.GetFullPath(Root);
            Assert.StartsWith(Path.Combine(Path.GetTempPath(), "adapanel-prune-replacement-"), absolute, StringComparison.OrdinalIgnoreCase);
            Directory.Delete(absolute, recursive: true);
        }
    }
}
