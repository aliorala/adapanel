using AdaPanel.Core.Dtos.Seo;

namespace AdaPanel.Infrastructure.Seo;

/// <summary>
/// Ham ölçümü BULGUYA çeviren kurallar. SAF: girdi sayfa listesi, çıktı bulgu listesi — ağ, disk,
/// saat yok.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden ayrı sınıf:</b> "hangi sayfaya istek attık" ile "bu ölçüm bir sorun mu" iki ayrı
/// sorudur. Kuralları crawler'ın içine yazmak, eşik değerlerini (kaç karakter uzun, kaç ms yavaş)
/// ağ kodu çalıştırmadan test edilemez hâle getirirdi.
/// </para>
/// <para>
/// <b>Önem dereceleri neden böyle:</b> <c>error</c> yalnız sitenin arama motorunda GÖRÜNMESİNİ
/// engelleyen ya da kullanıcıyı boş sayfaya götüren şeyler içindir (başlık yok, kırık bağlantı,
/// robots.txt her şeyi yasaklamış). Her eksiği kırmızıya boyamak, listeyi kimsenin okumadığı bir
/// duvara çevirir.
/// </para>
/// </remarks>
public static class SeoFindingRules
{
    public const int TitleMaxLength = 60;

    public const int DescriptionMaxLength = 160;

    /// <summary>Local response review threshold; not a Core Web Vitals measurement.</summary>
    public const int SlowPageMs = 1500;

    /// <summary>Kaç atlamadan sonra "yönlendirme zinciri" sayılır (1 atlama normaldir).</summary>
    public const int RedirectChainDepth = 2;

    /// <summary>Rapordaki bulgu üst sınırı; aşılırsa liste kırpılır ve bu AÇIKÇA söylenir.</summary>
    public const int MaxFindings = 300;

    public static IReadOnlyList<SeoFindingDto> Evaluate(
        IReadOnlyList<SeoPageDto> pages,
        SeoRobotsDto robots,
        SeoSitemapDto sitemap,
        bool limitReached)
    {
        var findings = new List<SeoFindingDto>();

        foreach (var page in pages)
        {
            if (page.Error.Length > 0)
            {
                findings.Add(New("unreachable_page", "error", page.Path, page.Error));
                continue;
            }

            if (page.StatusCode >= 400)
            {
                // Kırık bağlantı yalnız "404 var" demek değildir; ASIL bilgi HANGİ SAYFANIN
                // bağlantı verdiğidir — düzeltilecek yer orasıdır.
                var kaynak = page.LinkedFrom.Length > 0 ? page.LinkedFrom : "-";
                findings.Add(New("broken_link", "error", page.Path, $"HTTP {page.StatusCode}; {kaynak}"));
                continue;
            }

            if (page.RedirectDepth >= RedirectChainDepth)
            {
                findings.Add(New("redirect_chain", "warn", page.Path, $"{page.RedirectDepth} → {page.RedirectTo}"));
            }

            if (!IsHtml(page) || page.StatusCode is < 200 or >= 300)
            {
                continue;
            }

            findings.AddRange(SeoContentFindingRules.Evaluate(page));
            if (page.ContentSignals?.BodyTruncated == true) continue;

            if (page.Title.Length == 0)
            {
                findings.Add(New("missing_title", "error", page.Path, string.Empty));
            }
            else if (page.Title.Length > TitleMaxLength)
            {
                findings.Add(New("long_title", "info", page.Path, $"{page.Title.Length} > {TitleMaxLength}"));
            }

            if (page.MetaDescription.Length == 0)
            {
                findings.Add(New("missing_description", "warn", page.Path, string.Empty));
            }
            else if (page.MetaDescription.Length > DescriptionMaxLength)
            {
                findings.Add(New("long_description", "info", page.Path, $"{page.MetaDescription.Length} > {DescriptionMaxLength}"));
            }

            if (page.H1Count == 0)
            {
                findings.Add(New("missing_h1", "warn", page.Path, string.Empty));
            }
            else if (page.H1Count > 1)
            {
                findings.Add(New("multiple_h1", "info", page.Path, page.H1Count.ToString()));
            }

            if (page.ImagesWithoutAlt > 0)
            {
                findings.Add(New("image_without_alt", "warn", page.Path, $"{page.ImagesWithoutAlt}/{page.ImageCount}"));
            }

            if (page.ResponseMs > SlowPageMs)
            {
                findings.Add(New("slow_page", "warn", page.Path, $"{page.ResponseMs} ms"));
            }

            if (page.NoIndex)
            {
                findings.Add(New("noindex", "info", page.Path, string.Empty));
            }
        }

        findings.AddRange(Duplicates(pages, page => page.Title, "duplicate_title"));
        findings.AddRange(Duplicates(pages, page => page.MetaDescription, "duplicate_description"));

        foreach (var bot in robots.BotAccess.Where(bot => bot.Purpose == "search" && bot.Status == "blocked"))
            findings.Add(New("search_bot_blocked", "warn", "/robots.txt", bot.Agent));

        if (!robots.Found)
        {
            findings.Add(New("robots_missing", "warn", "/robots.txt", robots.Error.Length > 0 ? robots.Error : $"HTTP {robots.StatusCode}"));
        }
        else
        {
            if (robots.DisallowsAll)
            {
                findings.Add(New("robots_blocks_all", "error", "/robots.txt", "User-agent: * / Disallow: /"));
            }

            if (robots.SitemapUrls.Count == 0)
            {
                findings.Add(New("sitemap_not_declared", "info", "/robots.txt", string.Empty));
            }
        }

        if (!sitemap.Found)
        {
            findings.Add(New("sitemap_missing", "warn", sitemap.Path, sitemap.Error.Length > 0 ? sitemap.Error : $"HTTP {sitemap.StatusCode}"));
        }
        else
        {
            foreach (var unreachable in sitemap.UnreachableUrls)
            {
                findings.Add(New("sitemap_unreachable_url", "warn", sitemap.Path, unreachable));
            }

            if (sitemap.ExternalUrls.Count > 0)
            {
                findings.Add(New(
                    "sitemap_external_url",
                    "info",
                    sitemap.Path,
                    $"{sitemap.ExternalUrls.Count}: {string.Join(", ", sitemap.ExternalUrls.Take(3))}"));
            }
        }

        if (limitReached)
        {
            // Kırpılmış bir taramayı "temiz" gibi göstermek, raporun söyleyebileceği en zararlı
            // yalan olurdu: kullanıcı hiç bakılmamış sayfaları sorunsuz sanır.
            findings.Add(New("crawl_limit_reached", "info", string.Empty, pages.Count.ToString()));
        }

        if (findings.Count <= MaxFindings)
        {
            return findings;
        }

        var kirpilmis = findings.OrderBy(finding => finding.Severity == "error" ? 0 : finding.Severity == "warn" ? 1 : 2).Take(MaxFindings).ToList();
        kirpilmis.Add(New("findings_truncated", "info", string.Empty, findings.Count.ToString()));
        return kirpilmis;
    }

    /// <summary>
    /// Shared titles/descriptions warrant review; they do not prove duplicate content or exclusion.
    /// </summary>
    private static IEnumerable<SeoFindingDto> Duplicates(
        IReadOnlyList<SeoPageDto> pages,
        Func<SeoPageDto, string> selector,
        string key)
    {
        return pages
            .Where(page => IsHtml(page) && page.StatusCode is >= 200 and < 300 && page.ContentSignals?.BodyTruncated != true && page.Error.Length == 0 && selector(page).Length > 0)
            .GroupBy(selector, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => New(
                key,
                "warn",
                group.First().Path,
                $"{group.Count()}: {string.Join(", ", group.Select(page => page.Path).Take(5))}"));
    }

    /// <summary>
    /// İçerik türü bilinmiyorsa HTML SAYILIR: panelde <c>Content-Type</c> göndermeyen eski
    /// yapılandırmalar var ve bu sayfaları ölçüm dışı bırakmak, sorunları sessizce gizlerdi.
    /// </summary>
    private static bool IsHtml(SeoPageDto page)
        => page.ContentType.Length == 0 || page.ContentType.Contains("html", StringComparison.OrdinalIgnoreCase);

    private static SeoFindingDto New(string key, string severity, string url, string detail)
        => new() { Key = key, Severity = severity, Url = url, Detail = detail };
}
