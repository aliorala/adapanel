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
public sealed class SslController(ISslService sslService, ICustomerScopeService customerScopes) : ControllerBase
{
    [HttpGet("{siteName}")]
    public async Task<ActionResult<ApiResponse<SslWorkspaceDto>>> GetWorkspaceAsync(string siteName, CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        var workspace = await sslService.GetSiteWorkspaceAsync(siteName, cancellationToken);
        return Ok(ApiResponse<SslWorkspaceDto>.Ok(workspace));
    }

    [HttpPost("letsencrypt")]
    public async Task<ActionResult<ApiResponse<SslCertificateDto>>> IssueLetsEncryptAsync([FromBody] IssueLetsEncryptRequest request, CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, request.SiteName, cancellationToken);
        var certificate = await sslService.IssueLetsEncryptAsync(request, cancellationToken);
        return Ok(ApiResponse<SslCertificateDto>.Ok(certificate, "ssl.issued"));
    }

    [HttpPost("import")]
    public async Task<ActionResult<ApiResponse<SslCertificateDto>>> ImportAsync([FromBody] ImportCertificateRequest request, CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, request.SiteName, cancellationToken);
        var certificate = await sslService.ImportCertificateAsync(request, cancellationToken);
        return Ok(ApiResponse<SslCertificateDto>.Ok(certificate, "ssl.imported"));
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
