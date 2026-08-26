using AdaPanel.Infrastructure.Support;
using AdaPanel.Core.Dtos.Statistics;
using AdaPanel.Core.Interfaces;

namespace AdaPanel.Infrastructure.Statistics;

/// <summary>
/// IIS W3C genişletilmiş erişim loglarını ayrıştırarak site trafik istatistikleri
/// üretir. Harici bir bağımlılık (AWStats/GoAccess) gerektirmez; logları doğrudan
/// okur ve <c>#Fields</c> başlığına göre sütunları eşler.
/// </summary>
/// <remarks>
/// Biçim bilgisi (yönerge satırları, sütun eşlemesi, boşluk ayracı) BU DOSYADA DEĞİL,
/// <see cref="W3cLogReader"/> içindedir — SEO günlük analizi de aynı ayrıştırıcıyı kullanır.
/// Buradaki iş yalnız TOPLAMA'dır (hangi alan hangi sayaca gider).
/// </remarks>
public sealed class WebStatisticsService(IIisSiteService iisSiteService) : IWebStatisticsService
{
    private const int MaxLinesScanned = 2_000_000;
    private const int TopCount = 15;

    public async Task<WebStatisticsDto> GetSiteStatisticsAsync(string siteName, int days, CancellationToken cancellationToken = default)
    {
        var site = await iisSiteService.GetSiteAsync(siteName, cancellationToken);
        var periodDays = Math.Clamp(days, 1, 365);
        var cutoff = DateTime.UtcNow.Date.AddDays(-(periodDays - 1));

        // IIS'in GERÇEK günlük dizini (sabit kodlu değil) — gerekçe: IisLogPaths.
        var logsDirectory = IisLogPaths.ResolveSiteDirectory(site.Id);

        var result = new WebStatisticsDto
        {
            SiteName = siteName,
            PeriodDays = periodDays,
            GeneratedAtUtc = DateTime.UtcNow,
            LogPath = logsDirectory
        };

        if (!Directory.Exists(logsDirectory))
        {
            return result;
        }

        var files = new DirectoryInfo(logsDirectory)
            .GetFiles("*.log", SearchOption.TopDirectoryOnly)
            .Where(file => file.LastWriteTimeUtc.Date >= cutoff)
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ToArray();

        var aggregator = new Aggregator();
        var linesScanned = 0;

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (linesScanned >= MaxLinesScanned)
            {
                break;
            }

            await ParseFileAsync(file.FullName, cutoff, aggregator, () => linesScanned++, () => linesScanned, cancellationToken);
        }

        aggregator.Fill(result);
        return result;
    }

    /// <summary>
    /// W3C log satırlarını ayrıştırıp istatistik üretir. Birim testleri tarafından
    /// dosya sistemine bağlı kalmadan kullanılabilir.
    /// </summary>
    public static WebStatisticsDto AnalyzeLines(IEnumerable<string> lines, DateTime cutoff)
    {
        var aggregator = new Aggregator();

        foreach (var record in W3cLogReader.Read(lines))
        {
            aggregator.Add(record, cutoff);
        }

        var result = new WebStatisticsDto { GeneratedAtUtc = DateTime.UtcNow };
        aggregator.Fill(result);
        return result;
    }

    private static async Task ParseFileAsync(
        string path,
        DateTime cutoff,
        Aggregator aggregator,
        Action onLine,
        Func<int> currentCount,
        CancellationToken cancellationToken)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);

        await foreach (var record in W3cLogReader.ReadAsync(reader, cancellationToken))
        {
            if (currentCount() >= MaxLinesScanned)
            {
                break;
            }

            onLine();
            aggregator.Add(record, cutoff);
        }
    }

    /// <summary>
    /// Ayrı ziyaretçi kümesinin üst sınırı. Aşılırsa sayaç doyar ve DTO bunu "yaklaşık" diye
    /// bildirir — 250.000'i "gerçek sayı" gibi göstermek yanlış bilgi olurdu.
    /// </summary>
    private const int MaxUniqueVisitors = 250_000;

    /// <summary>
    /// Sayfa/kaynak sözlüklerinin üst sınırı. Anahtarlar İSTEMCİNİN yazdığı değerlerdir
    /// (URL yolu, Referer başlığı) ve IIS 404'leri de günlüğe yazar; sınırsız bırakılırsa bir
    /// güvenlik tarayıcısı tek istekte yüzlerce MB biriktirir.
    /// </summary>
    private const int MaxDistinctKeys = 50_000;

    private sealed class Aggregator
    {
        private readonly HashSet<string> uniqueVisitors = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, long> pages = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, long> referrers = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, long> daily = new(StringComparer.Ordinal);

        /// <summary>Ziyaretçi kümesi sınıra dayandı mı (sayı artık kesin değil).</summary>
        public bool VisitorsApproximate { get; private set; }

        /// <summary>
        /// Sözlüğü sınır içinde tutar. Dolduğunda EN SEYREK anahtarlar atılır, yenileri değil:
        /// tersi yapılsaydı önce gelen çöp sözlüğü doldurur ve sonradan gelen GERÇEK popüler
        /// sayfalar hiç sayılamazdı. Yalnız ilk 15 gösterildiği için seyrek kuyruğu atmak
        /// cevabı değiştirmez.
        /// </summary>
        private static void Budala(Dictionary<string, long> map)
        {
            if (map.Count <= MaxDistinctKeys)
            {
                return;
            }

            var esik = 1L;
            while (map.Count > MaxDistinctKeys / 2 && esik < 1_000_000)
            {
                foreach (var anahtar in map.Where(item => item.Value <= esik).Select(item => item.Key).ToArray())
                {
                    map.Remove(anahtar);
                }

                esik *= 2;
            }
        }

        private long totalRequests;
        private long totalBytes;
        private long status2xx;
        private long status3xx;
        private long status4xx;
        private long status5xx;

        public void Add(W3cLogRecord record, DateTime cutoff)
        {
            var date = record.Get("date");
            // IIS W3C 'date' alanı ISO yyyy-MM-dd; InvariantCulture ile parse (Türkçe locale'de de doğru, kültür-bağımsız).
            if (!string.IsNullOrEmpty(date) &&
                DateTime.TryParse(date, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var parsedDate) &&
                parsedDate.Date < cutoff)
            {
                return;
            }

            totalRequests++;

            var clientIp = record.Get("c-ip");
            if (!string.IsNullOrEmpty(clientIp) && clientIp != "-")
            {
                if (uniqueVisitors.Count < MaxUniqueVisitors)
                {
                    uniqueVisitors.Add(clientIp);
                }
                else
                {
                    VisitorsApproximate = true;
                }
            }

            var uri = record.Get("cs-uri-stem");
            if (!string.IsNullOrEmpty(uri) && uri != "-")
            {
                Increment(pages, uri);
                Budala(pages);
            }

            var referer = record.Get("cs(Referer)");
            if (!string.IsNullOrEmpty(referer) && referer != "-")
            {
                Increment(referrers, NormalizeReferer(referer));
                Budala(referrers);
            }

            if (!string.IsNullOrEmpty(date))
            {
                Increment(daily, date);
            }

            var status = record.Get("sc-status");
            if (int.TryParse(status, out var statusCode))
            {
                switch (statusCode)
                {
                    case >= 200 and < 300: status2xx++; break;
                    case >= 300 and < 400: status3xx++; break;
                    case >= 400 and < 500: status4xx++; break;
                    case >= 500: status5xx++; break;
                }
            }

            var bytes = record.Get("sc-bytes");
            if (long.TryParse(bytes, out var byteCount))
            {
                totalBytes += byteCount;
            }
        }

        public void Fill(WebStatisticsDto result)
        {
            result.HasData = totalRequests > 0;
            result.TotalRequests = totalRequests;
            result.UniqueVisitors = uniqueVisitors.Count;
            result.UniqueVisitorsApproximate = VisitorsApproximate;
            result.TotalBytes = totalBytes;
            result.Status2xx = status2xx;
            result.Status3xx = status3xx;
            result.Status4xx = status4xx;
            result.Status5xx = status5xx;
            result.TopPages = Top(pages);
            result.TopReferrers = Top(referrers);
            result.DailyHits = daily
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new StatBucketDto { Key = pair.Key, Count = pair.Value })
                .ToArray();
        }

        private static IReadOnlyCollection<StatBucketDto> Top(Dictionary<string, long> source) =>
            source
                .OrderByDescending(pair => pair.Value)
                .Take(TopCount)
                .Select(pair => new StatBucketDto { Key = pair.Key, Count = pair.Value })
                .ToArray();

        private static void Increment(Dictionary<string, long> map, string key) =>
            map[key] = map.TryGetValue(key, out var existing) ? existing + 1 : 1;

        private static string NormalizeReferer(string referer)
        {
            if (Uri.TryCreate(referer, UriKind.Absolute, out var uri))
            {
                return uri.Host;
            }

            return referer.Length > 80 ? referer[..80] : referer;
        }
    }
}
