using AdaPanel.Api.Security;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AdaPanel.Api.Controllers;

/// <summary>
/// Sitenin FSRM tabanlı disk kotası.
/// </summary>
/// <remarks>
/// <para>
/// Controller tümüyle <c>Administrator</c>'a kilitliydi ve KAPSAM DENETİMİ HİÇ YOKTU — yani rol
/// eklemek tek başına yapılsaydı, herhangi bir müşteri site adını değiştirerek BAŞKA bir kiracının
/// kotasını okuyup değiştirebilirdi. Bu yüzden her eyleme <see cref="CustomerAccess.EnsureSiteAsync"/>
/// eklendi.
/// </para>
/// <para>
/// Yetki ayrımı, "kotayı KİM koyar" sorusuna göre yapıldı:
/// </para>
/// <list type="bullet">
/// <item>
/// <b>Okuma (GET) → müşteri dahil.</b> Kendi disk kullanımını ve sınırını görmek, aboneliğin en
/// temel bilgisidir; müşteri "diskim doldu mu" sorusunu kendi yanıtlayabilmeli.
/// </item>
/// <item>
/// <b>Yazma (PUT/DELETE) → yönetici ve bayi.</b> Kota, müşterinin SATIN ALDIĞI sınırdır; onu
/// değiştirme yetkisi müşteriye verilseydi kendi limitini yükseltip planından kaçabilirdi.
/// Bayi kendi müşterisinin kotasını yönetir (Plesk paritesi) — EnsureSiteAsync onu kendi
/// kapsamıyla sınırlar.
/// </item>
/// <item>
/// <b>FSRM kurulumu (install) → yalnız yönetici.</b> Bu bir SUNUCU GENELİ Windows rolü kurar
/// (File Server Resource Manager) ve sunucuyu yeniden başlatma gerektirebilir; site başına bir
/// işlem değildir.
/// </item>
/// </list>
/// </remarks>
[ApiController]
[Authorize(Roles = "Administrator,Reseller,Customer")]
[RequireSubUserSection("sites")]
[Route("api/v1/sites/{siteName}/quota")]
public sealed class DiskQuotaController(
    IDiskQuotaService diskQuotaService,
    ICustomerScopeService customerScopes) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ApiResponse<SiteDiskQuotaDto>>> GetAsync(string siteName, CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        var quota = await diskQuotaService.GetQuotaAsync(siteName, cancellationToken);
        return Ok(ApiResponse<SiteDiskQuotaDto>.Ok(quota));
    }

    [HttpPut]
    [Authorize(Roles = "Administrator,Reseller")]
    public async Task<ActionResult<ApiResponse<SiteDiskQuotaDto>>> SetAsync(
        string siteName,
        [FromBody] SetSiteDiskQuotaRequest request,
        CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        var quota = await diskQuotaService.SetQuotaAsync(siteName, request, cancellationToken);
        return Ok(ApiResponse<SiteDiskQuotaDto>.Ok(quota, "quota.saved"));
    }

    [HttpDelete]
    [Authorize(Roles = "Administrator,Reseller")]
    public async Task<ActionResult<ApiResponse<SiteDiskQuotaDto>>> RemoveAsync(string siteName, CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        var quota = await diskQuotaService.RemoveQuotaAsync(siteName, cancellationToken);
        return Ok(ApiResponse<SiteDiskQuotaDto>.Ok(quota, "quota.removed"));
    }

    /// <summary>Disk kotası için gereken Windows rolünü (FSRM) kurar — gerekçe: IDiskQuotaService.</summary>
    [HttpPost("install")]
    [Authorize(Roles = "Administrator")]
    public async Task<ActionResult<ApiResponse<SiteDiskQuotaDto>>> InstallFsrmAsync(string siteName, CancellationToken cancellationToken)
    {
        var quota = await diskQuotaService.InstallFsrmAsync(siteName, cancellationToken);
        return Ok(ApiResponse<SiteDiskQuotaDto>.Ok(quota, "quota.fsrm_installed"));
    }

}
