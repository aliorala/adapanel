using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Backup;

namespace AdaPanel.Infrastructure.Backup;

public sealed partial class BackupService
{
    // Ayni anda YALNIZ BIR test geri yukleme (disk ve G/C korumasi); mesgulse 409.
    private static readonly SemaphoreSlim TestRestoreGate = new(1, 1);

    /// <summary>
    /// Yalitilmis TEST geri yukleme: arsivi veri kokunun altindaki gecici <c>restore-test</c> klasorune
    /// acip dogrular ve klasoru siler. Canli site, IIS, veritabani ve posta'ya DOKUNMAZ (hicbir
    /// geri yukleme kodu cagrilmaz). Yalniz yerel kopya sinanir; sifreli arsiv parola ister.
    /// </summary>
    public async Task<BackupTestRestoreResultDto> TestRestoreAsync(string backupId, string? encryptionPassword,
        CancellationToken cancellationToken = default)
    {
        var record = (await GetBackupsAsync(cancellationToken))
            .FirstOrDefault(item => string.Equals(item.Id, backupId, StringComparison.Ordinal))
            ?? throw new AppException("backup.not_found", 404, 404);
        if (string.IsNullOrWhiteSpace(record.Path) || IsLocalFileMissing(record))
        {
            // Uzak kopyayi indirip sinamak ayri bir is; olculmeyen sey "gecti" denmez.
            return new BackupTestRestoreResultDto
            {
                BackupId = record.Id, Name = record.Name, Outcome = "not_tested",
                NotTestedReason = string.IsNullOrWhiteSpace(record.Path) ? "backup.test_restore.remote_only" : "backup.file_missing",
                CleanedUp = true
            };
        }

        if (!await TestRestoreGate.WaitAsync(0, cancellationToken))
            throw new AppException("backup.test_restore_busy", 409, 409);
        try
        {
            using var content = await OpenContentAsync(record, encryptionPassword, cancellationToken);
            var workRoot = fileStore.EnsureDirectory("restore-test");
            BackupTestRestore.CleanupStale(workRoot);
            return await BackupTestRestore.RunAsync(record, content.FilePath, workRoot,
                BackupTestRestore.DefaultExtractCapBytes, cancellationToken);
        }
        finally { TestRestoreGate.Release(); }
    }
}
