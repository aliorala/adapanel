using AdaPanel.Core.Dtos.Components;

namespace AdaPanel.Core.Interfaces;

public interface IComponentBootstrapper
{
    Task<SoftwareCatalogDto> GetCatalogAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Kurulu bileşenlerin upstream'deki son sürümlerini canlı çözer, sonucu önbelleğe
    /// alır ve güncelleme bilgisini içeren güncel katalogu döndürür.
    /// </summary>
    Task<SoftwareCatalogDto> CheckForUpdatesAsync(CancellationToken cancellationToken = default);

    Task<ComponentInstallResultDto> InstallAsync(
        string componentId,
        Guid? actorUserId,
        string? ipAddress,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyCollection<ComponentInstallResultDto>> InstallDefaultsAsync(
        Guid? actorUserId,
        string? ipAddress,
        CancellationToken cancellationToken = default);

    Task RemoveAsync(
        string componentId,
        Guid? actorUserId,
        string? ipAddress,
        CancellationToken cancellationToken = default);
}
