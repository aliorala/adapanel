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
        CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
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
