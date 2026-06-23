using AdaPanel.Core.Dtos.Databases;

namespace AdaPanel.Core.Interfaces;

/// <summary>
/// Plesk benzeri uzak veritabanı erişimi: MariaDB (3306) için Windows Güvenlik Duvarı kuralını
/// yalnız belirli bir IP'ye açıp kapatır. MariaDB tüm arayüzlerde dinler; gerçek kapı firewall'dur.
/// </summary>
public interface IRemoteDbAccessService
{
    Task<RemoteDbAccessDto> GetAsync(CancellationToken cancellationToken = default);

    Task<RemoteDbAccessDto> SetAsync(UpdateRemoteDbAccessRequest request, CancellationToken cancellationToken = default);
}
