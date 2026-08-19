using System.Diagnostics;
using System.Net;
using System.Text;
using AdaPanel.Core.Dtos.Seo;

namespace AdaPanel.Infrastructure.Seo;

/// <summary>Tek bir isteğin ham sonucu.</summary>
public sealed record SeoFetchResult(
    int StatusCode,
    string ContentType,
    string Body,
    long ElapsedMs,
    string Location,
    string Error);

/// <summary>Taramanın toplu sonucu.</summary>
public sealed class SeoCrawlResult
{
    public List<SeoPageDto> Pages { get; } = [];

    /// <summary>Kuyrukta gezilmemiş sayfa kalarak durulduysa true — rapor SİTENİN TAMAMI DEĞİLDİR.</summary>
    public bool LimitReached { get; set; }

    /// <summary>
    /// Tarama iptal/süre sınırı ile YARIDA kesildiyse true. O ana kadarki sayfalar korunur:
    /// yarım bir ölçüm, hiç ölçüm olmamasından iyidir — yeter ki yarım olduğu SÖYLENSİN.
    /// </summary>
    public bool Interrupted { get; set; }

    /// <summary>Görülen ama İSTEK ATILMAYAN dış bağlantılar (bkz. <see cref="SeoLinkPolicy"/>).</summary>
    public HashSet<string> ExternalLinks { get; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Sitenin kendi sayfalarını gezen tarayıcı.
/// </summary>
/// <remarks>
/// <para>
/// <b>Kaynak sınırları burada UYGULANIR</b> (bkz. <see cref="SeoCrawlLimits"/>): sayfa sayısı,
/// eşzamanlılık, istek süresi, toplam süre ve sayfa başına bayt. Sınırsız bir tarayıcı, panelin
/// müşterinin sitesine karşı kullandığı bir yük aracı olurdu.
/// </para>
/// <para>
/// <b>Yönlendirmeler ELLE izlenir.</b> Sebep güvenlik: <c>HttpClient</c>'ın otomatik izlemesi
/// <c>Location</c> başlığını sorgusuz takip eder ve <c>Location: http://169.254.169.254/</c> diyen
/// bir sayfa, bağlantı kapısını atlardı. Elle izleyince her atlama yeniden sınıflandırılır. İkinci
/// fayda ölçüm: zincirin kaç adım olduğu ancak adımları sayarak bilinir.
/// </para>
/// </remarks>
public sealed class SeoCrawler(HttpClient client, SeoCrawlLimits limits)
{
    /// <summary>En fazla kaç yönlendirme izlenir (zincir bundan uzunsa zaten bulgu üretilir).</summary>
    public const int MaxRedirects = 3;

    /// <summary>Kuyruğun üst sınırı: sayfa sınırının katı kadar. Devasa siteler belleği doldurmasın.</summary>
    private const int QueueCapacityFactor = 20;

    public async Task<SeoCrawlResult> CrawlAsync(
        SeoCrawlTarget target,
        string startPath,
        Action<int>? progress,
        CancellationToken cancellationToken)
    {
        var result = new SeoCrawlResult();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<(string Path, string From)>();

        visited.Add(startPath);
        queue.Enqueue((startPath, string.Empty));

        try
        {
            await CrawlLoopAsync(target, queue, visited, result, progress, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // İptal (kullanıcı ya da toplam süre sınırı) taramayı BİTİRİR ama o ana kadarki
            // ölçümü ÇÖPE ATMAZ; rapor "yarıda kesildi" olarak kaydedilir.
            result.Interrupted = true;
        }

        result.LimitReached = queue.Count > 0 && !result.Interrupted;
        return result;
    }

    private async Task CrawlLoopAsync(
        SeoCrawlTarget target,
        Queue<(string Path, string From)> queue,
        HashSet<string> visited,
        SeoCrawlResult result,
        Action<int>? progress,
        CancellationToken cancellationToken)
    {
        while (queue.Count > 0 && result.Pages.Count < limits.MaxPages)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // DALGA DALGA: kuyruktan en fazla "eşzamanlılık" kadar sayfa alınır, birlikte indirilir,
            // sonra bulunan bağlantılar kuyruğa eklenir. Böylece eşzamanlılık sınırı ayrı bir
            // semafor gerektirmeden ve sayfa sınırı aşılmadan uygulanır.
            var wave = new List<(string Path, string From)>();
            while (wave.Count < limits.MaxConcurrency
                && queue.Count > 0
                && result.Pages.Count + wave.Count < limits.MaxPages)
            {
                wave.Add(queue.Dequeue());
            }

            var loaded = await Task.WhenAll(wave.Select(item => LoadPageAsync(target, item.Path, item.From, cancellationToken)));

            foreach (var outcome in loaded)
            {
                result.Pages.Add(outcome.Page);
                progress?.Invoke(result.Pages.Count);

                foreach (var href in outcome.Links)
                {
                    var decision = SeoLinkPolicy.Classify(href, outcome.Page.Path, target);
                    if (decision.Kind == SeoLinkKind.External)
                    {
                        if (result.ExternalLinks.Count < 200)
                        {
                            result.ExternalLinks.Add(decision.Value);
                        }

                        continue;
                    }

                    if (decision.Kind != SeoLinkKind.Internal || !visited.Add(decision.Value))
                    {
                        continue;
                    }

                    if (queue.Count < limits.MaxPages * QueueCapacityFactor)
                    {
                        queue.Enqueue((decision.Value, outcome.Page.Path));
                    }
                }
            }
        }
    }

    /// <summary>
    /// Tek bir yolu indirir; yönlendirme zincirini elle izler ve sayfa ölçümünü üretir.
    /// </summary>
    private async Task<PageOutcome> LoadPageAsync(
        SeoCrawlTarget target,
        string path,
        string linkedFrom,
        CancellationToken cancellationToken)
    {
        var currentPath = path;
        var redirectDepth = 0;
        var redirectTo = string.Empty;
        long totalMs = 0;

        while (true)
        {
            var fetch = await FetchAsync(target, currentPath, cancellationToken);
            totalMs += fetch.ElapsedMs;

            if (fetch.Error.Length > 0)
            {
                return new PageOutcome(
                    new SeoPageDto
                    {
                        Path = path,
                        LinkedFrom = linkedFrom,
                        ResponseMs = totalMs,
                        RedirectDepth = redirectDepth,
                        RedirectTo = redirectTo,
                        Error = fetch.Error
                    },
                    []);
            }

            var yonlendirme = fetch.StatusCode is >= 300 and < 400 && fetch.Location.Length > 0;
            if (yonlendirme && redirectDepth < MaxRedirects)
            {
                var decision = SeoLinkPolicy.Classify(fetch.Location, currentPath, target);
                if (decision.Kind == SeoLinkKind.Internal)
                {
                    redirectDepth++;
                    redirectTo = decision.Value;
                    currentPath = decision.Value;
                    continue;
                }

                // Site DIŞINA yönlendiriyor: izlemek SSRF kapısını açardı; zincir burada biter ve
                // hedef rapora ham hâliyle yazılır.
                redirectTo = fetch.Location;
            }
            else if (yonlendirme)
            {
                redirectTo = fetch.Location;
            }

            var parse = SeoHtmlAnalyzer.Parse(fetch.Body);
            var page = new SeoPageDto
            {
                Path = path,
                LinkedFrom = linkedFrom,
                StatusCode = fetch.StatusCode,
                ContentType = fetch.ContentType,
                ResponseMs = totalMs,
                RedirectDepth = redirectDepth,
                RedirectTo = redirectTo,
                Title = parse.Title,
                MetaDescription = parse.MetaDescription,
                H1Count = parse.H1Count,
                ImageCount = parse.ImageCount,
                ImagesWithoutAlt = parse.ImagesWithoutAlt,
                NoIndex = parse.NoIndex
            };

            // Bağlantılar yalnız BAŞARILI HTML sayfalarından toplanır: hata sayfasının menüsü
            // sitenin gerçek bağlantı yapısı değildir ve 404 sayfası her seferinde aynı bağlantıları
            // üreterek taramayı gereksiz uzatırdı.
            var links = fetch.StatusCode is >= 200 and < 300 ? parse.Links : [];
            return new PageOutcome(page, links);
        }
    }

    /// <summary>
    /// Tek istek. Adres <see cref="SeoLinkPolicy.TryBuildRequestUri"/> ile kurulur; kurulamıyorsa
    /// istek HİÇ atılmaz.
    /// </summary>
    public async Task<SeoFetchResult> FetchAsync(SeoCrawlTarget target, string path, CancellationToken cancellationToken)
    {
        if (!SeoLinkPolicy.TryBuildRequestUri(target, path, out var uri) || uri is null)
        {
            return new SeoFetchResult(0, string.Empty, string.Empty, 0, string.Empty, "blocked_by_policy");
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(limits.RequestTimeout);

            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            // Sunucu tarafındaki günlüklerde bu isteklerin panelden geldiği ANLAŞILMALI: site
            // sahibi kendi istatistiklerinde "bu trafik nereden geliyor" diye sormamalı.
            request.Headers.TryAddWithoutValidation("User-Agent", "AdaPanel-SEO/1.0 (+panel site audit)");

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);

            var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            var location = response.Headers.Location?.OriginalString ?? string.Empty;
            var body = await ReadBodyAsync(response, timeout.Token);

            stopwatch.Stop();
            return new SeoFetchResult((int)response.StatusCode, contentType, body, stopwatch.ElapsedMilliseconds, location, string.Empty);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Taramanın TAMAMI iptal edildi (kullanıcı ya da toplam süre) → yukarı çık.
            throw;
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            // Tek sayfanın hatası taramayı DÜŞÜRMEZ; sayfa "ölçülemedi" olarak raporlanır.
            return new SeoFetchResult(
                0,
                string.Empty,
                string.Empty,
                stopwatch.ElapsedMilliseconds,
                string.Empty,
                $"{exception.GetType().Name}: {exception.Message}");
        }
    }

    /// <summary>
    /// Gövdeyi ÜST SINIRA kadar okur. Sınırsız okuma, tek bir dev dosyayla panelin belleğini
    /// tüketmenin en kolay yoludur; sınır aşılınca okuma kesilir ve eldeki parça ayrıştırılır
    /// (başlık/açıklama zaten <c>&lt;head&gt;</c> içindedir).
    /// </summary>
    private async Task<string> ReadBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];

        while (buffer.Length < limits.MaxResponseBytes)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken);
            if (read <= 0)
            {
                break;
            }

            buffer.Write(chunk, 0, Math.Min(read, limits.MaxResponseBytes - (int)buffer.Length));
        }

        return Decode(buffer.ToArray(), response.Content.Headers.ContentType?.CharSet);
    }

    /// <summary>
    /// Bayt dizisini metne çevirir. Bilinmeyen/desteklenmeyen kod sayfasında UTF-8'e düşülür:
    /// çözümlenemeyen bir yanıt yüzünden tüm taramayı düşürmek, ölçümü hiç yapmamak olurdu.
    /// </summary>
    internal static string Decode(byte[] bytes, string? charset)
    {
        if (bytes.Length == 0)
        {
            return string.Empty;
        }

        var name = (charset ?? string.Empty).Trim().Trim('"');
        if (name.Length > 0)
        {
            try
            {
                return Encoding.GetEncoding(name).GetString(bytes);
            }
            catch (ArgumentException)
            {
                // .NET Core'da Windows kod sayfaları varsayılan olarak kayıtlı değildir.
            }
        }

        return Encoding.UTF8.GetString(bytes);
    }

    private sealed record PageOutcome(SeoPageDto Page, IReadOnlyList<string> Links);
}
