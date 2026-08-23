using AdaPanel.Api.Extensions;
using AdaPanel.Api.Security;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Proxy;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Audit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AdaPanel.Api.Controllers;

[ApiController]
[Authorize(Roles = "Administrator,Reseller")]
[Route("api/v1/sites")]
public sealed class ProxyController(
    IReverseProxyService reverseProxyService,
    ICustomerScopeService customerScopes,
    IAuditService auditService) : ControllerBase
{
    [HttpGet("{siteName}/proxy")]
    public async Task<ActionResult<ApiResponse<ReverseProxySiteConfigurationDto>>> GetSiteConfigurationAsync(
        string siteName,
        CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        var configuration = await reverseProxyService.GetSiteConfigurationAsync(siteName, cancellationToken);
        return Ok(ApiResponse<ReverseProxySiteConfigurationDto>.Ok(configuration));
    }

    [HttpPost("{siteName}/proxy")]
    public async Task<ActionResult<ApiResponse<ReverseProxySiteConfigurationDto>>> EnableProxyAsync(
        string siteName,
        [FromBody] EnableReverseProxyRequest request,
        CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        var configuration = await reverseProxyService.EnableProxyAsync(siteName, request, cancellationToken);
        await WriteAuditAsync("proxy.enabled", siteName, "Reverse proxy was enabled or updated.", cancellationToken);
        return Ok(ApiResponse<ReverseProxySiteConfigurationDto>.Ok(configuration, "proxy.proxy_enabled"));
    }

    [HttpDelete("{siteName}/proxy")]
    public async Task<ActionResult<ApiResponse<ReverseProxySiteConfigurationDto>>> RemoveProxyAsync(
        string siteName,
        CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        var configuration = await reverseProxyService.RemoveProxyAsync(siteName, cancellationToken);
        await WriteAuditAsync("proxy.removed", siteName, "Reverse proxy rules were removed.", cancellationToken);
        return Ok(ApiResponse<ReverseProxySiteConfigurationDto>.Ok(configuration, "proxy.proxy_removed"));
    }

    [HttpPost("{siteName}/rewrite")]
    public async Task<ActionResult<ApiResponse<ReverseProxySiteConfigurationDto>>> AddRewriteRuleAsync(
        string siteName,
        [FromBody] AddRewriteRuleRequest request,
        CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        var configuration = await reverseProxyService.AddRewriteRuleAsync(siteName, request, cancellationToken);
        await WriteAuditAsync("proxy.rewrite.updated", siteName, $"Rewrite rule '{request.Name}' was saved.", cancellationToken);
        return Ok(ApiResponse<ReverseProxySiteConfigurationDto>.Ok(configuration, "proxy.rewrite_rule_saved"));
    }

    /// <remarks>
    /// <b>Kiracı bağı:</b> kayıt <c>WriteAsync</c> ile konusuz yazılıyordu. Bu uçlar yöneticiye ve
    /// bayiye de açık; onların kiracının sitesinde yaptığı değişiklik, kaydın <c>UserId</c>'si
    /// kendileri olduğu için kiracının denetim görünümünde HİÇ çıkmıyordu. Konu artık sitenin
    /// abonelik sahibidir; site sahipsizse (yöneticinin kendi sitesi) boş kalır.
    /// </remarks>
    private async Task WriteAuditAsync(string action, string siteName, string detail, CancellationToken cancellationToken)
    {
        await auditService.WriteForSubscriptionAsync(
            action,
            $"proxy:{siteName}",
            $"{detail} Site: '{siteName}'.",
            User.GetRequiredUserId(),
            await AuditSubject.SiteOwnerAsync(customerScopes, siteName, cancellationToken),
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken);
    }
}
