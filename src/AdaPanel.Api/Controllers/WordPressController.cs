using AdaPanel.Api.Security;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AdaPanel.Api.Controllers;

// Reseller (Bayi) yalnız kendi müşterilerinin sitelerindeki WordPress kurulumlarını görür/yönetir.
[ApiController]
[Authorize(Roles = "Administrator,Reseller")]
[Route("api/v1/wordpress")]
public sealed class WordPressController(
    IWordPressToolkitService wordPressToolkitService,
    ICustomerScopeService customerScopes) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ApiResponse<IReadOnlyCollection<WordPressSiteDto>>>> GetAllAsync(CancellationToken cancellationToken)
    {
        var installs = await wordPressToolkitService.DetectAllAsync(cancellationToken);

        // Bayi/müşteri yalnızca etkin kapsamındaki sitelerin WP kurulumlarını görür.
        var allowed = await CustomerAccess.GetEffectiveSitesAsync(customerScopes, User, cancellationToken);
        if (allowed is not null)
        {
            installs = installs
                .Where(item => allowed.Contains(item.SiteName, StringComparer.OrdinalIgnoreCase))
                .ToArray();
        }

        return Ok(ApiResponse<IReadOnlyCollection<WordPressSiteDto>>.Ok(installs));
    }

    [HttpGet("{siteName}")]
    public async Task<ActionResult<ApiResponse<WordPressSiteDto>>> DetectAsync(string siteName, CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        var install = await wordPressToolkitService.DetectAsync(siteName, cancellationToken);
        return Ok(ApiResponse<WordPressSiteDto>.Ok(install));
    }

    [HttpPost("{siteName}/maintenance")]
    public async Task<ActionResult<ApiResponse<WordPressSiteDto>>> SetMaintenanceAsync(
        string siteName,
        [FromBody] SetWordPressMaintenanceRequest request,
        CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        var install = await wordPressToolkitService.SetMaintenanceAsync(siteName, request.Enabled, cancellationToken);
        return Ok(ApiResponse<WordPressSiteDto>.Ok(install, "wp.maintenance_updated"));
    }

    // panel "Giriş yap": yöneticiyi siteye tek tıkla sokan, tek kullanımlık ve kısa ömürlü sihirli bağlantı üretir.
    [HttpPost("{siteName}/login-link")]
    public async Task<ActionResult<ApiResponse<WordPressLoginLinkDto>>> CreateLoginLinkAsync(string siteName, CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        var link = await wordPressToolkitService.CreateLoginLinkAsync(siteName, cancellationToken);
        return Ok(ApiResponse<WordPressLoginLinkDto>.Ok(link));
    }

    // panel "Güvenlik": WordPress güvenlik önlemleri demetini uygular (sürüm gizleme, XML-RPC pingback, yazar taraması,
    // dosya düzenleyici, anahtar yenileme).
    [HttpPost("{siteName}/harden")]
    public async Task<ActionResult<ApiResponse<WordPressHardenResultDto>>> HardenAsync(string siteName, CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        var result = await wordPressToolkitService.HardenAsync(siteName, cancellationToken);
        return Ok(ApiResponse<WordPressHardenResultDto>.Ok(result, "wp.hardened"));
    }

    // panel "Klonla": kaynak WP'yi yeni bir staging sitesine kopyalar. Klon, kaynağın sahibi müşterinin kapsamına
    // eklenir (Plesk: klon aynı abonelikte kalır) → Bayi de kendi sitesinin klonunu görür ve yönetir.
    [HttpPost("{siteName}/clone")]
    public async Task<ActionResult<ApiResponse<WordPressCloneResultDto>>> CloneAsync(
        string siteName,
        [FromBody] CloneWordPressRequest request,
        CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);

        // Kaynağın sahibi müşteri varsa: önce kota denetimi (Plesk gibi). Sahibi yoksa (yönetici sitesi) sınırsız.
        var ownerId = await customerScopes.GetSiteOwnerUserIdAsync(siteName, cancellationToken);
        if (ownerId is Guid quotaOwner)
        {
            var ownerScope = await customerScopes.GetScopeAsync(quotaOwner, cancellationToken);
            if (ownerScope.MaxSites > 0 && ownerScope.Sites.Count >= ownerScope.MaxSites)
            {
                throw new AppException("wp.clone_quota", 403, 403);
            }
        }

        var result = await wordPressToolkitService.CloneAsync(siteName, request, cancellationToken);

        // Klonu kaynağın kapsamına ata → müşteri/bayi yeni siteyi görür.
        if (ownerId is Guid assignOwner)
        {
            var scope = await customerScopes.GetScopeAsync(assignOwner, cancellationToken);
            if (!scope.Sites.Contains(result.TargetSiteName, StringComparer.OrdinalIgnoreCase))
            {
                await customerScopes.SetScopeAsync(assignOwner, scope.Sites.Append(result.TargetSiteName), scope.MaxSites, scope.MaxDiskMb, cancellationToken);
            }
        }

        return Ok(ApiResponse<WordPressCloneResultDto>.Ok(result, "wp.clone_done"));
    }

    // panel "Veri Kopyala": kaynak WP'nin dosya/DB'sini VAR OLAN hedefe taşır (staging→canlı). İki siteye de erişim gerekir.
    [HttpPost("{siteName}/copy-data")]
    public async Task<ActionResult<ApiResponse<WordPressCopyDataResultDto>>> CopyDataAsync(
        string siteName,
        [FromBody] CopyWordPressDataRequest request,
        CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, request.TargetSiteName ?? string.Empty, cancellationToken);
        var result = await wordPressToolkitService.CopyDataAsync(siteName, request, cancellationToken);
        return Ok(ApiResponse<WordPressCopyDataResultDto>.Ok(result, "wp.copy_done"));
    }
}
