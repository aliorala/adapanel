using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Seo;
using AdaPanel.Core.Interfaces;

namespace AdaPanel.Infrastructure.Seo;

/// <summary>
/// Denetleyicinin çağırdığı SEO servisi: okur ve kuyruğa atar. Uzun süren işi KENDİSİ yapmaz
/// (bkz. <see cref="SeoScanRunner"/>).
/// </summary>
public sealed class SeoScanService(
    IIisSiteService iisSiteService,
    SeoReportStore reportStore,
    SeoScanQueue queue) : ISeoService
{
    public async Task<SeoOverviewDto> GetOverviewAsync(string siteName, CancellationToken cancellationToken = default)
    {
        var site = await iisSiteService.GetSiteAsync(siteName, cancellationToken);
        var resolution = SeoTargetResolver.Resolve(site);
        var history = await reportStore.ReadAsync(site.Name, cancellationToken);

        return new SeoOverviewDto
        {
            SiteName = site.Name,
            // Boş konak, arayüze "bu site taranamaz" demenin yoludur; düğme buna bakarak kapanır.
            Host = resolution.Target?.Host ?? string.Empty,
            State = queue.GetState(site.Name),
            Latest = history.Reports.FirstOrDefault(),
            History = history.Reports.Select(Summarize).ToArray()
        };
    }

    public async Task<SeoScanStateDto> StartScanAsync(
        string siteName,
        StartSeoScanRequest request,
        Guid? requestedBy,
        CancellationToken cancellationToken = default)
    {
        if (!SeoLinkPolicy.TryNormalizeStartPath(request.StartPath, out var startPath))
        {
            throw new AppException("seo.invalid_start_path", 400, 400);
        }

        // Site GERÇEKTEN var mı ve taranabilir mi — kuyruğa atmadan ÖNCE bakılır. Aksi hâlde
        // kullanıcı "başlatıldı" der, dakikalar sonra "failed" bir rapor görür ve sebebini
        // ancak raporu açınca öğrenirdi.
        var site = await iisSiteService.GetSiteAsync(siteName, cancellationToken);
        var resolution = SeoTargetResolver.Resolve(site);
        if (resolution.Target is null)
        {
            throw new AppException($"seo.{resolution.Reason}", 400, 400);
        }

        var limits = SeoCrawlLimits.FromRequest(request.MaxPages);
        var job = new SeoScanJob(Guid.NewGuid().ToString("N"), site.Name, startPath, limits.MaxPages, requestedBy);

        var sonuc = queue.TryEnqueue(job);
        if (sonuc == SeoEnqueueResult.AlreadyRunning)
        {
            throw new AppException("seo.scan_already_running", 409, 409);
        }

        if (sonuc == SeoEnqueueResult.QueueFull)
        {
            throw new AppException("seo.queue_full", 429, 429);
        }

        return queue.GetState(site.Name);
    }

    public SeoScanStateDto CancelScan(string siteName)
    {
        if (!queue.Cancel(siteName))
        {
            throw new AppException("seo.no_active_scan", 404, 404);
        }

        return queue.GetState(siteName);
    }

    public async Task<SeoScanReportDto> GetReportAsync(string siteName, string reportId, CancellationToken cancellationToken = default)
    {
        var site = await iisSiteService.GetSiteAsync(siteName, cancellationToken);
        var history = await reportStore.ReadAsync(site.Name, cancellationToken);
        var report = history.Reports.FirstOrDefault(item =>
            string.Equals(item.Id, reportId, StringComparison.OrdinalIgnoreCase));

        return report ?? throw new AppException("seo.report_not_found", 404, 404);
    }

    /// <summary>Geçmiş listesi için özet: bulgular önem derecesine göre sayılır.</summary>
    public static SeoScanSummaryDto Summarize(SeoScanReportDto report) => new()
    {
        Id = report.Id,
        CompletedAtUtc = report.CompletedAtUtc,
        Status = report.Status,
        PagesCrawled = report.PagesCrawled,
        ErrorCount = report.Findings.Count(finding => finding.Severity == "error"),
        WarningCount = report.Findings.Count(finding => finding.Severity == "warn"),
        InfoCount = report.Findings.Count(finding => finding.Severity == "info"),
        NewFindingCount = report.NewFindingCount,
        ResolvedFindingCount = report.ResolvedFindingCount
    };
}
