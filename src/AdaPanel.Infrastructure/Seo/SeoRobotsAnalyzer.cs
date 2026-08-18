using System.Net;
using System.Text.RegularExpressions;

namespace AdaPanel.Infrastructure.Seo;

/// <param name="DisallowsAll">Tüm robotlara kök yasağı var mı.</param>
/// <param name="SitemapUrls"><c>Sitemap:</c> satırlarındaki adresler.</param>
public sealed record SeoRobotsParse(bool DisallowsAll, IReadOnlyList<string> SitemapUrls);

/// <summary>
/// <c>robots.txt</c> ve <c>sitemap.xml</c> içeriğini okur. SAF: ağ yok.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden en çok "hepsini yasaklıyor" aranıyor:</b> panelde barındırılan sitelerde en pahalı ve
/// en sinsi hata budur. Geliştirme sırasında konan <c>Disallow: /</c> yayına çıkarken unutulur;
/// site çalışır, güzel görünür ve arama motorlarında HİÇ görünmez. Kullanıcı bunu genellikle
/// haftalar sonra fark eder.
/// </para>
/// <para>
/// <b>SINIRI:</b> tam bir robots.txt yorumlayıcısı değildir (yol kalıpları, <c>Allow</c>
/// önceliği, robot bazlı kurallar uygulanmaz). Yalnız "site tümüyle kapalı mı" sorusu ve
/// sitemap bildirimleri okunur.
/// </para>
/// </remarks>
public static partial class SeoRobotsAnalyzer
{
    /// <summary>Bir sitemap'ten okunacak en fazla adres — bozuk/dev bir dosya belleği doldurmasın.</summary>
    public const int MaxSitemapUrls = 5000;

    public static SeoRobotsParse ParseRobots(string? text)
    {
        var sitemaps = new List<string>();
        var yildizGrubunda = false;
        var hepsiYasak = false;
        var yildizGrubundaIzin = false;

        foreach (var rawLine in (text ?? string.Empty).Split('\n'))
        {
            var line = rawLine;
            var comment = line.IndexOf('#');
            if (comment >= 0)
            {
                line = line[..comment];
            }

            line = line.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var separator = line.IndexOf(':');
            if (separator <= 0)
            {
                continue;
            }

            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();

            if (string.Equals(key, "sitemap", StringComparison.OrdinalIgnoreCase))
            {
                if (value.Length > 0 && sitemaps.Count < 20)
                {
                    sitemaps.Add(value);
                }

                continue;
            }

            if (string.Equals(key, "user-agent", StringComparison.OrdinalIgnoreCase))
            {
                yildizGrubunda = value == "*";
                continue;
            }

            if (!yildizGrubunda)
            {
                continue;
            }

            if (string.Equals(key, "disallow", StringComparison.OrdinalIgnoreCase) && value == "/")
            {
                hepsiYasak = true;
            }
            else if (string.Equals(key, "allow", StringComparison.OrdinalIgnoreCase) && value.Length > 0)
            {
                // "Disallow: /" + "Allow: /blog" kalıbı siteyi tümüyle kapatmaz; bunu "kapalı"
                // diye raporlamak yanlış alarm olurdu.
                yildizGrubundaIzin = true;
            }
        }

        return new SeoRobotsParse(hepsiYasak && !yildizGrubundaIzin, sitemaps);
    }

    /// <summary>
    /// Sitemap XML'inden <c>&lt;loc&gt;</c> adreslerini çıkarır (sitemap index dosyaları da aynı
    /// etiketi kullanır).
    /// </summary>
    /// <remarks>
    /// XML ayrıştırıcı yerine desen kullanılır: harici varlık (XXE) ve varlık genişlemesi gibi
    /// XML'e özgü saldırı yüzeyini hiç açmamak, ad alanı farklarına takılmamak için.
    /// </remarks>
    public static IReadOnlyList<string> ParseSitemapLocations(string? xml)
    {
        var locations = new List<string>();
        foreach (Match match in Location().Matches(xml ?? string.Empty))
        {
            if (locations.Count >= MaxSitemapUrls)
            {
                break;
            }

            var value = WebUtility.HtmlDecode(match.Groups[1].Value).Trim();
            if (value.Length > 0)
            {
                locations.Add(value);
            }
        }

        return locations;
    }

    [GeneratedRegex("<loc[^>]*>(.*?)</loc\\s*>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex Location();
}
