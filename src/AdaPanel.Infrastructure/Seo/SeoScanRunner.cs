using System.Diagnostics;
using AdaPanel.Core.Dtos.Seo;
using AdaPanel.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace AdaPanel.Infrastructure.Seo;

/// <summary>
/// Kuyruktan alınan tek bir tarama işini baştan sona yürütür ve raporu kaydeder.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden servisten ayrı:</b> denetleyicinin çağırdığı servis (<see cref="SeoScanService"/>)
/// yalnız okur ve kuyruğa atar; asıl iş burada, arka plan işçisinin açtığı kapsamda koşar. İkisini
/// tek sınıfta toplamak, uzun süren işin istek kapsamındaki bağımlılıkları (DbContext gibi) elde
/// tutmasına yol açardı.
/// </para>
/// <para>
/// <b>Hata da rapordur:</b> tarama patlarsa sessizce kaybolmaz; <c>failed</c> durumlu bir rapor
/// yazılır. Aksi hâlde kullanıcı "tara" der, hiçbir şey olmaz ve neden olmadığını öğrenemezdi.
/// </para>
/// </remarks>
public sealed class SeoScanRunner(
    IIisSiteService iisSiteService,
    SeoReportStore reportStore,
    SeoScanQueue queue,
    ILogger<SeoScanRunner> logger)
{
    /// <summary>Sitemap'ten kaç adres GERÇEKTEN açılıp denenir. Tamamını denemek ikinci bir tarama olurdu.</summary>
    public const int SitemapProbeLimit = 5;

    public async Task RunAsync(SeoScanJob job, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var startedAt = DateTime.UtcNow;
        var limits = SeoCrawlLimits.FromRequest(job.MaxPages);

        try
        {
            var site = await iisSiteService.GetSiteAsync(job.SiteName, cancellationToken);
            var resolution = SeoTargetResolver.Resolve(site);
            if (resolution.Target is not { } target)
            {
                await SaveAsync(job, BuildFailure(job, startedAt, stopwatch, limits, resolution.Reason), cancellationToken);
                return;
            }

            // TOPLAM SÜRE: kullanıcı iptali ile birleştirilir. Sayfa sınırı tek başına yetmez —
            // her sayfası yavaş bir site, sınıra ulaşmadan da işçiyi saatlerce meşgul edebilirdi.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(limits.TotalTimeout);

            using var client = SeoHttpTransport.CreateClient(limits);
            var crawler = new SeoCrawler(client, limits);

            var crawl = await crawler.CrawlAsync(
                target,
                job.StartPath,
                pages => queue.ReportProgress(job.SiteName, pages),
                timeout.Token);

            // robots/sitemap denetimi iptal edilmiş bir taramada ATLANIR: kullanıcı durdurduysa
            // yeni istek atmak, durdurma isteğine saygısızlık olurdu.
            var robots = crawl.Interrupted ? new SeoRobotsDto { Error = "interrupted" } : await CheckRobotsAsync(crawler, target, timeout.Token);
            var sitemap = crawl.Interrupted
                ? new SeoSitemapDto { Path = "/sitemap.xml", Error = "interrupted" }
                : await CheckSitemapAsync(crawler, target, robots, timeout.Token);

            var findings = SeoFindingRules.Evaluate(crawl.Pages, robots, sitemap, crawl.LimitReached).ToList();

            var iptalEdildi = crawl.Interrupted && cancellationToken.IsCancellationRequested;
            if (crawl.Interrupted && !iptalEdildi)
            {
                // Süre sınırına takıldı: bu, "sorun yok" değil "her yere bakamadık" demektir.
                findings.Add(new SeoFindingDto
                {
                    Key = "time_limit_reached",
                    Severity = "info",
                    Detail = $"{limits.TotalTimeout.TotalMinutes:0} dk"
                });
            }

            var history = await reportStore.ReadAsync(job.SiteName, cancellationToken);
            var previous = history.Reports.FirstOrDefault(report => report.Status == "completed");
            var (yeni, cozulen) = Compare(previous?.Findings, findings);

            var report = new SeoScanReportDto
            {
                AnalysisVersion = 1,
                Id = job.Id,
                SiteName = job.SiteName,
                Host = target.Host,
                StartedAtUtc = startedAt,
                CompletedAtUtc = DateTime.UtcNow,
                DurationMs = stopwatch.ElapsedMilliseconds,
                Status = iptalEdildi ? "cancelled" : "completed",
                PagesCrawled = crawl.Pages.Count,
                PageLimit = limits.MaxPages,
                LimitReached = crawl.LimitReached,
                Findings = findings,
                Pages = crawl.Pages,
                Robots = robots,
                Sitemap = sitemap,
                NewFindingCount = yeni,
                ResolvedFindingCount = cozulen,
                ComparedToId = previous?.Id ?? string.Empty
            };

            await SaveAsync(job, report, CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "SEO taramasi basarisiz oldu: {Site}", job.SiteName);
            await SaveAsync(
                job,
                BuildFailure(job, startedAt, stopwatch, limits, $"{exception.GetType().Name}: {exception.Message}"),
                CancellationToken.None);
        }
    }

    /// <summary>
    /// İki taramanın bulgularını karşılaştırır. Kimlik = anahtar + adres; aynı sayfadaki aynı
    /// sorun aynı bulgudur, ölçülen değer (süre gibi) değişse bile.
    /// </summary>
    public static (int NewFindings, int ResolvedFindings) Compare(
        IReadOnlyCollection<SeoFindingDto>? previous,
        IReadOnlyCollection<SeoFindingDto> current)
    {
        if (previous is null || previous.Count == 0)
        {
            return (0, 0);
        }

        var oncekiler = previous.Select(Identity).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var simdikiler = current.Select(Identity).ToHashSet(StringComparer.OrdinalIgnoreCase);

        return (
            simdikiler.Count(key => !oncekiler.Contains(key)),
            oncekiler.Count(key => !simdikiler.Contains(key)));

        static string Identity(SeoFindingDto finding) => $"{finding.Key}|{finding.Url}";
    }

    private async Task<SeoRobotsDto> CheckRobotsAsync(SeoCrawler crawler, SeoCrawlTarget target, CancellationToken cancellationToken)
    {
        var fetch = await crawler.FetchAsync(target, "/robots.txt", cancellationToken);
        if (fetch.Error.Length > 0)
        {
            return new SeoRobotsDto { Error = fetch.Error, BotAccess = SeoBotAccessAnalyzer.Analyze(null, 0) };
        }

        if (fetch.StatusCode != 200)
        {
            return new SeoRobotsDto { StatusCode = fetch.StatusCode, BotAccess = SeoBotAccessAnalyzer.Analyze(null, fetch.StatusCode) };
        }

        if (fetch.BodyTruncated || fetch.ContentType.Contains("html", StringComparison.OrdinalIgnoreCase))
            return new SeoRobotsDto { StatusCode = fetch.StatusCode, Error = "invalid_or_truncated_robots", BotAccess = SeoBotAccessAnalyzer.Analyze(null, 200, true) };

        var parse = SeoRobotsAnalyzer.ParseRobots(fetch.Body);
        return new SeoRobotsDto
        {
            Found = true,
            StatusCode = fetch.StatusCode,
            DisallowsAll = parse.DisallowsAll,
            SitemapUrls = parse.SitemapUrls,
            BotAccess = SeoBotAccessAnalyzer.Analyze(fetch.Body)
        };
    }

    /// <summary>
    /// Sitemap'i bulur, okur ve içindeki adreslerin GERÇEKTEN açıldığını örnekleyerek denetler.
    /// </summary>
    /// <remarks>
    /// <b>Güvenlik:</b> sitemap içeriğini site sahibi yazar. İçindeki her adres
    /// <see cref="SeoLinkPolicy"/> kapısından geçirilir; siteye ait olmayanlar DENENMEZ, yalnız
    /// listelenir. Aksi hâlde sitemap'e <c>http://169.254.169.254/</c> yazmak, panele istek
    /// attırmanın en kolay yolu olurdu.
    /// </remarks>
    private async Task<SeoSitemapDto> CheckSitemapAsync(
        SeoCrawler crawler,
        SeoCrawlTarget target,
        SeoRobotsDto robots,
        CancellationToken cancellationToken)
    {
        var path = "/sitemap.xml";
        foreach (var bildirilen in robots.SitemapUrls)
        {
            var decision = SeoLinkPolicy.Classify(bildirilen, "/", target);
            if (decision.Kind == SeoLinkKind.Internal)
            {
                path = decision.Value;
                break;
            }
        }

        var fetch = await crawler.FetchAsync(target, path, cancellationToken);
        if (fetch.Error.Length > 0)
        {
            return new SeoSitemapDto { Path = path, Error = fetch.Error };
        }

        if (fetch.StatusCode != 200)
        {
            return new SeoSitemapDto { Path = path, StatusCode = fetch.StatusCode };
        }

        var locations = SeoRobotsAnalyzer.ParseSitemapLocations(fetch.Body);
        var icerdekiler = new List<string>();
        var disaridakiler = new List<string>();

        foreach (var location in locations)
        {
            var decision = SeoLinkPolicy.Classify(location, "/", target);
            if (decision.Kind == SeoLinkKind.Internal)
            {
                icerdekiler.Add(decision.Value);
            }
            else if (decision.Kind == SeoLinkKind.External && disaridakiler.Count < 20)
            {
                disaridakiler.Add(decision.Value);
            }
        }

        var acilmayanlar = new List<string>();
        var denenen = 0;
        foreach (var aday in icerdekiler.Distinct(StringComparer.OrdinalIgnoreCase).Take(SitemapProbeLimit))
        {
            var deneme = await crawler.FetchAsync(target, aday, cancellationToken);
            denenen++;
            if (deneme.Error.Length > 0)
            {
                acilmayanlar.Add($"{aday}: {deneme.Error}");
            }
            else if (deneme.StatusCode >= 400)
            {
                acilmayanlar.Add($"{aday}: HTTP {deneme.StatusCode}");
            }
        }

        return new SeoSitemapDto
        {
            Path = path,
            Found = true,
            StatusCode = fetch.StatusCode,
            UrlCount = locations.Count,
            CheckedCount = denenen,
            UnreachableUrls = acilmayanlar,
            ExternalUrls = disaridakiler
        };
    }

    private static SeoScanReportDto BuildFailure(
        SeoScanJob job,
        DateTime startedAt,
        Stopwatch stopwatch,
        SeoCrawlLimits limits,
        string error) => new()
        {
            AnalysisVersion = 1,
            Id = job.Id,
            SiteName = job.SiteName,
            StartedAtUtc = startedAt,
            CompletedAtUtc = DateTime.UtcNow,
            DurationMs = stopwatch.ElapsedMilliseconds,
            Status = "failed",
            PageLimit = limits.MaxPages,
            Error = error
        };

    private async Task SaveAsync(SeoScanJob job, SeoScanReportDto report, CancellationToken cancellationToken)
    {
        try
        {
            await reportStore.AppendAsync(job.SiteName, report, cancellationToken);
        }
        catch (Exception exception)
        {
            // Rapor yazılamazsa tarama boşa gitmiş olur ama panel ÇÖKMEMELİ; günlükte iz kalır.
            logger.LogError(exception, "SEO raporu kaydedilemedi: {Site}", job.SiteName);
        }
    }
}
