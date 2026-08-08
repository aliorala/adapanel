using AdaPanel.Core.Dtos.Sites;

namespace AdaPanel.Core.Interfaces;

/// <summary>
/// Alan adı takma adları: aynı siteyi ikinci (üçüncü…) bir alan adından da yayınlar.
/// </summary>
/// <remarks>
/// Sahiplik denetimi ÇAĞIRANIN sorumluluğundadır (<c>CustomerAccess.EnsureDomainClaimAsync</c>):
/// takma ad eklemek, bağlama eklemekle aynı alan adı iddiasını üretir ve denetlenmezse bir kiracı
/// başkasının alan adını kendi sitesine yöneltebilirdi.
/// </remarks>
public interface ISiteAliasService
{
    Task<SiteAliasWorkspaceDto> GetAliasesAsync(string siteName, CancellationToken cancellationToken = default);

    Task<SiteAliasWorkspaceDto> AddAliasAsync(string siteName, AddSiteAliasRequest request, CancellationToken cancellationToken = default);

    /// <summary>Takma adı kaldırır: bağlamaları ve varsa yönlendirme kuralını birlikte siler.</summary>
    Task<SiteAliasWorkspaceDto> RemoveAliasAsync(string siteName, string hostName, CancellationToken cancellationToken = default);
}
