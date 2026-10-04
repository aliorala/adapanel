using AdaPanel.Core.Dtos.Backup;

namespace AdaPanel.Core.Interfaces;

public interface IBackupService
{
    Task<IReadOnlyCollection<BackupRecordDto>> GetBackupsAsync(CancellationToken cancellationToken = default);

    Task<BackupRecordDto> RetryRemoteUploadAsync(string backupId, string expectedRemoteUrl, CancellationToken cancellationToken = default);

    Task<BackupRecordDto> BackupSiteAsync(BackupSiteRequest request, CancellationToken cancellationToken = default);

    async Task<BackupRecordDto> BackupSiteAsync(BackupSiteRequest request,
        Func<CancellationToken, Task> beforeBackup, CancellationToken cancellationToken = default)
    {
        await beforeBackup(cancellationToken);
        return await BackupSiteAsync(request, cancellationToken);
    }

    // Production implementations invoke these callbacks under the source mutation lease.
    // The defaults keep simple, non-queuing implementations compatible while enforcing
    // their authorization callback before calling the original operation.
    async Task<BackupRecordDto> RetryRemoteUploadAsync(string backupId, string expectedRemoteUrl,
        Func<BackupRecordDto, CancellationToken, Task> beforeMutation, CancellationToken cancellationToken = default)
    {
        await AuthorizeRecordAsync(backupId, beforeMutation, cancellationToken);
        return await RetryRemoteUploadAsync(backupId, expectedRemoteUrl, cancellationToken);
    }

    async Task<BackupRecordDto> RestoreAsync(RestoreBackupRequest request,
        Func<BackupRecordDto, CancellationToken, Task> beforeMutation, CancellationToken cancellationToken = default)
    {
        await AuthorizeRecordAsync(request.BackupId, beforeMutation, cancellationToken);
        return await RestoreAsync(request, cancellationToken);
    }

    async Task<RestoreBackupItemsResultDto> RestoreItemsAsync(RestoreBackupItemsRequest request,
        Func<BackupRecordDto, CancellationToken, Task> beforeMutation, CancellationToken cancellationToken = default)
    {
        await AuthorizeRecordAsync(request.BackupId, beforeMutation, cancellationToken);
        return await RestoreItemsAsync(request, cancellationToken);
    }

    async Task DeleteBackupAsync(string backupId, Func<BackupRecordDto, CancellationToken, Task> beforeMutation,
        CancellationToken cancellationToken = default)
    {
        await AuthorizeRecordAsync(backupId, beforeMutation, cancellationToken);
        await DeleteBackupAsync(backupId, cancellationToken);
    }

    async Task<BackupDeleteResultDto> DeleteBackupChainAsync(string backupId, IReadOnlyCollection<string>? expectedBackupIds,
        Func<BackupRecordDto, CancellationToken, Task> beforeMutation, CancellationToken cancellationToken = default)
    {
        foreach (var id in (expectedBackupIds ?? [backupId]).Append(backupId).Distinct(StringComparer.Ordinal))
            await AuthorizeRecordAsync(id, beforeMutation, cancellationToken);
        return await DeleteBackupChainAsync(backupId, expectedBackupIds, cancellationToken);
    }

    private async Task AuthorizeRecordAsync(string backupId,
        Func<BackupRecordDto, CancellationToken, Task> beforeMutation, CancellationToken cancellationToken)
    {
        var matches = (await GetBackupsAsync(cancellationToken)).Where(record => record.Id == backupId).ToArray();
        if (matches.Length == 0) throw new Common.AppException("backup.not_found", 404, 404);
        if (matches.Length != 1) throw new Common.AppException("backup.chain_ambiguous", 409, 409);
        await beforeMutation(matches[0], cancellationToken);
    }

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

    async Task<BackupRecordDto> BackupSubscriptionAsync(BackupSubscriptionRequest request,
        Func<CancellationToken, Task> beforeBackup, CancellationToken cancellationToken = default)
    {
        await beforeBackup(cancellationToken);
        return await BackupSubscriptionAsync(request, cancellationToken);
    }

    Task<BackupRecordDto> BackupDatabaseAsync(BackupDatabaseRequest request, CancellationToken cancellationToken = default);

    async Task<BackupRecordDto> BackupDatabaseAsync(BackupDatabaseRequest request,
        Func<CancellationToken, Task> beforeBackup, CancellationToken cancellationToken = default)
    {
        await beforeBackup(cancellationToken);
        return await BackupDatabaseAsync(request, cancellationToken);
    }

    /// <summary>
    /// Mail sunucusu (Stalwart) verisini yedekler: servis kısa süre durdurulur,
    /// data dizini zip'lenir ve servis yeniden başlatılır.
    /// </summary>
    Task<BackupRecordDto> BackupMailAsync(BackupMailRequest request, CancellationToken cancellationToken = default);

    async Task<BackupRecordDto> BackupMailAsync(BackupMailRequest request,
        Func<CancellationToken, Task> beforeBackup, CancellationToken cancellationToken = default)
    {
        await beforeBackup(cancellationToken);
        return await BackupMailAsync(request, cancellationToken);
    }

    /// <summary>
    /// Panelin KENDİ durumunu yedekler: adapanel.db (kullanıcı/2FA) + config/*.json
    /// (servis planları, müşteri scope'ları, otomasyon ayarları). DataProtection anahtarları HARİÇ.
    /// </summary>
    Task<BackupRecordDto> BackupPanelAsync(BackupPanelRequest request, CancellationToken cancellationToken = default);

    async Task<BackupRecordDto> BackupPanelAsync(BackupPanelRequest request,
        Func<CancellationToken, Task> beforeBackup, CancellationToken cancellationToken = default)
    {
        await beforeBackup(cancellationToken);
        return await BackupPanelAsync(request, cancellationToken);
    }

    Task<BackupRecordDto> RestoreAsync(RestoreBackupRequest request, CancellationToken cancellationToken = default);

    /// <summary>Zip yedeği içeriğini dizin bazlı listeler (granüler geri yükleme için gezinme).</summary>
    Task<BackupEntriesDto> GetEntriesAsync(string backupId, string? prefix, CancellationToken cancellationToken = default);

    /// <summary>Build a private selected-entry ZIP. Ownership is checked after the source lease wait.</summary>
    Task<BackupItemsDownload> DownloadItemsAsync(DownloadBackupItemsRequest request,
        Func<BackupRecordDto, CancellationToken, Task> beforeRead, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    /// <summary>Seçilen dosya/dizinleri orijinal site köküne veya verilen hedefe geri yükler.</summary>
    Task<RestoreBackupItemsResultDto> RestoreItemsAsync(RestoreBackupItemsRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// <paramref name="retentionDays"/> gününden eski yedekleri budar: yerel dosya + kayıt silinir; <paramref name="remote"/>
    /// verilmişse off-site (S3/FTP/WebDAV) UZAK kopya da silinir (uzak rotasyon). 0/negatif gün hiçbir şeyi silmez.
    /// </summary>
    /// <remarks>
    /// <b>Dönüş SAYI DEĞİL SONUÇTUR</b> (<see cref="BackupPruneResultDto"/>): "kaç tanesi silindi"
    /// tek başına çağıranın doğruyu söylemesine yetmez. Uzak kopyası silinemeyen kayıtlar başarı
    /// sayısına GİRMEZ ve eskiden yalnızca günlüğe yazılırdı; gece işi bunu göremediği için
    /// panelde "Tamamlandı, 7 eski yedek silindi" yazarken dosyalar FTP'de duruyordu.
    /// </remarks>
    Task<BackupPruneResultDto> PruneAsync(int retentionDays, BackupPruneRemoteOptions? remote = null, CancellationToken cancellationToken = default);

    async Task<BackupPruneResultDto> PruneAsync(int retentionDays, BackupPruneRemoteOptions? remote,
        Func<CancellationToken, Task> beforeMutation, CancellationToken cancellationToken = default)
    {
        await beforeMutation(cancellationToken);
        return await PruneAsync(retentionDays, remote, cancellationToken);
    }

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
    Task<BackupPruneResultDto> PruneSubscriptionAsync(
        Guid userId,
        int retentionDays,
        int keepCount,
        BackupPruneRemoteOptions? remote = null,
        CancellationToken cancellationToken = default);

    async Task<BackupPruneResultDto> PruneSubscriptionAsync(Guid userId, int retentionDays, int keepCount,
        BackupPruneRemoteOptions? remote, Func<CancellationToken, Task> beforeMutation, CancellationToken cancellationToken = default)
    {
        await beforeMutation(cancellationToken);
        return await PruneSubscriptionAsync(userId, retentionDays, keepCount, remote, cancellationToken);
    }

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

    Task<BackupDeleteResultDto> DeleteBackupChainAsync(string backupId, IReadOnlyCollection<string>? expectedBackupIds,
        CancellationToken cancellationToken = default) => DeleteBackupChainAsync(backupId, cancellationToken);

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
