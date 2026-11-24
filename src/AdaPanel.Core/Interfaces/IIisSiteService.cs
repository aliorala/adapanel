using AdaPanel.Core.Dtos.Sites;

namespace AdaPanel.Core.Interfaces;

public interface IIisSiteService
{
    Task<IReadOnlyCollection<SiteDto>> GetSitesAsync(CancellationToken cancellationToken = default);

    // Migration destinations must also avoid the roots/bindings of hidden panel tools and
    // Default Web Site. The ordinary customer-facing list intentionally hides those sites.
    Task<IReadOnlyCollection<SiteDto>> GetMigrationTargetSitesAsync(CancellationToken cancellationToken = default)
        => throw new NotSupportedException("A complete native IIS target inventory is required for migration.");

    Task<SiteDto> GetSiteAsync(string siteName, CancellationToken cancellationToken = default);

    Task<SiteDto> CreateSiteAsync(CreateSiteRequest request, CancellationToken cancellationToken = default);

    Task<SiteDto> UpdatePhysicalPathAsync(
        string siteName,
        string physicalPath,
        CancellationToken cancellationToken = default);

    Task DeleteSiteAsync(string siteName, CancellationToken cancellationToken = default);

    Task<SiteDto> StartSiteAsync(string siteName, CancellationToken cancellationToken = default);

    Task<SiteDto> SetSiteAutoStartAsync(string siteName, bool autoStart, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Persistent IIS startup configuration is required.");

    Task<SiteDto> StopSiteAsync(string siteName, CancellationToken cancellationToken = default);

    Task<SiteDto> AddBindingAsync(string siteName, SiteBindingRequest request, CancellationToken cancellationToken = default);

    Task<SiteDto> RemoveBindingAsync(string siteName, RemoveSiteBindingRequest request, CancellationToken cancellationToken = default);

    Task<IReadOnlyCollection<SiteDto>> ImportExistingSitesAsync(CancellationToken cancellationToken = default);
}
