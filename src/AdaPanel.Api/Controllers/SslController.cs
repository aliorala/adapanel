using AdaPanel.Api.Security;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Ssl;
using AdaPanel.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AdaPanel.Api.Controllers;

// Reseller (Bayi) yalnız kendi müşterilerinin siteleri için SSL yönetebilir (EnsureSiteAsync).
// Tüm sertifikaları yenileme (renew-all) server-level → admin-only.
[ApiController]
[Authorize(Roles = "Administrator,Reseller")]
[Route("api/v1/ssl")]
public sealed class SslController(ISslService sslService, ICustomerScopeService customerScopes, IWebmailService webmailService) : ControllerBase
{
    [HttpGet("{siteName}")]
    public async Task<ActionResult<ApiResponse<SslWorkspaceDto>>> GetWorkspaceAsync(string siteName, CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        var workspace = await sslService.GetSiteWorkspaceAsync(siteName, cancellationToken);
        return Ok(ApiResponse<SslWorkspaceDto>.Ok(workspace));
    }

    [HttpPost("letsencrypt")]
    public async Task<ActionResult<ApiResponse<SslCertificateDto>>> IssueLetsEncryptAsync(
        [FromBody] IssueLetsEncryptRequest request,
        [FromServices] IIisSiteService iisSiteService,
        [FromServices] IMailService mailService,
        [FromServices] ILogger<SslController> logger,
        CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, request.SiteName, cancellationToken);

        // Wildcard (DNS-01) sunucunun DNS bölgesine kayıt yazar → yalnız yönetici.
        if (request.Wildcard && !User.IsInRole("Administrator"))
        {
            throw new AdaPanel.Core.Common.AppException("ssl.wildcard_admin_only", 403, 403);
        }

        // GÜVENLİK (çapraz-kiracı): host adı bağlama ucundakiyle AYNI denetimden geçmek zorunda.
        //
        // Buradaki akış sertifikayı almakla kalmıyor, o host adını siteye BAĞLAMA olarak da ekliyor
        // (WinAcmeSslService.EnsureHttpsBindingAsync). Yani denetimsiz bırakılırsa, bağlama ucuna
        // konan kısıt bu uçtan dolanılır ve mail sahipliği zinciri yeniden açılırdı. Üstelik ACME
        // başarısız olsa bile akış self-signed sertifikaya düşüp bağlamayı YİNE ekliyor — yani
        // "nasılsa doğrulama tutmaz" diye güvenmek yanlış olurdu.
        await CustomerAccess.EnsureDomainClaimAsync(
            customerScopes, iisSiteService, mailService, User, request.HostName, request.SiteName, logger, cancellationToken);

        var certificate = await sslService.IssueLetsEncryptAsync(request, cancellationToken);
        await TrySecureWebmailAsync(certificate.HostName, request.ContactEmail, cancellationToken);
        return Ok(ApiResponse<SslCertificateDto>.Ok(certificate, "ssl.issued"));
    }

    [HttpPost("import")]
    public async Task<ActionResult<ApiResponse<SslCertificateDto>>> ImportAsync(
        [FromBody] ImportCertificateRequest request,
        [FromServices] IIisSiteService iisSiteService,
        [FromServices] IMailService mailService,
        [FromServices] ILogger<SslController> logger,
        CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, request.SiteName, cancellationToken);

        // İçe aktarma da bağlama ekler (EnsureHttpsBindingAsync) — gerekçe yukarıda.
        await CustomerAccess.EnsureDomainClaimAsync(
            customerScopes, iisSiteService, mailService, User, request.HostName, request.SiteName, logger, cancellationToken);

        var certificate = await sslService.ImportCertificateAsync(request, cancellationToken);
        await TrySecureWebmailAsync(certificate.HostName, string.Empty, cancellationToken);
        return Ok(ApiResponse<SslCertificateDto>.Ok(certificate, "ssl.imported"));
    }

    // Best-effort: site SSL aldıktan sonra webmail.<domain>'i de otomatik HTTPS yap (profesyonel paneller gibi).
    // İsteği ASLA bozmaz — webmail kurulu değilse / DNS yoksa / sertifika alınamazsa sessiz geçer.
    private async Task TrySecureWebmailAsync(string? domain, string? contactEmail, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(domain))
        {
            return;
        }

        try
        {
            await webmailService.SecureWebmailForDomainAsync(domain, contactEmail ?? string.Empty, cancellationToken);
        }
        catch
        {
            // best-effort — SSL akışını bozma
        }
    }

    [HttpPost("renew")]
    [Authorize(Roles = "Administrator")]
    public async Task<ActionResult<ApiResponse<RenewAllResultDto>>> RenewAllAsync(CancellationToken cancellationToken)
    {
        var result = await sslService.RenewAllAsync(cancellationToken);
        return Ok(ApiResponse<RenewAllResultDto>.Ok(result, "ssl.renewed"));
    }

    [HttpPost("https-redirect")]
    public async Task<ActionResult<ApiResponse<SslWorkspaceDto>>> SetHttpsRedirectAsync([FromBody] SetHttpsRedirectRequest request, CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, request.SiteName, cancellationToken);
        var workspace = await sslService.SetHttpsRedirectAsync(request, cancellationToken);
        return Ok(ApiResponse<SslWorkspaceDto>.Ok(workspace, "ssl.https_redirect_saved"));
    }
}
