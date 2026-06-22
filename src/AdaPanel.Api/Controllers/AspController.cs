using AdaPanel.Api.Extensions;
using AdaPanel.Api.Security;
using AdaPanel.Core.Dtos.Asp;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AdaPanel.Api.Controllers;

[ApiController]
[Authorize(Roles = "Administrator,Reseller")]
[Route("api/v1/sites")]
public sealed class AspController(
    IAspNetService aspNetService,
    ICustomerScopeService customerScopes,
    IAuditService auditService) : ControllerBase
{
    [HttpGet("{siteName}/asp")]
    public async Task<ActionResult<ApiResponse<AspSiteConfigurationDto>>> GetSiteConfigurationAsync(
        string siteName,
        CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        var configuration = await aspNetService.GetSiteConfigurationAsync(siteName, cancellationToken);
        return Ok(ApiResponse<AspSiteConfigurationDto>.Ok(configuration));
    }

    [HttpPost("{siteName}/asp/classic")]
    public async Task<ActionResult<ApiResponse<AspSiteConfigurationDto>>> SetClassicAspAsync(
        string siteName,
        [FromBody] UpdateClassicAspRequest request,
        CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        var configuration = await aspNetService.SetClassicAspAsync(siteName, request, cancellationToken);
        await WriteAuditAsync("aspnet.classic.updated", siteName, cancellationToken);
        return Ok(ApiResponse<AspSiteConfigurationDto>.Ok(configuration, "aspnet.classic_asp_updated"));
    }

    [HttpPost("{siteName}/dotnet")]
    public async Task<ActionResult<ApiResponse<AspSiteConfigurationDto>>> SetDotNetVersionAsync(
        string siteName,
        [FromBody] UpdateDotNetRuntimeRequest request,
        CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        var configuration = await aspNetService.SetDotNetVersionAsync(siteName, request, cancellationToken);
        await WriteAuditAsync("aspnet.dotnet.updated", siteName, cancellationToken);
        return Ok(ApiResponse<AspSiteConfigurationDto>.Ok(configuration, "aspnet.dotnet_updated"));
    }

    [HttpPost("{siteName}/mixed")]
    public async Task<ActionResult<ApiResponse<AspSiteConfigurationDto>>> EnableMixedHandlersAsync(
        string siteName,
        [FromBody] EnableMixedHandlersRequest request,
        CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        var configuration = await aspNetService.EnableMixedHandlersAsync(siteName, request, cancellationToken);
        await WriteAuditAsync("aspnet.mixed.updated", siteName, cancellationToken);
        return Ok(ApiResponse<AspSiteConfigurationDto>.Ok(configuration, "aspnet.mixed_handlers_updated"));
    }

    private Task WriteAuditAsync(string action, string siteName, CancellationToken cancellationToken)
    {
        return auditService.WriteAsync(
            action,
            "aspnet",
            $"ASP/ASP.NET settings for '{siteName}' were updated.",
            User.GetRequiredUserId(),
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken);
    }
}
