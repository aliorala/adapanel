using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace AdaPanel.Data.Persistence;

/// <summary>
/// Panel veritabanını açılışta güvenle yükseltir: WAL, bekleyen migration varsa ÖNCEDEN tutarlı yedek,
/// sonra migration. Başarısızlıkta şemayı "EnsureCreated ile garanti etmeye" ÇALIŞMAZ: mevcut bir
/// veritabanında bu, başarısız migration'ı maskeler ve şemayı sessizce ayrıştırırdı. Hata yukarı atılır;
/// yedek diskte kalır.
/// </summary>
public static class DatabaseUpgrader
{
    public const int DefaultBackupsToKeep = 5;
    private const string BackupPrefix = "adapanel-pre-";

    /// <summary>Tek seferlik: günlük kipini WAL yapar (kalıcıdır; dosyada saklanır).</summary>
    public static async Task EnableWalAsync(AdaPanelDbContext dbContext, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(dbContext.Database.GetConnectionString());
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL;";
        var mode = Convert.ToString(await command.ExecuteScalarAsync(cancellationToken));
        if (!string.Equals(mode, "wal", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"SQLite WAL kipine geçilemedi (kip: {mode}).");
        }
    }

    /// <summary>
    /// Bekleyen migration varsa (ve veritabanında zaten tablo varsa) yedek alır, sonra uygular.
    /// Dönen değer: alınan yedeğin yolu (yedek gerekmediyse null).
    /// </summary>
    public static async Task<string?> UpgradeAsync(
        AdaPanelDbContext dbContext,
        string dataRoot,
        ILogger logger,
        int backupsToKeep = DefaultBackupsToKeep,
        CancellationToken cancellationToken = default)
    {
        var database = dbContext.Database;
        var creator = dbContext.GetService<IRelationalDatabaseCreator>();
        var pending = (await database.GetPendingMigrationsAsync(cancellationToken)).ToList();
        var applied = (await database.GetAppliedMigrationsAsync(cancellationToken)).ToList();
        var schemaExists = await creator.ExistsAsync(cancellationToken) && await creator.HasTablesAsync(cancellationToken);

        string? backupPath = null;
        if (pending.Count > 0 && schemaExists)
        {
            var from = applied.Count > 0 ? applied[^1] : "baseline";
            backupPath = await BackupAsync(dbContext, dataRoot, from, backupsToKeep, cancellationToken);
            logger.LogInformation("Migration öncesi veritabanı yedeği alındı: {Path}", backupPath);
        }

        if (applied.Count == 0 && schemaExists && pending.Count > 0)
        {
            // Eski EnsureCreated kurulumu: şema InitialCreate ile birebir; onu "uygulanmış" işaretle.
            var initialMigration = pending[0];
            await database.ExecuteSqlRawAsync(
                "CREATE TABLE IF NOT EXISTS \"__EFMigrationsHistory\" (" +
                "\"MigrationId\" TEXT NOT NULL CONSTRAINT \"PK___EFMigrationsHistory\" PRIMARY KEY, " +
                "\"ProductVersion\" TEXT NOT NULL);",
                cancellationToken);
            await database.ExecuteSqlRawAsync(
                "INSERT OR IGNORE INTO \"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") VALUES ({0}, {1});",
                [initialMigration, "8.0.27"],
                cancellationToken);
            logger.LogInformation("Mevcut şema InitialCreate olarak baseline'landı (EnsureCreated → migration geçişi).");
        }

        try
        {
            await database.MigrateAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogCritical(
                exception,
                "Migration BAŞARISIZ. Veritabanına EnsureCreated UYGULANMAYACAK. Yedek: {Backup}",
                backupPath ?? "(yok: veritabanı yeni/boştu)");
            throw new InvalidOperationException(
                "Veritabanı migration'ı başarısız oldu; panel güvenli olmayan bir şemayla başlatılmadı." +
                (backupPath is null ? string.Empty : $" Yükseltme öncesi yedek: {backupPath}"),
                exception);
        }

        return backupPath;
    }

    /// <summary>VACUUM INTO: WAL dahil tutarlı anlık görüntü; ayrı bağlantıyla alınır.</summary>
    public static async Task<string> BackupAsync(
        AdaPanelDbContext dbContext,
        string dataRoot,
        string fromMigration,
        int keep,
        CancellationToken cancellationToken = default)
    {
        var directory = Path.Combine(dataRoot, "backups", "db");
        Directory.CreateDirectory(directory);

        var safeFrom = new string(fromMigration.Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_').ToArray());
        var target = Path.Combine(directory, $"{BackupPrefix}{safeFrom}-{DateTime.UtcNow:yyyyMMddTHHmmssfff}Z.db");

        await using (var connection = new SqliteConnection(dbContext.Database.GetConnectionString()))
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = $"VACUUM INTO '{target.Replace("'", "''")}';";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        if (!File.Exists(target) || new FileInfo(target).Length == 0)
        {
            throw new IOException($"Veritabanı yedeği oluşturulamadı: {target}");
        }

        Prune(directory, Math.Max(1, keep));
        return target;
    }

    private static void Prune(string directory, int keep)
    {
        var old = new DirectoryInfo(directory)
            .EnumerateFiles(BackupPrefix + "*.db")
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ThenByDescending(file => file.Name, StringComparer.Ordinal)
            .Skip(keep);
        foreach (var file in old)
        {
            try { file.Delete(); }
            catch (IOException) { /* temizlik en iyi çaba; yedek silinemedi diye açılış düşmez */ }
            catch (UnauthorizedAccessException) { }
        }
    }
}
