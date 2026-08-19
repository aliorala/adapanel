using AdaPanel.Core.Dtos.Backup;

namespace AdaPanel.Core.Interfaces;

public interface IBackupService
{
    Task<IReadOnlyCollection<BackupRecordDto>> GetBackupsAsync(CancellationToken cancellationToken = default);

    Task<BackupRecordDto> BackupSiteAsync(BackupSiteRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// ABONELİK bazında yedek: bir müşterinin bütün siteleri + veritabanları + posta alan adları +
    /// FTP hesapları TEK arşivde (Plesk'in abonelik yedeği paritesi).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Arşive giren her kaynak, aboneliğin KAPSAMINDAN ölçülerek seçilir; ad benzerliğine
    /// güvenilmez. Böylece bir müşterinin arşivinde başka bir müşterinin verisi bulunamaz.
    /// </para>
    /// <para>
    /// Posta bölümü posta kutusu ENVANTERİDİR (hesaplar/alias/kota), iletiler değil; iletiler
    /// sunucu geneli posta yedeğindedir (<see cref="BackupMailAsync"/>). FTP ve posta PAROLALARI
    /// dışa aktarılamaz ve geri yüklemede sıfırlanmalıdır — ikisi de manifestte ve geri yükleme
    /// uyarılarında açıkça söylenir.
    /// </para>
    /// </remarks>
    Task<BackupRecordDto> BackupSubscriptionAsync(BackupSubscriptionRequest request, CancellationToken cancellationToken = default);

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

    /// <summary>
    /// Kayıtlı yedeklerin GERÇEKTEN var olup olmadığını ölçer (uzlaştırma) ve hedefte duran ama
    /// kaydı olmayan dosyaları (yetim) raporlar. Hiçbir şeyi SİLMEZ.
    /// </summary>
    /// <remarks>
    /// Listede duran ama artık olmayan yedek, panelin verebileceği en pahalı yalandır; kullanıcı
    /// bunu normalde geri yüklemeye çalışırken, yani felaket anında öğrenirdi. Ulaşılamayan hedef
    /// "yok" SAYILMAZ — geçici bir ağ arızası bütün yedek geçmişini kayıp göstermemelidir.
    /// </remarks>
    Task<BackupVerificationResultDto> VerifyBackupsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Bir aboneliğin YALNIZCA kendi yedeklerini budar: süre (<paramref name="retentionDays"/>) ve
    /// sayı (<paramref name="keepCount"/>) limitleri birlikte uygulanır. Kapsam daraltması ŞARTTIR:
    /// bir müşterinin saklama ayarı başka bir müşterinin yedeklerine dokunamaz.
    /// </summary>
    Task<int> PruneSubscriptionAsync(
        Guid userId,
        int retentionDays,
        int keepCount,
        BackupPruneRemoteOptions? remote = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Bir yedeğin geri yüklenebilmesi için gereken artımsal ZİNCİRİ çözer (tam yedekten hedefe
    /// doğru sıralı halkalar + kopukluk bilgisi). Ekran, kopuk zinciri geri yüklemeyi denemeden
    /// önce uyarabilsin diye vardır.
    /// </summary>
    Task<BackupChainDto> GetChainAsync(string backupId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Tek bir yedeği siler: UZAK kopya + yerel dosya/dizin + kayıt.
    /// <para>
    /// Uzak kopyanın kimlik bilgileri, otomatik temizliğin kullandığı AYNI kaynaktan (otomasyon
    /// ayarları) okunur. Uzak kopya silinemezse <c>backup.remote_delete_failed</c> fırlatılır ve
    /// KAYIT KORUNUR: kaydı silmek, panelin bir daha göremeyeceği bir YETİM dosya bırakırdı.
    /// </para>
    /// <para>
    /// Yedeğe BAĞIMLI artımsal halkalar varsa istek <c>backup.chain_has_dependents</c> ile
    /// REDDEDİLİR: onları sessizce çöpe çevirmek, panelde duran ama geri yüklenemeyen yedekler
    /// bırakmak olurdu. Kullanıcı ne kaybedeceğini gördükten sonra
    /// <see cref="DeleteBackupChainAsync"/> ile devam eder.
    /// </para>
    /// </summary>
    Task DeleteBackupAsync(string backupId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Yedeği ve ona bağımlı BÜTÜN artımsal halkaları siler (Plesk'in zincire yayılan silmesi).
    /// Silinen kayıtların kimlik ve adlarını döner; hiçbiri silinemediyse hata verir.
    /// </summary>
    Task<BackupDeleteResultDto> DeleteBackupChainAsync(string backupId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Paneldeki varsayılan yedek şifreleme ayarını okur. Parolanın KENDİSİ dönmez; yalnızca
    /// açık mı ve kayıtlı parola gerçekten KULLANILABİLİR mi (çözülebiliyor mu) bilgisi döner.
    /// </summary>
    Task<BackupEncryptionSettingsDto> GetEncryptionSettingsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Varsayılan yedek şifreleme parolasını belirler (boş parola = şifrelemeyi kapat ve kaydı sil).
    /// Parola panelde DPAPI/DataProtection ile korunarak saklanır; zamanlanmış yedeklerin
    /// şifrelenebilmesinin tek yolu budur.
    /// <para><b>PAROLA KAYBOLURSA ŞİFRELİ YEDEKLER GERİ GETİRİLEMEZ.</b></para>
    /// </summary>
    Task<BackupEncryptionSettingsDto> SetEncryptionSettingsAsync(SetBackupEncryptionRequest request, CancellationToken cancellationToken = default);
}
