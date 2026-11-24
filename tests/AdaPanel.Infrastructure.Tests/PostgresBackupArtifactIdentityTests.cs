using System.IO.Compression;
using System.Reflection;
using AdaPanel.Core.Dtos.Backup;
using AdaPanel.Core.Dtos.Databases;
using AdaPanel.Core.Dtos.Settings;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Backup;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Support;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class PostgresBackupArtifactIdentityTests
{
    [Fact]
    public async Task CaseDistinctDatabases_GetSeparateWindowsFoldersAndRestoreExactContents()
    {
        using var fixture = new BackupFixture();
        var lower = await fixture.Backup("postgresql", "foo");
        var upper = await fixture.Backup("postgresql", "Foo");

        Assert.False(StringComparer.OrdinalIgnoreCase.Equals(Path.GetDirectoryName(lower.Path), Path.GetDirectoryName(upper.Path)),
            "Distinct PostgreSQL databases must not share a Windows source folder.");
        Assert.False(StringComparer.OrdinalIgnoreCase.Equals(lower.Path, upper.Path));
        await fixture.Service.RestoreAsync(new RestoreBackupRequest { BackupId = lower.Id });
        await fixture.Service.RestoreAsync(new RestoreBackupRequest { BackupId = upper.Id });
        Assert.Equal([("postgresql", "foo", "-- foo export 1"), ("postgresql", "Foo", "-- Foo export 2")], fixture.Imports);
    }

    [Fact]
    public async Task LongCaseDistinctNames_StaySeparateAfterFolderLabelTruncation()
    {
        using var fixture = new BackupFixture();
        var name = new string('a', 55);
        var lower = await fixture.Backup("postgresql", name + "x");
        var upper = await fixture.Backup("postgresql", name + "X");
        Assert.False(StringComparer.OrdinalIgnoreCase.Equals(Path.GetDirectoryName(lower.Path), Path.GetDirectoryName(upper.Path)));
    }

    [Fact]
    public async Task EngineAliases_KeepSameSourceFolderAndDistinctBackupFiles()
    {
        using var fixture = new BackupFixture();
        var canonical = await fixture.Backup("postgresql", "Foo");
        var alias = await fixture.Backup("postgres", "Foo");
        Assert.Equal(Path.GetDirectoryName(canonical.Path), Path.GetDirectoryName(alias.Path));
        Assert.False(StringComparer.OrdinalIgnoreCase.Equals(canonical.Path, alias.Path));
    }

    [Theory]
    [InlineData("postgresql")]
    [InlineData("mysql")]
    public async Task RepeatedBackups_DoNotOverwriteEarlierCompletedArtifacts(string engine)
    {
        using var fixture = new BackupFixture();
        var records = new List<BackupRecordDto>();
        for (var i = 0; i < 12; i++)
        {
            records.Add(await fixture.Backup(engine, "foo"));
        }

        Assert.Equal(records.Count, records.Select(record => record.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        for (var i = 0; i < records.Count; i++)
        {
            await fixture.Service.RestoreAsync(new RestoreBackupRequest { BackupId = records[i].Id });
            Assert.Equal((engine, "foo", $"-- foo export {i + 1}"), fixture.Imports[^1]);
        }
    }

    [Fact]
    public async Task LegacyBackup_StillRestoresUsingRecordedPathAndExactSource()
    {
        using var fixture = new BackupFixture();
        var path = Path.Combine(fixture.Root, "postgresql-Foo-20260901000000.sql");
        await File.WriteAllTextAsync(path, "-- legacy uppercase");
        await fixture.Store.WriteAsync("config/backups.json", new[]
        {
            new BackupRecordDto { Id = "legacy", Path = path, Name = Path.GetFileName(path), Source = "postgresql:Foo", Type = "database", Format = "sql", Status = "created" }
        });
        var restored = await fixture.Service.RestoreAsync(new RestoreBackupRequest { BackupId = "legacy" });
        Assert.Equal("restored", restored.Status);
        Assert.Equal(("postgresql", "Foo", "-- legacy uppercase"), Assert.Single(fixture.Imports));
    }

    [Fact]
    public async Task SubscriptionArchive_RoundTripsBothPostgresCasesWithoutFilenameChanges()
    {
        using var fixture = new BackupFixture();
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            Assert.True(await Invoke<bool>(fixture.Service, "TryAddDatabaseDumpAsync", archive, "postgresql", "foo", "databases", CancellationToken.None));
            Assert.True(await Invoke<bool>(fixture.Service, "TryAddDatabaseDumpAsync", archive, "postgresql", "Foo", "databases", CancellationToken.None));
        }
        stream.Position = 0;
        using var reopened = new ZipArchive(stream, ZipArchiveMode.Read);
        var warnings = new List<BackupRestoreWarningDto>();
        await InvokeTask(fixture.Service, "ImportDatabasesFromArchiveAsync", reopened, "databases/", warnings, false, CancellationToken.None);
        Assert.Empty(warnings);
        Assert.Equal([("postgresql", "foo", "-- foo export 1"), ("postgresql", "Foo", "-- Foo export 2")], fixture.Imports);
    }

    private static Task<T> Invoke<T>(object service, string method, params object[] args) =>
        (Task<T>)service.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(service, args)!;

    private static Task InvokeTask(object service, string method, params object[] args) =>
        (Task)service.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(service, args)!;

    private sealed class BackupFixture : IDisposable
    {
        private int exports;
        public string Root { get; } = Path.Combine(Path.GetTempPath(), $"adapanel-pg-backup-identity-{Guid.NewGuid():N}");
        public JsonFileStore Store { get; }
        public BackupService Service { get; }
        public List<(string Engine, string Database, string Content)> Imports { get; } = [];

        public BackupFixture()
        {
            Directory.CreateDirectory(Root);
            Store = new JsonFileStore(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = Root }));
            var database = Fake<IDatabaseService>((method, args) => method.Name switch
            {
                "ExportDumpAsync" => Export((DatabaseDumpRequest)args[0]!),
                "ImportDumpAsync" => Import((DatabaseDumpRequest)args[0]!),
                _ => throw new NotSupportedException(method.Name)
            });
            var preferences = Fake<IPanelPreferencesService>((method, _) => method.Name switch
            {
                "GetAsync" => Task.FromResult(new PanelPreferencesDto { DefaultBackupRoot = Path.Combine(Root, "backups") }),
                _ => throw new NotSupportedException(method.Name)
            });
            Service = new BackupService(null!, database, null!, preferences, null!, Store, null!);
        }

        public Task<BackupRecordDto> Backup(string engine, string name) => Service.BackupDatabaseAsync(new BackupDatabaseRequest { Engine = engine, DatabaseName = name });

        private async Task<DatabaseDumpResultDto> Export(DatabaseDumpRequest request)
        {
            var contents = $"-- {request.DatabaseName} export {++exports}";
            await File.WriteAllTextAsync(request.FilePath, contents);
            return new DatabaseDumpResultDto { Engine = request.Engine, DatabaseName = request.DatabaseName, FilePath = request.FilePath, Size = contents.Length };
        }

        private async Task<DatabaseDumpResultDto> Import(DatabaseDumpRequest request)
        {
            Imports.Add((request.Engine, request.DatabaseName, await File.ReadAllTextAsync(request.FilePath)));
            return new DatabaseDumpResultDto { Engine = request.Engine, DatabaseName = request.DatabaseName, FilePath = request.FilePath };
        }

        public void Dispose()
        {
            var absolute = Path.GetFullPath(Root);
            Assert.StartsWith(Path.Combine(Path.GetTempPath(), "adapanel-pg-backup-identity-"), absolute, StringComparison.OrdinalIgnoreCase);
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
