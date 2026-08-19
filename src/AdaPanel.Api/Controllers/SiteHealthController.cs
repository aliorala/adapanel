using AdaPanel.Api.Security;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Health;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Audit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AdaPanel.Api.Controllers;

// Sitenin DIŞARIDAN görünen sağlığı. Kapsam denetimi DnsController ile AYNI
// (CustomerAccess.EnsureSiteAsync).
//
// MÜŞTERİYE AÇILDI — ama yalnız TEŞHİS, ONARIM DEĞİL:
//   • GET /health ve GET /health/repair hiçbir şey DEĞİŞTİRMEZ; "sitem neden açılmıyor"
//     sorusunun yanıtı buradadır ve müşterinin bunu kendi görememesi, çözülebilir her sorunu
//     destek talebine çeviriyordu.
//   • POST /health/repair müşteriye AÇILMADI. Sebep ölçüldü (DomainRefreshService.RefreshAsync):
//     tek site onarılırken RunServerWideAsync da çalışıyor — posta sunucusunun SERTİFİKASINI
//     eşitliyor ve karşılama adını (server hostname) ayarlıyor. Bunlar sunucuda TEKTİR, yani
//     bir kiracının isteği bütün kiracıları etkiler; ayrıca her çağrı birden çok CLI süreci
//     başlattığı için ucuz bir DoS yüzeyi olurdu.
[ApiController]
[Authorize(Roles = "Administrator,Reseller,Customer")]
[RequireSubUserSection("sites")]
[Route("api/v1/sites/{siteName}/health")]
public sealed class SiteHealthController(
    ISiteHealthService siteHealthService,
    IDomainRefreshService domainRefreshService,
    ICustomerScopeService customerScopes,
    IAuditService auditService) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ApiResponse<SiteHealthDto>>> GetAsync(string siteName, CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        return Ok(ApiResponse<SiteHealthDto>.Ok(await siteHealthService.GetSiteHealthAsync(siteName, cancellationToken)));
    }

    /// <summary>
    /// Bu sitede onarılacak bir şey var mı? Hiçbir şey DEĞİŞTİRMEZ.
    /// <para>
    /// Site detay sayfası bunu okuyup uyarı gösterir. Öncesinde yalnız sunucu geneli bir denetim
    /// vardı: kullanıcı "2 alan adı eski" yazısını görüyor ama HANGİ site olduğunu öğrenemiyordu.
    /// </para>
    /// </summary>
    [HttpGet("repair")]
    public async Task<ActionResult<ApiResponse<DomainRefreshResultDto>>> InspectRepairAsync(string siteName, CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        return Ok(ApiResponse<DomainRefreshResultDto>.Ok(await domainRefreshService.InspectAsync(siteName, cancellationToken)));
    }

    /// <summary>
    /// Bu sitenin eksiklerini tamamlar ve NE YAPILDIĞINI döner.
    /// <para>
    /// SUNUCU GENELİ yan etkileri olduğu için müşteriye kapalı (gerekçe sınıf başlığında).
    /// </para>
    /// </summary>
    [HttpPost("repair")]
    [Authorize(Roles = "Administrator,Reseller")]
    public async Task<ActionResult<ApiResponse<DomainRefreshResultDto>>> RepairAsync(string siteName, CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        var result = await domainRefreshService.RefreshAsync(siteName, cancellationToken);

        // DENETİM KAYDI: "onarım" adı zararsız görünür ama bu uç DNS KAYDI EKLER, posta sunucusunun
        // TLS sertifikasını değiştirir ve önizleme bağlamalarını KALDIRIR — hepsi sitenin dışarıdan
        // görünen davranışını değiştirir. Üstelik sunucu geneli yan etkileri vardır (posta sunucusu
        // TEKTİR, bir kiracının onarımı hepsini etkiler). NE YAPILDIĞI sayı sayı yazılır; "onarıldı"
        // demek, neyin değiştiğini söylemez.
        Guid? siteSahibi = null;
        try
        {
            siteSahibi = await customerScopes.GetSiteOwnerUserIdAsync(siteName, cancellationToken);
        }
        catch
        {
            // Sahip çözülemedi → kayıt sunucu geneli yazılır.
        }

        await auditService.AuditAsync(
            HttpContext,
            "site.health.repaired",
            $"site:{siteName}",
            $"Site repair applied to zone '{result.ZoneName}': "
            + $"{result.AddedDnsRecords.Count} DNS record(s) added, "
            + $"{result.AppliedMailRecords} mail record(s) applied, "
            + $"mail certificate refreshed: {result.MailCertificateRefreshed}, "
            + $"{result.RemovedPreviewBindings} preview binding(s) removed"
            + (string.IsNullOrWhiteSpace(result.Error) ? "." : $"; error: {result.Error}"),
            siteSahibi);

        return Ok(ApiResponse<DomainRefreshResultDto>.Ok(result, "health.domains_refreshed"));
    }
}
