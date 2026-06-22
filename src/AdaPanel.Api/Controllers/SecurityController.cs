using AdaPanel.Api.Extensions;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Security;
using AdaPanel.Core.Dtos.Waf;
using AdaPanel.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AdaPanel.Api.Controllers;

[ApiController]
[Authorize(Roles = "Administrator")]
[Route("api/v1/security")]
public sealed class SecurityController(
    IPanelSecurityService panelSecurityService,
    IWafService wafService,
    IBruteForceProtectionService bruteForceProtection) : ControllerBase
{
    [HttpGet("bruteforce")]
    public async Task<ActionResult<ApiResponse<BruteForceStatusDto>>> GetBruteForceAsync(CancellationToken cancellationToken)
    {
        var status = await bruteForceProtection.GetStatusAsync(cancellationToken);
        return Ok(ApiResponse<BruteForceStatusDto>.Ok(status));
    }

    [HttpPut("bruteforce")]
    public async Task<ActionResult<ApiResponse<BruteForceStatusDto>>> UpdateBruteForceAsync(
        [FromBody] UpdateBruteForceSettingsRequest request,
        CancellationToken cancellationToken)
    {
        var status = await bruteForceProtection.UpdateSettingsAsync(request, cancellationToken);
        return Ok(ApiResponse<BruteForceStatusDto>.Ok(status, "security.bruteforce.saved"));
    }

    [HttpPost("bruteforce/unban")]
    public async Task<ActionResult<ApiResponse<BruteForceStatusDto>>> UnbanAsync(
        [FromBody] UnbanIpRequest request,
        CancellationToken cancellationToken)
    {
        var status = await bruteForceProtection.UnbanAsync(request.IpAddress, cancellationToken);
        return Ok(ApiResponse<BruteForceStatusDto>.Ok(status, "security.bruteforce.unbanned"));
    }

    [HttpGet("settings")]
    public async Task<ActionResult<ApiResponse<SecuritySettingsDto>>> GetSettingsAsync(CancellationToken cancellationToken)
    {
        var settings = await panelSecurityService.GetSettingsAsync(cancellationToken);
        return Ok(ApiResponse<SecuritySettingsDto>.Ok(settings));
    }

    [HttpPut("settings")]
    public async Task<ActionResult<ApiResponse<SecuritySettingsDto>>> UpdateSettingsAsync(
        [FromBody] UpdateSecuritySettingsRequest request,
        CancellationToken cancellationToken)
    {
        var settings = await panelSecurityService.UpdateSettingsAsync(
            request,
            User.GetRequiredUserId(),
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken);

        return Ok(ApiResponse<SecuritySettingsDto>.Ok(settings, "security.settings.updated"));
    }

    [HttpGet("/api/v1/sites/{siteName}/security")]
    public async Task<ActionResult<ApiResponse<SiteSecurityConfigurationDto>>> GetSiteSecurityAsync(string siteName, CancellationToken cancellationToken)
    {
        var configuration = await wafService.GetSiteConfigurationAsync(siteName, cancellationToken);
        return Ok(ApiResponse<SiteSecurityConfigurationDto>.Ok(configuration));
    }

    [HttpPost("/api/v1/sites/{siteName}/isolate")]
    public async Task<ActionResult<ApiResponse<SiteSecurityConfigurationDto>>> IsolateAsync(string siteName, CancellationToken cancellationToken)
    {
        var configuration = await wafService.IsolateAppPoolAsync(siteName, cancellationToken);
        return Ok(ApiResponse<SiteSecurityConfigurationDto>.Ok(configuration, "security.site_saved"));
    }

    [HttpPost("/api/v1/sites/{siteName}/acl")]
    public async Task<ActionResult<ApiResponse<SiteSecurityConfigurationDto>>> ApplyAclAsync(string siteName, [FromBody] ApplyFolderAclRequest request, CancellationToken cancellationToken)
    {
        var configuration = await wafService.ApplyFolderAclAsync(siteName, request, cancellationToken);
        return Ok(ApiResponse<SiteSecurityConfigurationDto>.Ok(configuration, "security.site_saved"));
    }

    [HttpPost("/api/v1/sites/{siteName}/ratelimit")]
    public async Task<ActionResult<ApiResponse<SiteSecurityConfigurationDto>>> SetRateLimitAsync(string siteName, [FromBody] SetSiteRateLimitRequest request, CancellationToken cancellationToken)
    {
        var configuration = await wafService.SetRateLimitAsync(siteName, request, cancellationToken);
        return Ok(ApiResponse<SiteSecurityConfigurationDto>.Ok(configuration, "security.site_saved"));
    }

    [HttpPost("/api/v1/sites/{siteName}/basic-rules")]
    public async Task<ActionResult<ApiResponse<SiteSecurityConfigurationDto>>> EnableBasicRulesAsync(string siteName, CancellationToken cancellationToken)
    {
        var configuration = await wafService.EnableBasicRulesAsync(siteName, cancellationToken);
        return Ok(ApiResponse<SiteSecurityConfigurationDto>.Ok(configuration, "security.site_saved"));
    }
}
