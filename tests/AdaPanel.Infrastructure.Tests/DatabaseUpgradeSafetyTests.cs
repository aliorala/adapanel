using System.Data.Common;
using AdaPanel.Core.Entities;
using AdaPanel.Data.Extensions;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Audit;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Services;
using AdaPanel.Infrastructure.Support;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Açılış yükseltmesi (yedek / başarısızlık / WAL / busy timeout) + denetim kaydı dayanıklılığı
/// (parti silme, dizin, yazım hatası politikası).
/// </summary>
public sealed class DatabaseUpgradeSafetyTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "adapanel-upg-" + Guid.NewGuid().ToString("N"));
    private string DbPath => Path.Combine(root, "data", "adapanel.db");
    private string BackupDir => Path.Combine(root, "backups", "db");

    public DatabaseUpgradeSafetyTests()
    {
        Directory.CreateDirectory(Path.Combine(root, "data"));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(root, recursive: true); } catch (IOException) { }
    }

    private AdaPanelDbContext NewContext(params IInterceptor[] interceptors)
    {
        var builder = new DbContextOptionsBuilder<AdaPanelDbContext>()
            .UseSqlite($"Data Source={DbPath};Pooling=False");
        if (interceptors.Length > 0) builder.AddInterceptors(interceptors);
        return new AdaPanelDbContext(builder.Options);
    }

    private static string[] AllMigrations(AdaPanelDbContext db) => db.Database.GetMigrations().ToArray();

    /// <summary>Şemayı SON migration hariç uygular (güncelleme senaryosu: bir migration bekliyor).</summary>
    private static async Task MigrateToPenultimateAsync(AdaPanelDbContext db)
    {
        var all = AllMigrations(db);
        await db.GetService<IMigrator>().MigrateAsync(all[^2]);
    }

    private static async Task<long> ScalarLongAsync(DbConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Fresh_empty_database_is_migrated_without_backup()
    {
        await using var db = NewContext();
        var backup = await DatabaseUpgrader.UpgradeAsync(db, root, NullLogger.Instance);

        Assert.Null(backup);
        Assert.False(Directory.Exists(BackupDir) && Directory.EnumerateFiles(BackupDir).Any());
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
    }

    [Fact]
    public async Task Up_to_date_database_takes_no_backup()
    {
        await using var db = NewContext();
        await DatabaseUpgrader.UpgradeAsync(db, root, NullLogger.Instance);

        var backup = await DatabaseUpgrader.UpgradeAsync(db, root, NullLogger.Instance);

        Assert.Null(backup);
        Assert.False(Directory.Exists(BackupDir) && Directory.EnumerateFiles(BackupDir).Any());
    }

    [Fact]
    public async Task Pending_migration_on_existing_database_takes_consistent_backup_first()
    {
        await using var db = NewContext();
        await MigrateToPenultimateAsync(db);
        db.Settings.Add(new PanelSetting { Key = "probe", Value = "korunmali" });
        await db.SaveChangesAsync();
        var penultimate = AllMigrations(db)[^2];

        var backup = await DatabaseUpgrader.UpgradeAsync(db, root, NullLogger.Instance);

        Assert.NotNull(backup);
        Assert.True(File.Exists(backup));
        Assert.StartsWith(BackupDir, backup);
        Assert.Contains(penultimate, Path.GetFileName(backup));
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());

        // Yedek, yükseltme ÖNCESİ şemayı (son migration'ın dizini YOK) ve veriyi taşır.
        await using var copy = new SqliteConnection($"Data Source={backup};Pooling=False;Mode=ReadOnly");
        await copy.OpenAsync();
        Assert.Equal(1L, await ScalarLongAsync(copy, "SELECT count(*) FROM panel_settings WHERE Key='probe' AND Value='korunmali'"));
        Assert.Equal(0L, await ScalarLongAsync(copy, "SELECT count(*) FROM sqlite_master WHERE name='ix_audit_logs_created_at'"));
    }

    [Fact]
    public async Task Failed_migration_throws_keeps_backup_and_does_not_fall_back_to_ensure_created()
    {
        await using var db = NewContext();
        await MigrateToPenultimateAsync(db);
        var penultimate = AllMigrations(db)[^2];
        // Son migration'ın oluşturacağı dizini elle önceden yarat → migration "already exists" ile patlar.
        await db.Database.ExecuteSqlRawAsync(
            "CREATE INDEX \"ix_audit_logs_created_at\" ON \"audit_logs\" (\"CreatedAtUtc\");");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => DatabaseUpgrader.UpgradeAsync(db, root, NullLogger.Instance));

        Assert.Contains("migration", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(Directory.EnumerateFiles(BackupDir, "adapanel-pre-*.db"));
        // Geçmiş değişmedi: başarısız migration "uygulanmış" görünmüyor, şema örtük olarak onarılmadı.
        var applied = (await db.Database.GetAppliedMigrationsAsync()).ToList();
        Assert.Equal(penultimate, applied[^1]);
        Assert.Single(await db.Database.GetPendingMigrationsAsync());
    }

    [Fact]
    public async Task Backups_are_pruned_to_the_newest_N()
    {
        await using var db = NewContext();
        await DatabaseUpgrader.UpgradeAsync(db, root, NullLogger.Instance);
        for (var i = 0; i < 4; i++)
        {
            await DatabaseUpgrader.BackupAsync(db, root, "m" + i, keep: 2);
            await Task.Delay(30);
        }

        var files = Directory.EnumerateFiles(BackupDir, "adapanel-pre-*.db").Select(f => Path.GetFileName(f)).ToArray();
        Assert.Equal(2, files.Length);
        Assert.Contains(files, f => f.Contains("-m3-"));
        Assert.Contains(files, f => f.Contains("-m2-"));
    }

    [Fact]
    public async Task Wal_mode_is_enabled_and_persists_for_new_connections()
    {
        await using var db = NewContext();
        await DatabaseUpgrader.UpgradeAsync(db, root, NullLogger.Instance);
        await DatabaseUpgrader.EnableWalAsync(db);

        await using var other = new SqliteConnection($"Data Source={DbPath};Pooling=False");
        await other.OpenAsync();
        await using var cmd = other.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode;";
        Assert.Equal("wal", (string?)await cmd.ExecuteScalarAsync());
    }

    [Theory]
    [InlineData(null, 30000L)]
    [InlineData(17, 17000L)]
    public async Task Busy_timeout_is_pinned_and_explicit_value_is_respected(int? explicitSeconds, long expectedMs)
    {
        var cs = $"Data Source={DbPath}" + (explicitSeconds is { } s ? $";Default Timeout={s}" : string.Empty);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:AdaPanel"] = cs })
            .Build();
        var services = new ServiceCollection();
        services.AddAdaPanelData(config);
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var conn = scope.ServiceProvider.GetRequiredService<AdaPanelDbContext>().Database.GetDbConnection();
        await conn.OpenAsync();
        Assert.Equal(expectedMs, await ScalarLongAsync(conn, "PRAGMA busy_timeout;"));
    }

    [Fact]
    public async Task Created_at_index_exists_in_model_and_in_migrated_schema()
    {
        await using var db = NewContext();
        var entity = db.Model.FindEntityType(typeof(AuditLog))!;
        Assert.Contains(entity.GetIndexes(), i => i.Properties.Single().Name == nameof(AuditLog.CreatedAtUtc));

        await DatabaseUpgrader.UpgradeAsync(db, root, NullLogger.Instance);
        await db.Database.OpenConnectionAsync();
        Assert.Equal(1L, await ScalarLongAsync(
            db.Database.GetDbConnection(),
            "SELECT count(*) FROM sqlite_master WHERE type='index' AND name='ix_audit_logs_created_at'"));
    }

    private sealed class DeleteCounter : DbCommandInterceptor
    {
        public int Deletes;

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.TrimStart().StartsWith("DELETE", StringComparison.OrdinalIgnoreCase)) Deletes++;
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private AuditService NewAuditService(AdaPanelDbContext db) => new(
        db,
        new AuditLogReadTests.SahteKapsam(),
        new JsonFileStore(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = root })));

    [Fact]
    public async Task Purge_deletes_in_batches_and_keeps_recent_rows()
    {
        var counter = new DeleteCounter();
        await using var db = NewContext(counter);
        await DatabaseUpgrader.UpgradeAsync(db, root, NullLogger.Instance);

        var old = DateTime.UtcNow.AddDays(-400);
        var total = AuditService.PurgeBatchSize * 2 + 37;
        for (var i = 0; i < total; i++)
        {
            db.AuditLogs.Add(new AuditLog { Action = "a", Resource = "r", Details = "d", CreatedAtUtc = old.AddSeconds(i) });
        }
        for (var i = 0; i < 3; i++)
        {
            db.AuditLogs.Add(new AuditLog { Action = "new", Resource = "r", Details = "d", CreatedAtUtc = DateTime.UtcNow });
        }
        await db.SaveChangesAsync();

        var service = NewAuditService(db);
        await service.SetRetentionAsync(30);
        var deleted = await service.PurgeAsync();

        Assert.Equal(total, deleted);
        Assert.Equal(3, await db.AuditLogs.CountAsync());
        Assert.Equal(3, counter.Deletes); // 5000 + 5000 + 37
    }

    [Fact]
    public async Task Audit_write_failure_does_not_throw_but_is_counted()
    {
        await using var db = NewContext();
        await DatabaseUpgrader.UpgradeAsync(db, root, NullLogger.Instance);
        await db.Database.ExecuteSqlRawAsync("DROP TABLE audit_logs;");
        AuditFailureMonitor.ResetForTests();

        await NewAuditService(db).WriteAsync("x.y", "res", "details", null, null);

        Assert.Equal(1, AuditFailureMonitor.FailureCount);
        Assert.NotNull(AuditFailureMonitor.LastFailureUtc);
        Assert.Empty(db.ChangeTracker.Entries<AuditLog>()); // bozuk giriş izlemeden çıktı
    }

    [Fact]
    public async Task Audit_write_failure_propagates_when_other_changes_are_pending_or_when_required()
    {
        await using var db = NewContext();
        await DatabaseUpgrader.UpgradeAsync(db, root, NullLogger.Instance);
        await db.Database.ExecuteSqlRawAsync("DROP TABLE audit_logs;");
        AuditFailureMonitor.ResetForTests();

        // Aynı SaveChanges başka bekleyen değişikliği de taşır → hata yutulursa kayıp "başarı" görünür.
        db.Settings.Add(new PanelSetting { Key = "pending", Value = "v" });
        await Assert.ThrowsAnyAsync<Exception>(() => NewAuditService(db).WriteAsync("x", "r", "d", null, null));

        // Güvenliğe kritik yol: her durumda atar.
        await using var db2 = NewContext();
        await Assert.ThrowsAnyAsync<Exception>(() =>
            AuditWriter.WriteAsync(db2, "x", "r", "d", null, null, null, propagateFailure: true));
        Assert.Equal(0, AuditFailureMonitor.FailureCount);
    }
}
