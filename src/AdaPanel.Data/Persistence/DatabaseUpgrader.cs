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
        string? reuseBackup = null,
        CancellationToken cancellationToken = default)
    {
        var database = dbContext.Database;
        var creator = dbContext.GetService<IRelationalDatabaseCreator>();
        var pending = (await database.GetPendingMigrationsAsync(cancellationToken)).ToList();
        var applied = (await database.GetAppliedMigrationsAsync(cancellationToken)).ToList();
        var schemaExists = await creator.ExistsAsync(cancellationToken) && await creator.HasTablesAsync(cancellationToken);

        string? backupPath = null;
        if (pending.Count > 0 && schemaExists && reuseBackup is not null && File.Exists(reuseBackup))
        {
            // Yeniden deneme: ilk denemenin yedeği (yükseltme öncesi durum) zaten var; ikincisi YARIM
            // kalmış şemanın kopyası olurdu ve gerçek yedeği prune ile itebilirdi.
            backupPath = reuseBackup;
        }
        else if (pending.Count > 0 && schemaExists)
        {
            var from = applied.Count > 0 ? applied[^1] : "baseline";
            backupPath = await BackupAsync(dbContext, dataRoot, from, cancellationToken);
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
            throw new DatabaseUpgradeException(
                backupPath,
                "Veritabanı migration'ı başarısız oldu; panel güvenli olmayan bir şemayla başlatılmadı." +
                (backupPath is null ? string.Empty : $" Yükseltme öncesi yedek: {backupPath}"),
                exception);
        }

        // Yedekler ANCAK başarılı yükseltmeden sonra budanır: başarısız denemeler gerçek yedeği itmesin.
        if (backupPath is not null)
        {
            Prune(Path.Combine(dataRoot, "backups", "db"), Math.Max(1, backupsToKeep));
        }

        return backupPath;
    }

    /// <summary>Tutarlı anlık görüntü (WAL checkpoint + kopya ya da VACUUM INTO); ayrı bağlantıyla alınır.</summary>
    public static async Task<string> BackupAsync(
        AdaPanelDbContext dbContext,
        string dataRoot,
        string fromMigration,
        CancellationToken cancellationToken = default)
    {
        var directory = Path.Combine(dataRoot, "backups", "db");
        Directory.CreateDirectory(directory);

        var safeFrom = new string(fromMigration.Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_').ToArray());
        var target = Path.Combine(directory, $"{BackupPrefix}{safeFrom}-{DateTime.UtcNow:yyyyMMddTHHmmssfff}Z.db");

        await using (var connection = new SqliteConnection(dbContext.Database.GetConnectionString()))
        {
            await connection.OpenAsync(cancellationToken);

            // HIZLI YOL: açılışta (app.Run öncesi) başka yazar yok. WAL TRUNCATE ile tamamen ana dosyaya
            // işlenirse ve meşgul kayıt yoksa, dosya kopyası tutarlıdır ve VACUUM INTO'dan kat kat hızlıdır
            // (ölçüm: ~630 MB'ta VACUUM INTO ≈ 7 sn). Windows servis başlatma zaman aşımı (1053, ~30 sn)
            // bu süreye dayanır; bu yüzden hızlı yol tercih edilir. Olmazsa VACUUM INTO'ya düşülür.
            var copied = false;
            var source = connection.DataSource;
            if (!string.IsNullOrEmpty(source) && File.Exists(source))
            {
                await using var checkpoint = connection.CreateCommand();
                checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                await using var reader = await checkpoint.ExecuteReaderAsync(cancellationToken);
                var busy = await reader.ReadAsync(cancellationToken) ? reader.GetInt32(0) : 1;
                await reader.CloseAsync();
                var walPath = source + "-wal";
                var walEmpty = !File.Exists(walPath) || new FileInfo(walPath).Length == 0;
                if (busy == 0 && walEmpty)
                {
                    File.Copy(source, target, overwrite: false);
                    copied = new FileInfo(target).Length == new FileInfo(source).Length;
                    if (!copied)
                    {
                        File.Delete(target);
                    }
                }
            }

            if (!copied)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = $"VACUUM INTO '{target.Replace("'", "''")}';";
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        if (!File.Exists(target) || new FileInfo(target).Length == 0)
        {
            throw new IOException($"Veritabanı yedeği oluşturulamadı: {target}");
        }

        return target;
    }

    public static void Prune(string directory, int keep)
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

/// <summary>Migration başarısız oldu; <see cref="BackupPath"/> yükseltme öncesi yedeğin yolu (yoksa null).</summary>
public sealed class DatabaseUpgradeException(string? backupPath, string message, Exception inner)
    : InvalidOperationException(message, inner)
{
    public string? BackupPath { get; } = backupPath;
}
