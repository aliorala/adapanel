using AdaPanel.Api.Security;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.AppRuntimes;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Interfaces;
using AdaPanel.Data.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AdaPanel.Api.Controllers;

[ApiController]
[Authorize(Roles = "Administrator,Reseller")]
[Route("api/v1/sites/{siteName}/apps/runtimes")]
public sealed class AppRuntimeInventoryController(
    IAppRuntimeInventoryService inventory,
    ICustomerScopeService scopes,
    AdaPanelDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ApiResponse<IReadOnlyCollection<AppRuntimeInventoryDto>>>> GetAsync(
        string siteName, CancellationToken cancellationToken)
    {
        if (!User.IsInRole("Administrator") && !User.IsInRole("Reseller"))
            throw new AppException("auth.forbidden", 403, 403);
        var actor = await SiteActorAuthority.CaptureAsync(db, scopes, User, cancellationToken);
        await CustomerAccess.EnsureSiteAsync(scopes, User, siteName, cancellationToken);
        var result = await inventory.GetAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        await actor.RevalidateAsync(db, scopes, User, cancellationToken);
        await CustomerAccess.EnsureSiteAsync(scopes, User, siteName, cancellationToken);
        return Ok(ApiResponse<IReadOnlyCollection<AppRuntimeInventoryDto>>.Ok(result));
    }
}
