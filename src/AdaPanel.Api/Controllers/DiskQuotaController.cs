using AdaPanel.Api.Security;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Audit;
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
/// <para>
/// <b>Denetim kaydı:</b> kota, müşterinin SATIN ALDIĞI sınırdır ve yazma uçları müşterinin kendisine
/// kapalıdır — yani buradaki her değişikliği BAŞKASI (yönetici ya da bayi) yapar. Kota düşürüldüğünde
/// müşteri bunu ancak dosya yazamayınca fark eder; kaydın <c>subscriptionUserId</c>'si doldurulmazsa
/// kiracı, kendi sitesine yapılan bu dokunuşu denetim görünümünde hiç göremez.
/// </para>
/// </remarks>
[ApiController]
[Authorize(Roles = "Administrator,Reseller,Customer")]
[RequireSubUserSection("sites")]
[Route("api/v1/sites/{siteName}/quota")]
public sealed class DiskQuotaController(
    IDiskQuotaService diskQuotaService,
    ICustomerScopeService customerScopes,
    IAuditService auditService) : ControllerBase
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

        // ÖNCEKİ sınır değişiklikten önce ölçülür: FSRM yalnız güncel kotayı tutar, "kaçtan kaça
        // çekildi" bilgisi yazma anından sonra hiçbir yerde kalmaz.
        var oncesi = await diskQuotaService.GetQuotaAsync(siteName, cancellationToken);

        var quota = await diskQuotaService.SetQuotaAsync(siteName, request, cancellationToken);

        await auditService.AuditAsync(
            HttpContext,
            "quota.set",
            $"site:{siteName}",
            $"Disk quota set to {request.LimitMegabytes}MB ({(request.SoftLimit ? "soft" : "hard")}); "
            + $"before: {(oncesi.QuotaEnabled ? $"{oncesi.LimitBytes / 1024 / 1024}MB ({(oncesi.SoftLimit ? "soft" : "hard")})" : "none")}; "
            + $"applied: {quota.QuotaEnabled}.",
            await SiteSahibiAsync(siteName, cancellationToken));

        return Ok(ApiResponse<SiteDiskQuotaDto>.Ok(quota, "quota.saved"));
    }

    [HttpDelete]
    [Authorize(Roles = "Administrator,Reseller")]
    public async Task<ActionResult<ApiResponse<SiteDiskQuotaDto>>> RemoveAsync(string siteName, CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);

        var oncesi = await diskQuotaService.GetQuotaAsync(siteName, cancellationToken);
        var quota = await diskQuotaService.RemoveQuotaAsync(siteName, cancellationToken);

        // Kotanın KALDIRILMASI, satılan sınırın fiilen sınırsıza çevrilmesidir; ayrıca kaldırma
        // sessizce başarısız olabildiği (DiskQuotaService, -ErrorAction SilentlyContinue) için
        // ÖLÇÜLEN sonuç da yazılır — "kaldırıldı" demek ile kalkmış olmak aynı şey değil.
        await auditService.AuditAsync(
            HttpContext,
            "quota.removed",
            $"site:{siteName}",
            $"Disk quota removed (was: {(oncesi.QuotaEnabled ? $"{oncesi.LimitBytes / 1024 / 1024}MB" : "none")}); "
            + $"quota still enabled after removal: {quota.QuotaEnabled}.",
            await SiteSahibiAsync(siteName, cancellationToken));

        return Ok(ApiResponse<SiteDiskQuotaDto>.Ok(quota, "quota.removed"));
    }

    /// <summary>Disk kotası için gereken Windows rolünü (FSRM) kurar — gerekçe: IDiskQuotaService.</summary>
    [HttpPost("install")]
    [Authorize(Roles = "Administrator")]
    public async Task<ActionResult<ApiResponse<SiteDiskQuotaDto>>> InstallFsrmAsync(string siteName, CancellationToken cancellationToken)
    {
        var quota = await diskQuotaService.InstallFsrmAsync(siteName, cancellationToken);

        // SUNUCU GENELİ bir Windows rolü kurar ve yeniden başlatma gerektirebilir — site başına bir
        // işlem değildir, bu yüzden kiracıya değil sunucuya yazılır (subscriptionUserId yok).
        await auditService.AuditAsync(
            HttpContext,
            "quota.fsrm.installed",
            "server",
            $"FSRM Windows role installation requested (triggered from site '{siteName}'); "
            + $"FSRM available afterwards: {quota.FsrmAvailable}.");

        return Ok(ApiResponse<SiteDiskQuotaDto>.Ok(quota, "quota.fsrm_installed"));
    }

    /// <summary>
    /// Sitenin abonelik sahibi — kiracının, kotasına yapılan dokunuşu görebilmesi için.
    /// </summary>
    /// <remarks>
    /// Kapsam denetimi (EnsureSiteAsync) çağıranın siteye erişebildiğini doğrular; sahibi ise
    /// KAYNAĞIN kime ait olduğudur ve çağırandan farklı olabilir (yönetici/bayi dokunuşu). Kaydın
    /// kiracıya görünmesini sağlayan alan budur.
    /// </remarks>
    private async Task<Guid?> SiteSahibiAsync(string siteName, CancellationToken cancellationToken)
    {
        try
        {
            return await customerScopes.GetSiteOwnerUserIdAsync(siteName, cancellationToken);
        }
        catch
        {
            return null;
        }
    }
}
