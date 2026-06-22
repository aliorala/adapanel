using AdaPanel.Core.Dtos.Ssl;

namespace AdaPanel.Core.Interfaces;

public interface ISslService
{
    Task<SslWorkspaceDto> GetSiteWorkspaceAsync(string siteName, CancellationToken cancellationToken = default);

    Task<SslCertificateDto> IssueLetsEncryptAsync(IssueLetsEncryptRequest request, CancellationToken cancellationToken = default);

    Task<SslCertificateDto> ImportCertificateAsync(ImportCertificateRequest request, CancellationToken cancellationToken = default);

    Task<RenewAllResultDto> RenewAllAsync(CancellationToken cancellationToken = default);

    /// <summary>HTTP→HTTPS kalıcı (301) yönlendirmesini açar/kapatır (Plesk "Force HTTPS").</summary>
    Task<SslWorkspaceDto> SetHttpsRedirectAsync(SetHttpsRedirectRequest request, CancellationToken cancellationToken = default);

    /// <summary>Site silinince panelin SSL durumundaki o siteye ait kayıtları temizler (öksüz kalmasın).</summary>
    Task RemoveSiteAsync(string siteName, CancellationToken cancellationToken = default);
}
