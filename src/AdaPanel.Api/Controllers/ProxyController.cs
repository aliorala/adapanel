using AdaPanel.Api.Extensions;
using AdaPanel.Api.Security;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Proxy;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Audit;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Database;
using AdaPanel.Infrastructure.Iis;
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
        var configuration = await ConfigureAuthorizedAsync(siteName,
            beforeMutation => reverseProxyService.EnableProxyAsync(siteName, request, beforeMutation, cancellationToken), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        await WriteAuditAsync("proxy.enabled", siteName, "Reverse proxy was enabled or updated.", cancellationToken);
        return Ok(ApiResponse<ReverseProxySiteConfigurationDto>.Ok(configuration, "proxy.proxy_enabled"));
    }

    [HttpDelete("{siteName}/proxy")]
    public async Task<ActionResult<ApiResponse<ReverseProxySiteConfigurationDto>>> RemoveProxyAsync(
        string siteName,
        CancellationToken cancellationToken)
    {
        var configuration = await ConfigureAuthorizedAsync(siteName,
            beforeMutation => reverseProxyService.RemoveProxyAsync(siteName, beforeMutation, cancellationToken), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        await WriteAuditAsync("proxy.removed", siteName, "Reverse proxy rules were removed.", cancellationToken);
        return Ok(ApiResponse<ReverseProxySiteConfigurationDto>.Ok(configuration, "proxy.proxy_removed"));
    }

    [HttpPost("{siteName}/rewrite")]
    public async Task<ActionResult<ApiResponse<ReverseProxySiteConfigurationDto>>> AddRewriteRuleAsync(
        string siteName,
        [FromBody] AddRewriteRuleRequest request,
        CancellationToken cancellationToken)
    {
        var configuration = await ConfigureAuthorizedAsync(siteName,
            beforeMutation => reverseProxyService.AddRewriteRuleAsync(siteName, request, beforeMutation, cancellationToken), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        await WriteAuditAsync("proxy.rewrite.updated", siteName, $"Rewrite rule '{request.Name}' was saved.", cancellationToken);
        return Ok(ApiResponse<ReverseProxySiteConfigurationDto>.Ok(configuration, "proxy.rewrite_rule_saved"));
    }

    private async Task<ReverseProxySiteConfigurationDto> ConfigureAuthorizedAsync(string siteName,
        Func<Func<SiteDto, CancellationToken, Task>, Task<ReverseProxySiteConfigurationDto>> configure,
        CancellationToken cancellationToken)
    {
        if (!User.IsInRole("Administrator") && !User.IsInRole("Reseller"))
            throw new AppException("auth.forbidden", 403, 403);
        var services = HttpContext.RequestServices;
        var sites = services.GetRequiredService<IIisSiteService>();
        var gates = services.GetRequiredService<IConcurrencyLock>();
        // Preserve the approved generation even while the first tenant lookup waits.
        using var target = await SiteMutationGuard.CaptureAsync(sites, gates, siteName, cancellationToken);
        var db = services.GetRequiredService<AdaPanelDbContext>();
        var actor = await SiteActorAuthority.CaptureAsync(db, customerScopes, User, cancellationToken);
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        using var subscription = await DatabaseLifecycle.EnterSubscriptionAsync(actor.SubscriptionId, cancellationToken);
        await actor.RevalidateAsync(db, customerScopes, User, cancellationToken);
        return await configure(async (current, token) =>
        {
            target.ValidateSnapshot(current);
            await actor.RevalidateAsync(db, customerScopes, User, token);
            await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, token);
        });
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
