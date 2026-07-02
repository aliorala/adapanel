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

    /// <summary>
    /// Panelin KENDİ durumunu yedekler: adapanel.db (kullanıcı/2FA) + config/*.json
    /// (servis planları, müşteri scope'ları, otomasyon ayarları). DataProtection anahtarları HARİÇ.
    /// </summary>
    Task<BackupRecordDto> BackupPanelAsync(BackupPanelRequest request, CancellationToken cancellationToken = default);

    Task<BackupRecordDto> RestoreAsync(RestoreBackupRequest request, CancellationToken cancellationToken = default);

    /// <summary>Zip yedeği içeriğini dizin bazlı listeler (granüler geri yükleme için gezinme).</summary>
    Task<BackupEntriesDto> GetEntriesAsync(string backupId, string? prefix, CancellationToken cancellationToken = default);

    /// <summary>Seçilen dosya/dizinleri orijinal site köküne veya verilen hedefe geri yükler.</summary>
    Task<RestoreBackupItemsResultDto> RestoreItemsAsync(RestoreBackupItemsRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// <paramref name="retentionDays"/> gününden eski yedekleri budar: yerel dosya + kayıt silinir; <paramref name="remote"/>
    /// verilmişse off-site (S3/FTP/WebDAV) UZAK kopya da silinir (uzak rotasyon). 0/negatif gün hiçbir şeyi silmez.
    /// Silinen kayıt sayısını döner.
    /// </summary>
    Task<int> PruneAsync(int retentionDays, BackupPruneRemoteOptions? remote = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Off-site yedek hedefini gerçek yedek almadan doğrular: küçük bir test dosyasını gerçek yükleme yoluyla
    /// gönderir (kimlik + bağlantı + yazma izni). Başarısızlıkta fırlatmaz; sonucu (Success + neden) döner.
    /// </summary>
    Task<BackupRemoteTestResultDto> TestRemoteTargetAsync(BackupRemoteTestRequest request, CancellationToken cancellationToken = default);
}
