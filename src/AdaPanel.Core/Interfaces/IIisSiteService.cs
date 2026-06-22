using AdaPanel.Core.Dtos.Sites;

namespace AdaPanel.Core.Interfaces;

public interface IIisSiteService
{
    Task<IReadOnlyCollection<SiteDto>> GetSitesAsync(CancellationToken cancellationToken = default);

    Task<SiteDto> GetSiteAsync(string siteName, CancellationToken cancellationToken = default);

    Task<SiteDto> CreateSiteAsync(CreateSiteRequest request, CancellationToken cancellationToken = default);

    Task<SiteDto> UpdatePhysicalPathAsync(
        string siteName,
        string physicalPath,
        CancellationToken cancellationToken = default);

    Task DeleteSiteAsync(string siteName, CancellationToken cancellationToken = default);

    Task<SiteDto> StartSiteAsync(string siteName, CancellationToken cancellationToken = default);

    Task<SiteDto> StopSiteAsync(string siteName, CancellationToken cancellationToken = default);

    Task<SiteDto> AddBindingAsync(string siteName, SiteBindingRequest request, CancellationToken cancellationToken = default);

    Task<SiteDto> RemoveBindingAsync(string siteName, RemoveSiteBindingRequest request, CancellationToken cancellationToken = default);

    Task<IReadOnlyCollection<SiteDto>> ImportExistingSitesAsync(CancellationToken cancellationToken = default);
}
