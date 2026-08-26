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
    ISeoLogService seoLogService,
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

    /// <summary>
    /// Saklanan SON günlük analizi. Hiç çalıştırılmadıysa <c>null</c> döner.
    /// </summary>
    /// <remarks>
    /// Kapsam denetimi burada da ZORUNLU: erişim günlüğü ziyaretçi IP'lerini ve sorgu dizelerini
    /// taşır, yani rapor okuma ucu da kiracı sınırıdır — yalnız analizi BAŞLATAN uç değil.
    /// </remarks>
    [HttpGet("logs")]
    public async Task<ActionResult<ApiResponse<SeoLogReportDto?>>> GetLogReportAsync(
        string siteName,
        CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        return Ok(ApiResponse<SeoLogReportDto?>.Ok(await seoLogService.GetLatestAsync(siteName, cancellationToken)));
    }

    /// <summary>
    /// GÜNLÜK ANALİZİNİ ÇALIŞTIRIR: arama motoru botlarının siteyi nasıl gezdiğini IIS erişim
    /// günlüklerinden ölçer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Neden POST:</b> okur ama diskte MB'larca günlük açar, ZSTD arşivi çözer ve (istenirse)
    /// ters DNS sorgusu yapar; ayrıca sonucu diske YAZAR. Yenilenebilir bir GET olsaydı tarayıcı
    /// ön belleği ve arama motorları bu işi kendiliğinden tetikleyebilirdi.
    /// </para>
    /// <para>
    /// <b>İstek gövdesi HEDEFİ BELİRLEMEZ</b> (bkz. <see cref="StartSeoLogAnalysisRequest"/>):
    /// okunacak klasör site adından ve o sitenin IIS kimliğinden türetilir. Gövdede bir yol alanı
    /// olsaydı kiracı BAŞKA kiracının erişim günlüğünü okutabilirdi.
    /// </para>
    /// </remarks>
    [HttpPost("logs")]
    public async Task<ActionResult<ApiResponse<SeoLogReportDto>>> AnalyzeLogsAsync(
        string siteName,
        [FromBody] StartSeoLogAnalysisRequest? request,
        CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        var subscriptionUserId = await CustomerAccess.GetSubscriptionUserIdAsync(customerScopes, User, cancellationToken);

        var payload = request ?? new StartSeoLogAnalysisRequest();
        var report = await seoLogService.AnalyzeAsync(siteName, payload, cancellationToken);

        // Erişim günlüğü hassas veridir (ziyaretçi IP'leri, sorgu dizeleri): KİMİN okuduğu kayda
        // geçmeli. Pencere ve ters DNS kararı da yazılır — sonradan "kim 30 günü taratmış" ve
        // "panel hangi IP'lere DNS sorgusu yapmış" soruları sorulur.
        await auditService.AuditAsync(
            HttpContext,
            "seo.log_analysis",
            $"site:{siteName}",
            $"SEO log analysis ({report.PeriodDays} days, {report.FilesScanned} files, verifyBots={payload.VerifyBots})",
            subscriptionUserId);

        return Ok(ApiResponse<SeoLogReportDto>.Ok(report, "seo.log_analysis_completed"));
    }
}
