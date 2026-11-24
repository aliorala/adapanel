using AdaPanel.Api.Extensions;
using AdaPanel.Api.Security;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.AppRuntimes;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Audit;
using AdaPanel.Infrastructure.Database;
using AdaPanel.Infrastructure.Iis;
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
        var configuration = await ConfigureAuthorizedAsync(siteName,
            beforeMutation => appRuntimeService.HostNodeAppAsync(siteName, request, beforeMutation, cancellationToken), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        await WriteAuditAsync("appruntime.node.updated", siteName, "Node.js", cancellationToken);
        return Ok(ApiResponse<AppRuntimeSiteConfigurationDto>.Ok(configuration, "appruntime.node_updated"));
    }

    [HttpPost("{siteName}/python")]
    public async Task<ActionResult<ApiResponse<AppRuntimeSiteConfigurationDto>>> HostPythonAppAsync(
        string siteName,
        [FromBody] HostPythonAppRequest request,
        CancellationToken cancellationToken)
    {
        var configuration = await ConfigureAuthorizedAsync(siteName,
            beforeMutation => appRuntimeService.HostPythonAppAsync(siteName, request, beforeMutation, cancellationToken), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        await WriteAuditAsync("appruntime.python.updated", siteName, "Python", cancellationToken);
        return Ok(ApiResponse<AppRuntimeSiteConfigurationDto>.Ok(configuration, "appruntime.python_updated"));
    }

    [HttpPost("{siteName}/apps/stop")]
    public async Task<ActionResult<ApiResponse<AppRuntimeSiteConfigurationDto>>> StopAppAsync(string siteName, CancellationToken cancellationToken)
    {
        var configuration = await ConfigureAuthorizedAsync(siteName,
            before => appRuntimeService.StopAppAsync(siteName, before, cancellationToken), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        await WriteAuditAsync("appruntime.stopped", siteName, "Node.js/Python", cancellationToken, $"Hosted application was stopped for '{siteName}'.");
        return Ok(ApiResponse<AppRuntimeSiteConfigurationDto>.Ok(configuration, "appruntime.stopped"));
    }

    [HttpPost("{siteName}/apps/start")]
    public async Task<ActionResult<ApiResponse<AppRuntimeSiteConfigurationDto>>> StartAppAsync(string siteName, CancellationToken cancellationToken)
    {
        var configuration = await ConfigureAuthorizedAsync(siteName,
            before => appRuntimeService.StartAppAsync(siteName, before, cancellationToken), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        await WriteAuditAsync("appruntime.started", siteName, "Node.js/Python", cancellationToken, $"Hosted application was started for '{siteName}'.");
        return Ok(ApiResponse<AppRuntimeSiteConfigurationDto>.Ok(configuration, "appruntime.started"));
    }

    [HttpPost("{siteName}/apps/restart")]
    public async Task<ActionResult<ApiResponse<AppRuntimeSiteConfigurationDto>>> RestartAppAsync(string siteName, CancellationToken cancellationToken)
    {
        var configuration = await ConfigureAuthorizedAsync(siteName,
            before => appRuntimeService.RestartAppAsync(siteName, before, cancellationToken), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        await WriteAuditAsync("appruntime.restarted", siteName, "Node.js/Python", cancellationToken, $"Hosted application was restarted for '{siteName}'.");
        return Ok(ApiResponse<AppRuntimeSiteConfigurationDto>.Ok(configuration, "appruntime.restarted"));
    }

    /// <summary>Barındırmayı kaldırır. YIKICI: site adı gövdede AYNEN onaylanmadan çalışmaz.</summary>
    [HttpPost("{siteName}/apps/remove")]
    public async Task<ActionResult<ApiResponse<AppRuntimeRemovalResultDto>>> RemoveAppAsync(
        string siteName,
        [FromBody] RemoveAppRuntimeRequest request,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(request.ConfirmSiteName?.Trim(), siteName, StringComparison.OrdinalIgnoreCase))
            throw new AppException("appruntime.confirmation_required", 400, 400);
        var result = await ConfigureAuthorizedAsync(siteName,
            before => appRuntimeService.RemoveAppAsync(siteName, request.DeleteLogs, before, cancellationToken), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        await WriteAuditAsync("appruntime.removed", siteName, "Node.js/Python", cancellationToken,
            $"Hosted application was removed for '{siteName}' (removed: {string.Join(',', result.Removed)}; leftovers: {result.Leftovers.Length}).");
        // KALINTI VARSA başarı anahtarı DEĞİL: kullanıcı kısmi temizliği "tamam" sanmasın.
        return Ok(ApiResponse<AppRuntimeRemovalResultDto>.Ok(result,
            result.Leftovers.Length == 0 ? "appruntime.removed" : "appruntime.removed_partial"));
    }

    [HttpGet("{siteName}/apps/logs")]
    public async Task<ActionResult<ApiResponse<AppRuntimeLogsDto>>> GetLogsAsync(
        string siteName, [FromQuery] int tail = 200, CancellationToken cancellationToken = default)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        var logs = await appRuntimeService.GetLogsAsync(siteName, tail, cancellationToken);
        return Ok(ApiResponse<AppRuntimeLogsDto>.Ok(logs));
    }

    private async Task<T> ConfigureAuthorizedAsync<T>(string siteName,
        Func<Func<SiteDto, CancellationToken, Task>, Task<T>> configure,
        CancellationToken cancellationToken)
    {
        if (!User.IsInRole("Administrator") && !User.IsInRole("Reseller"))
            throw new AppException("auth.forbidden", 403, 403);
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        var services = HttpContext.RequestServices;
        var sites = services.GetRequiredService<IIisSiteService>();
        var gates = services.GetRequiredService<IConcurrencyLock>();
        using var target = await SiteMutationGuard.CaptureAsync(sites, gates, siteName, cancellationToken);
        var db = services.GetRequiredService<AdaPanelDbContext>();
        var actor = await SiteActorAuthority.CaptureAsync(db, customerScopes, User, cancellationToken);
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
    private async Task WriteAuditAsync(string action, string siteName, string runtimeLabel, CancellationToken cancellationToken, string? description = null)
    {
        await auditService.WriteForSubscriptionAsync(
            action,
            $"appruntime:{siteName}",
            description ?? $"{runtimeLabel} hosting was configured for '{siteName}'.",
            User.GetRequiredUserId(),
            await AuditSubject.SiteOwnerAsync(customerScopes, siteName, cancellationToken),
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken);
    }
}
