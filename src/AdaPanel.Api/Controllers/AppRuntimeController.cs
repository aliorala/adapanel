using AdaPanel.Api.Extensions;
using AdaPanel.Api.Security;
using AdaPanel.Core.Dtos.AppRuntimes;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Audit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AdaPanel.Api.Controllers;

[ApiController]
[Authorize(Roles = "Administrator,Reseller")]
[Route("api/v1/sites")]
public sealed class AppRuntimeController(
    IAppRuntimeService appRuntimeService,
    ICustomerScopeService customerScopes,
    IAuditService auditService) : ControllerBase
{
    [HttpGet("{siteName}/apps")]
    public async Task<ActionResult<ApiResponse<AppRuntimeSiteConfigurationDto>>> GetSiteConfigurationAsync(
        string siteName,
        CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        var configuration = await appRuntimeService.GetSiteConfigurationAsync(siteName, cancellationToken);
        return Ok(ApiResponse<AppRuntimeSiteConfigurationDto>.Ok(configuration));
    }

    [HttpPost("{siteName}/node")]
    public async Task<ActionResult<ApiResponse<AppRuntimeSiteConfigurationDto>>> HostNodeAppAsync(
        string siteName,
        [FromBody] HostNodeAppRequest request,
        CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        var configuration = await appRuntimeService.HostNodeAppAsync(siteName, request, cancellationToken);
        await WriteAuditAsync("appruntime.node.updated", siteName, "Node.js", cancellationToken);
        return Ok(ApiResponse<AppRuntimeSiteConfigurationDto>.Ok(configuration, "appruntime.node_updated"));
    }

    [HttpPost("{siteName}/python")]
    public async Task<ActionResult<ApiResponse<AppRuntimeSiteConfigurationDto>>> HostPythonAppAsync(
        string siteName,
        [FromBody] HostPythonAppRequest request,
        CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        var configuration = await appRuntimeService.HostPythonAppAsync(siteName, request, cancellationToken);
        await WriteAuditAsync("appruntime.python.updated", siteName, "Python", cancellationToken);
        return Ok(ApiResponse<AppRuntimeSiteConfigurationDto>.Ok(configuration, "appruntime.python_updated"));
    }

    /// <remarks>
    /// <b>Kiracı bağı:</b> kayıt <c>WriteAsync</c> ile konusuz yazılıyordu. Bu uçlar yöneticiye ve
    /// bayiye de açık; onların kiracının sitesinde yaptığı değişiklik, kaydın <c>UserId</c>'si
    /// kendileri olduğu için kiracının denetim görünümünde HİÇ çıkmıyordu. Konu artık sitenin
    /// abonelik sahibidir; site sahipsizse (yöneticinin kendi sitesi) boş kalır.
    /// </remarks>
    private async Task WriteAuditAsync(string action, string siteName, string runtimeLabel, CancellationToken cancellationToken)
    {
        await auditService.WriteForSubscriptionAsync(
            action,
            $"appruntime:{siteName}",
            $"{runtimeLabel} hosting was configured for '{siteName}'.",
            User.GetRequiredUserId(),
            await AuditSubject.SiteOwnerAsync(customerScopes, siteName, cancellationToken),
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken);
    }
}
