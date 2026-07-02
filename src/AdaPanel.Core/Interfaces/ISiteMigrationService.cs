using AdaPanel.Core.Dtos.Migration;

namespace AdaPanel.Core.Interfaces;

public interface ISiteMigrationService
{
    /// <summary>
    /// Tek akışta site göçü: (opsiyonel) siteyi oluştur + dosyaları FTP/SFTP ile çek + veritabanını aktar.
    /// Her adım bağımsızdır (biri başarısız olsa diğerleri yürür); sonuç adım-adım döner.
    /// <paramref name="onProgress"/> verilirse her aşama (plan/başla/sonuç/bitti) üretildiği anda raporlanır —
    /// canlı (SSE) ilerleme göstermek için. Geri çağrı sıralı/await edilerek çağrılır (akış sırası korunur).
    /// </summary>
    Task<MigrateSiteResultDto> MigrateSiteAsync(
        MigrateSiteRequest request,
        Guid? actorUserId,
        string? ipAddress,
        Func<MigrationProgressEvent, CancellationToken, Task>? onProgress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Plesk REST API'sine bağlanıp domain'leri keşfeder (göç sihirbazını doldurmak için). Best-effort —
    /// bağlantı/şekil hatasında net hata fırlatır; manuel form etkilenmez.
    /// </summary>
    Task<PleskDiscoverResultDto> DiscoverPleskAsync(PleskDiscoverRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// cPanel/WHM API'sine bağlanıp hesap/domain'leri keşfeder (WHM listaccts). Best-effort — bağlantı/şekil
    /// hatasında net hata fırlatır; manuel form etkilenmez. Plesk keşfiyle aynı domain-listesi sonucunu döner.
    /// </summary>
    Task<PleskDiscoverResultDto> DiscoverCpanelAsync(CpanelDiscoverRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Göç ÖNCESİ ön-kontrol: site adı uygunluğu + dosya/DB kaynağı TCP erişilebilirliği. Göç başlatmadan
    /// "bağlanır mı" sorusunu yanıtlar (kimlik doğrulamaz; ucuz erişim kontrolü). Adım-adım sonuç döner.
    /// </summary>
    Task<MigrationValidationResultDto> ValidateMigrationAsync(MigrateSiteRequest request, CancellationToken cancellationToken = default);
}
