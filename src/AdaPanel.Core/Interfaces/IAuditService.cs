using AdaPanel.Core.Dtos.Audit;
using AdaPanel.Core.Enums;

namespace AdaPanel.Core.Interfaces;

public interface IAuditService
{
    Task WriteAsync(
        string action,
        string resource,
        string details,
        Guid? userId,
        string? ipAddress,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyCollection<AuditLogDto>> GetRecentAsync(int take, CancellationToken cancellationToken = default);

    /// <summary>
    /// Filtreli + sayfalı arama. <paramref name="visibility"/> ZORUNLUDUR: kapsam denetimi bu
    /// katmanda uygulanır, çağıranın filtre koymayı hatırlamasına bırakılmaz.
    /// </summary>
    Task<AuditLogPageDto> SearchAsync(
        AuditLogQuery query,
        AuditVisibility visibility,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Aynı filtreye uyan kayıtları AKITARAK verir (CSV dışa aktarma).
    /// </summary>
    /// <remarks>
    /// Liste değil akış döner: yüz binlerce kaydı önce belleğe toplamak, dışa aktarmayı tam da
    /// gerektiği anda (büyük veri kümesinde) çökertirdi.
    /// </remarks>
    IAsyncEnumerable<AuditLogDto> StreamAsync(
        AuditLogQuery query,
        AuditVisibility visibility,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Çağıranın görebileceği kayıt kapsamını çözer: yönetici/operatör sunucu genelini; müşteri
    /// kendi aboneliğini (kendisi + alt kullanıcıları); bayi kendi müşterilerinin kümesini görür.
    /// </summary>
    Task<AuditVisibility> ResolveVisibilityAsync(
        Guid userId,
        UserRole role,
        CancellationToken cancellationToken = default);

    /// <summary>Saklama politikası + biriken kaydın ölçümü.</summary>
    Task<AuditRetentionSettingsDto> GetRetentionAsync(CancellationToken cancellationToken = default);

    /// <summary>Saklama süresini kaydeder (0 = sınırsız). Kaydetmek tek başına silmez.</summary>
    Task<AuditRetentionSettingsDto> SetRetentionAsync(int retentionDays, CancellationToken cancellationToken = default);

    /// <summary>
    /// Saklama süresinden eski kayıtları siler ve silinen sayısını döner. Süre 0 ise hiçbir şey yapmaz.
    /// </summary>
    Task<int> PurgeAsync(CancellationToken cancellationToken = default);
}
