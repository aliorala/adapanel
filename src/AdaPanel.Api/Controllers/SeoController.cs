using AdaPanel.Api.Security;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Seo;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Audit;
using AdaPanel.Infrastructure.Seo;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AdaPanel.Api.Controllers;

// SEO ARAÇ KUTUSU — sitenin kendi sayfalarını gezip teknik SEO sorunlarını bulur.
//
// KAPSAM DENETİMİ HER UÇTA (CustomerAccess.EnsureSiteAsync): bu, güvenliğin BİRİNCİ halkasıdır.
// Taranacak adres site adından ve o sitenin IIS bağlamalarından türetilir (SeoTargetResolver),
// istek gövdesinden DEĞİL. Yani kiracı yalnız kendi sitesini taratabilir; keyfi bir adres
// verilemediği için panel bir SSRF aracına dönüşemez. İkinci halka taşımadadır: tarayıcının
// soketi her zaman loopback'e bağlanır (SeoHttpTransport). Testi: SeoSsrfGuardTests.
//
// MÜŞTERİYE AÇIK: "sitem Google'da neden görünmüyor" sorusunun cevabı kendi sayfalarındadır ve
// bunu görebilmesi gerekir. Tarama okuma yapar, siteyi DEĞİŞTİRMEZ.
[ApiController]
[Authorize(Roles = "Administrator,Reseller,Customer")]
[RequireSubUserSection("sites")]
[Route("api/v1/seo/sites/{siteName}")]
public sealed class SeoController(
    ISeoService seoService,
    ICustomerScopeService customerScopes,
    IAuditService auditService) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ApiResponse<SeoOverviewDto>>> GetAsync(string siteName, CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        return Ok(ApiResponse<SeoOverviewDto>.Ok(await seoService.GetOverviewAsync(siteName, cancellationToken)));
    }

    [HttpGet("reports/{reportId}")]
    public async Task<ActionResult<ApiResponse<SeoScanReportDto>>> GetReportAsync(
        string siteName,
        string reportId,
        CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        return Ok(ApiResponse<SeoScanReportDto>.Ok(await seoService.GetReportAsync(siteName, reportId, cancellationToken)));
    }

    /// <summary>
    /// Taramayı KUYRUĞA ALIR ve hemen döner (durum: <c>queued</c>). Arayüz ilerlemeyi yoklar.
    /// </summary>
    [HttpPost("scan")]
    public async Task<ActionResult<ApiResponse<SeoScanStateDto>>> ScanAsync(
        string siteName,
        [FromBody] StartSeoScanRequest? request,
        CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        var subscriptionUserId = await CustomerAccess.GetSubscriptionUserIdAsync(customerScopes, User, cancellationToken);

        var payload = request ?? new StartSeoScanRequest();
        var state = await seoService.StartScanAsync(siteName, payload, subscriptionUserId, cancellationToken);

        // Tarama kaynak tüketen bir iştir ve müşterinin sitesine istek atar: kimin başlattığı
        // kayda geçmeli. Sınırlar da yazılır — sonradan "kim 200 sayfa taratmış" sorusu sorulur.
        await auditService.AuditAsync(
            HttpContext,
            "seo.scan",
            $"site:{siteName}",
            $"SEO scan queued (limit {state.PageLimit}, start '{payload.StartPath}')",
            subscriptionUserId);

        return Ok(ApiResponse<SeoScanStateDto>.Ok(state, "seo.scan_started"));
    }

    [HttpPost("cancel")]
    public async Task<ActionResult<ApiResponse<SeoScanStateDto>>> CancelAsync(string siteName, CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        var subscriptionUserId = await CustomerAccess.GetSubscriptionUserIdAsync(customerScopes, User, cancellationToken);

        var state = seoService.CancelScan(siteName);

        await auditService.AuditAsync(
            HttpContext,
            "seo.scan_cancel",
            $"site:{siteName}",
            "SEO scan cancelled",
            subscriptionUserId);

        return Ok(ApiResponse<SeoScanStateDto>.Ok(state, "seo.scan_cancelled"));
    }
}
