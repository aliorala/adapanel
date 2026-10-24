using System.Security.Claims;
using AdaPanel.Api.Security;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Dtos.Ssl;
using AdaPanel.Core.Interfaces;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Audit;
using AdaPanel.Infrastructure.Database;
using AdaPanel.Infrastructure.Ssl;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AdaPanel.Api.Controllers;

[ApiController]
[Authorize(Roles = "Administrator")]
[RequireSubUserSection("sites")]
[Route("api/v1/ssl/repair")]
public sealed class SslRepairController(SslBindingRepairService repair, AdaPanelDbContext db,
    ICustomerScopeService scopes, IAuditService auditService) : ControllerBase
{
    private string Actor => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? throw new AppException("auth.forbidden", 403, 403);

    [HttpPost("plan")]
    public async Task<ActionResult<ApiResponse<SslRepairPlan>>> PlanAsync(SslRepairTarget request, CancellationToken token)
    {
        var authority = await SiteActorAuthority.CaptureAsync(db, scopes, User, token);
        if (authority.Role != "Administrator") throw new AppException("auth.forbidden", 403, 403);
        using var lease = await DatabaseLifecycle.EnterSubscriptionAsync(authority.SubscriptionId, token);
        async Task AuthorizeAsync(SiteDto site, CancellationToken ct)
        {
            await authority.RevalidateAsync(db, scopes, User, ct);
            await CustomerAccess.EnsureSiteAsync(scopes, User, site.Name, ct);
        }
        return Ok(ApiResponse<SslRepairPlan>.Ok(await repair.PlanAsync(request, Actor, AuthorizeAsync, token)));
    }

    [HttpPost("apply")]
    public async Task<ActionResult<ApiResponse<SslRepairResult>>> ApplyAsync(SslRepairApplyRequest request, CancellationToken token)
    {
        var authority = await SiteActorAuthority.CaptureAsync(db, scopes, User, token);
        if (authority.Role != "Administrator") throw new AppException("auth.forbidden", 403, 403);
        using var lease = await DatabaseLifecycle.EnterSubscriptionAsync(authority.SubscriptionId, token);
        async Task AuthorizeAsync(SiteDto site, CancellationToken ct)
        {
            await authority.RevalidateAsync(db, scopes, User, ct);
            await CustomerAccess.EnsureSiteAsync(scopes, User, site.Name, ct);
        }
        var result = await repair.ApplyAsync(request.PlanToken, Actor, AuthorizeAsync, token);
        await auditService.AuditAsync(HttpContext, result.TlsVerified ? "ssl.binding_repaired" : "ssl.binding_repair_unverified",
            $"ssl:{result.SiteName}", $"binding={result.BindingInformation}; previous={result.PreviousThumbprint}; " +
            $"applied={result.AppliedThumbprint}; store={result.StoreName}; bindingApplied={result.BindingApplied}; tlsVerified={result.TlsVerified}; detail={result.Detail}",
            await AuditSubject.SiteOwnerAsync(scopes, result.SiteName, token));
        return Ok(ApiResponse<SslRepairResult>.Ok(result));
    }
}
