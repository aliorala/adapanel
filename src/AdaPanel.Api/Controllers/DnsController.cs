using AdaPanel.Api.Security;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AdaPanel.Api.Controllers;

// Reseller (Bayi) yalnız kendi müşterilerinin sitelerinin DNS bölgesini yönetebilir
// (EnsureSiteAsync). Windows DNS özelliği KURULUMU server-level → admin-only.
[ApiController]
[Authorize(Roles = "Administrator,Reseller")]
[Route("api/v1/sites/{siteName}/dns")]
public sealed class DnsController(IDnsService dnsService, ICustomerScopeService customerScopes) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ApiResponse<SiteDnsZoneDto>>> GetAsync(string siteName, CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        return Ok(ApiResponse<SiteDnsZoneDto>.Ok(await dnsService.GetZoneAsync(siteName, cancellationToken)));
    }

    [HttpPut("zone")]
    public async Task<ActionResult<ApiResponse<SiteDnsZoneDto>>> UpdateZoneAsync(
        string siteName,
        [FromBody] UpdateDnsZoneRequest request,
        [FromServices] IIisSiteService iisSiteService,
        [FromServices] IMailService mailService,
        [FromServices] ILogger<DnsController> logger,
        CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);

        // GÜVENLİK (çapraz-kiracı): site kapsamı doğrulanıyordu ama BÖLGE ADI serbestti
        // (DnsService: state.ZoneName = request.ZoneName). Kendi sitesinin bölge adını
        // "kurban.com" yapan bir kiracı, kurbanın alan adının DNS kayıtlarını yönetir hâle
        // geliyordu — A kaydını istediği yere çevirmek, sertifika almak dahil.
        await CustomerAccess.EnsureDomainClaimAsync(
            customerScopes, iisSiteService, mailService, User, request.ZoneName, siteName, logger, cancellationToken);

        return Ok(ApiResponse<SiteDnsZoneDto>.Ok(await dnsService.UpdateZoneAsync(siteName, request, cancellationToken), "dns.zone_saved"));
    }

    [HttpPost("windows/install")]
    [Authorize(Roles = "Administrator")]
    public async Task<ActionResult<ApiResponse<SiteDnsZoneDto>>> InstallWindowsDnsAsync(
        string siteName,
        CancellationToken cancellationToken)
        => Ok(ApiResponse<SiteDnsZoneDto>.Ok(await dnsService.InstallWindowsDnsAsync(siteName, cancellationToken), "dns.windows_installed"));

    [HttpPost("defaults")]
    public async Task<ActionResult<ApiResponse<SiteDnsZoneDto>>> ResetDefaultsAsync(
        string siteName,
        CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        return Ok(ApiResponse<SiteDnsZoneDto>.Ok(await dnsService.ResetDefaultsAsync(siteName, cancellationToken), "dns.defaults_restored"));
    }

    // DNSSEC aç/kapat (Windows DNS zone-imzalama). Server-level imzalama → admin-only.
    [HttpPost("dnssec")]
    [Authorize(Roles = "Administrator")]
    public async Task<ActionResult<ApiResponse<SiteDnsZoneDto>>> SetDnssecAsync(
        string siteName,
        [FromBody] SetDnssecRequest request,
        CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        return Ok(ApiResponse<SiteDnsZoneDto>.Ok(
            await dnsService.SetDnssecAsync(siteName, request.Enabled, cancellationToken),
            request.Enabled ? "dns.dnssec_enabled" : "dns.dnssec_disabled"));
    }

    [HttpPost("records")]
    public async Task<ActionResult<ApiResponse<SiteDnsZoneDto>>> UpsertRecordAsync(
        string siteName,
        [FromBody] UpsertDnsRecordRequest request,
        CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        return Ok(ApiResponse<SiteDnsZoneDto>.Ok(await dnsService.UpsertRecordAsync(siteName, request, cancellationToken), "dns.record_saved"));
    }

    [HttpDelete("records/{recordId}")]
    public async Task<ActionResult<ApiResponse<SiteDnsZoneDto>>> DeleteRecordAsync(
        string siteName,
        string recordId,
        CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        return Ok(ApiResponse<SiteDnsZoneDto>.Ok(await dnsService.DeleteRecordAsync(siteName, recordId, cancellationToken), "dns.record_deleted"));
    }
}
