using AdaPanel.Core.Dtos.Migration;

namespace AdaPanel.Core.Interfaces;

public interface ISiteMigrationService
{
    /// <summary>
    /// Tek akışta site göçü: (opsiyonel) siteyi oluştur + dosyaları FTP/SFTP ile çek + veritabanını aktar.
    /// Her adım bağımsızdır (biri başarısız olsa diğerleri yürür); sonuç adım-adım döner.
    /// </summary>
    Task<MigrateSiteResultDto> MigrateSiteAsync(
        MigrateSiteRequest request,
        Guid? actorUserId,
        string? ipAddress,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Plesk REST API'sine bağlanıp domain'leri keşfeder (göç sihirbazını doldurmak için). Best-effort —
    /// bağlantı/şekil hatasında net hata fırlatır; manuel form etkilenmez.
    /// </summary>
    Task<PleskDiscoverResultDto> DiscoverPleskAsync(PleskDiscoverRequest request, CancellationToken cancellationToken = default);
}
