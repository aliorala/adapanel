using AdaPanel.Core.Dtos.Databases;

namespace AdaPanel.Core.Interfaces;

/// <summary>
/// Site ↔ veritabanı bağı (Plesk abonelik mantığı). Veritabanı oluşturulurken/içe aktarılırken bir siteye
/// bağlanır; site silinince bağlı tüm veritabanları cascade ile temizlenir (isim tahminine bağlı kalmaz).
/// </summary>
public interface ISiteDatabaseLinkService
{
    Task LinkAsync(string engine, string databaseName, string siteName, CancellationToken cancellationToken = default);

    Task UnlinkAsync(string engine, string databaseName, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SiteDatabaseLinkDto>> GetForSiteAsync(string siteName, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SiteDatabaseLinkDto>> GetAllAsync(CancellationToken cancellationToken = default);

    Task RemoveSiteAsync(string siteName, CancellationToken cancellationToken = default);
}
