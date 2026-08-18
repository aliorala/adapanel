using AdaPanel.Core.Dtos.Databases;

namespace AdaPanel.Core.Interfaces;

/// <summary>
/// profesyonel panel benzeri uzak veritabanı erişimi: MariaDB (3306) için Windows Güvenlik Duvarı kuralını
/// yalnız belirli bir IP'ye açıp kapatır. MariaDB tüm arayüzlerde dinler; gerçek kapı firewall'dur.
/// </summary>
public interface IRemoteDbAccessService
{
    Task<RemoteDbAccessDto> GetAsync(CancellationToken cancellationToken = default);

    Task<RemoteDbAccessDto> SetAsync(UpdateRemoteDbAccessRequest request, CancellationToken cancellationToken = default);

    /// <summary>Kurulu motorların KULLANICI bazlı uzak erişim tablosu (motordan okunan gerçekle).</summary>
    Task<DatabaseRemoteAccessOverviewDto> GetUserAccessAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Tek bir veritabanı kullanıcısının uzak erişimini değiştirir.
    /// </summary>
    /// <param name="actorSubscriptionId">İsteği yapanın abonelik kimliği (kapsam denetimi için).</param>
    /// <param name="actorIsAdministrator">
    /// Yönetici kapsam denetiminden muaftır. Kimlik denetleyiciden GEÇİRİLİR, servis içinde
    /// tahmin edilmez: kapsamı çağıran katmana bırakmak, yeni bir uç eklendiğinde denetimin
    /// sessizce atlanmasına yol açar.
    /// </param>
    Task<DatabaseRemoteAccessEngineDto> SetUserAccessAsync(
        UpdateDatabaseUserRemoteAccessRequest request,
        Guid actorSubscriptionId,
        bool actorIsAdministrator,
        CancellationToken cancellationToken = default);
}
