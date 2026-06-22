using AdaPanel.Core.Dtos.Sites;

namespace AdaPanel.Core.Interfaces;

/// <summary>
/// Site uygulama kökleri için Windows FSRM tabanlı disk kotası yönetimi.
/// </summary>
public interface IDiskQuotaService
{
    Task<SiteDiskQuotaDto> GetQuotaAsync(string siteName, CancellationToken cancellationToken = default);

    Task<SiteDiskQuotaDto> SetQuotaAsync(string siteName, SetSiteDiskQuotaRequest request, CancellationToken cancellationToken = default);

    Task<SiteDiskQuotaDto> RemoveQuotaAsync(string siteName, CancellationToken cancellationToken = default);
}
