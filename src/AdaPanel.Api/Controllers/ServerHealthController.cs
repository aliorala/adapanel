using System.Security.Claims;
using AdaPanel.Api.Extensions;
using AdaPanel.Api.Security;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Health;
using AdaPanel.Core.Enums;
using AdaPanel.Core.Interfaces;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Audit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AdaPanel.Api.Controllers;

// panel Server Health / Repair Kit benzeri: kritik servis durumları + tek-tık yeniden başlat (admin).
// (Anonim liveness ucu HealthController'da; bu admin-only operasyonel sağlık panosudur.)
//
// DENETİM KAYDI: iki uç da SUNUCU GENELİ kesinti üretir.
//   • `restart` bir Windows servisini (IIS, MariaDB, posta, DNS...) yeniden başlatır — o servise
//     bağlı BÜTÜN siteler/kiracılar saniyeler boyunca düşer. Bir olay incelemesinde ilk soru
//     "kesinti bir arıza mıydı yoksa biri mi yeniden başlattı" olur; Windows servis yeniden
//     başlatmalarının kimin yaptığını olay günlüğünden çıkarmak güvenilir değildir.
//   • `refresh-domains` sunucudaki TÜM alan adlarına DNS kaydı ekler ve posta sertifikasını
//     eşitler; tek istekle onlarca zone değişir.
[ApiController]
[Authorize(Roles = "Administrator")]
[Route("api/v1/server-health")]
public sealed class ServerHealthController(
    IHealthService healthService,
    IDomainRefreshService domainRefreshService,
    IAuditService auditService) : ControllerBase
{
    /// <summary>
    /// Sunucudaki TÜM alan adlarını panelin güncel varsayılanlarına taşır (eksikleri ekler, var
    /// olanı değiştirmez). Panel güncellendiğinde eski alan adları yeni ayarları almadığı için
    /// gerekli; ne eklendiği alan adı bazında raporlanır.
    /// </summary>
    [HttpPost("refresh-domains")]
    public async Task<ActionResult<ApiResponse<DomainRefreshSummaryDto>>> RefreshDomainsAsync(CancellationToken cancellationToken)
    {
        var summary = await domainRefreshService.RefreshAllAsync(cancellationToken);

        await auditService.AuditAsync(
            HttpContext,
            "server.domains.refreshed",
            "server",
            $"Server-wide domain refresh: {summary.UpdatedSites} of {summary.TotalSites} site(s) updated, "
            + $"{summary.FailedSites} failed, {summary.ServerFindings.Count} server-level finding(s).");

        return Ok(ApiResponse<DomainRefreshSummaryDto>.Ok(summary, "health.domains_refreshed"));
    }

    /// <summary>Değişiklik yapmadan kaç alan adının eski kaldığını söyler (uyarı rozeti için).</summary>
    [HttpGet("refresh-domains")]
    public async Task<ActionResult<ApiResponse<DomainRefreshSummaryDto>>> InspectDomainsAsync(CancellationToken cancellationToken)
    {
        return Ok(ApiResponse<DomainRefreshSummaryDto>.Ok(await domainRefreshService.InspectAllAsync(cancellationToken)));
    }

    [HttpGet]
    public async Task<ActionResult<ApiResponse<IReadOnlyCollection<ServiceHealthDto>>>> GetAsync(CancellationToken cancellationToken)
    {
        return Ok(ApiResponse<IReadOnlyCollection<ServiceHealthDto>>.Ok(await healthService.GetHealthAsync(cancellationToken)));
    }

    [HttpPost("restart")]
    public async Task<ActionResult<ApiResponse<ServiceHealthDto>>> RestartAsync(
        [FromBody] RestartServiceRequest request,
        CancellationToken cancellationToken)
    {
        var name = request.Name;
        var database = HttpContext.RequestServices.GetRequiredService<AdaPanelDbContext>();
        var authority = await CaptureAdministratorAsync(database, cancellationToken);
        var health = await healthService.RestartServiceAsync(name, async token =>
        {
            if (await CaptureAdministratorAsync(database, token) != authority)
                throw new AppException("auth.unauthorized", 401, 401);
        }, cancellationToken);

        // The service returns only after the exact target is measured as Running.
        // Use that immutable identity for the successful operation's audit record.
        await auditService.AuditAsync(
            HttpContext,
            "server.service.restarted",
            $"service:{health.Name}",
            $"Windows service '{health.Name}' ({health.Label}) restarted; status afterwards: {health.Status}.");

        return Ok(ApiResponse<ServiceHealthDto>.Ok(health, "health.restarted"));
    }

    private async Task<(Guid UserId, string Stamp)> CaptureAdministratorAsync(AdaPanelDbContext database, CancellationToken token)
    {
        var userId = User.GetRequiredUserId();
        var actor = await database.Users.AsNoTracking().SingleOrDefaultAsync(user => user.Id == userId, token);
        if (actor is null || !actor.IsActive || actor.Role != UserRole.Administrator ||
            User.FindFirst(ClaimTypes.Role)?.Value != nameof(UserRole.Administrator))
            throw new AppException("auth.unauthorized", 401, 401);
        var stamp = CookieSessionValidator.ComputeStamp(actor.PasswordHash);
        if (User.FindFirst(CookieSessionValidator.CredentialStampClaim)?.Value is { } claimed && claimed != stamp)
            throw new AppException("auth.unauthorized", 401, 401);
        return (userId, stamp);
    }
}
