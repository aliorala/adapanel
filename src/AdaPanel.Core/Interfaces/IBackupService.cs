using AdaPanel.Core.Dtos.Backup;

namespace AdaPanel.Core.Interfaces;

public interface IBackupService
{
    Task<IReadOnlyCollection<BackupRecordDto>> GetBackupsAsync(CancellationToken cancellationToken = default);

    Task<BackupRecordDto> BackupSiteAsync(BackupSiteRequest request, CancellationToken cancellationToken = default);

    Task<BackupRecordDto> BackupDatabaseAsync(BackupDatabaseRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Mail sunucusu (Stalwart) verisini yedekler: servis kısa süre durdurulur,
    /// data dizini zip'lenir ve servis yeniden başlatılır.
    /// </summary>
    Task<BackupRecordDto> BackupMailAsync(BackupMailRequest request, CancellationToken cancellationToken = default);

    Task<BackupRecordDto> RestoreAsync(RestoreBackupRequest request, CancellationToken cancellationToken = default);

    /// <summary>Zip yedeği içeriğini dizin bazlı listeler (granüler geri yükleme için gezinme).</summary>
    Task<BackupEntriesDto> GetEntriesAsync(string backupId, string? prefix, CancellationToken cancellationToken = default);

    /// <summary>Seçilen dosya/dizinleri orijinal site köküne veya verilen hedefe geri yükler.</summary>
    Task<RestoreBackupItemsResultDto> RestoreItemsAsync(RestoreBackupItemsRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// <paramref name="retentionDays"/> gününden eski yerel yedek dosyalarını ve kayıtlarını siler.
    /// 0 veya negatif değer hiçbir şeyi silmez. Silinen kayıt sayısını döner.
    /// </summary>
    Task<int> PruneAsync(int retentionDays, CancellationToken cancellationToken = default);
}
