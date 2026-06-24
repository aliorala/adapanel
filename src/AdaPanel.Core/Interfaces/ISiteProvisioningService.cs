using AdaPanel.Core.Dtos.Sites;

namespace AdaPanel.Core.Interfaces;

public interface ISiteProvisioningService
{
    Task<SiteDto> CreateAndProvisionAsync(
        CreateSiteRequest request,
        Guid? actorUserId,
        string? ipAddress,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sitenin belge kökünü (IIS fiziksel yolu) değiştirir — Laravel/Symfony için <c>...\public</c>. IIS'te
    /// kalıcıdır; yeni kökte index.php varsayılan belge yapılır (IIS varsayılanı index.php içermez → "/" = 403).
    /// </summary>
    Task<SiteDto> SetDocumentRootAsync(string siteName, string physicalPath, CancellationToken cancellationToken = default);
}
