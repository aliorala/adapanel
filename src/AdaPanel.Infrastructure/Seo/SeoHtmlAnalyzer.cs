using System.Net;
using System.Text.RegularExpressions;

namespace AdaPanel.Infrastructure.Seo;

/// <summary>Tek bir HTML sayfasından çıkarılan ham ölçüm.</summary>
public sealed record SeoPageParse(
    string Title,
    int TitleTagCount,
    string MetaDescription,
    int H1Count,
    int ImageCount,
    int ImagesWithoutAlt,
    bool NoIndex,
    string Canonical,
    string BaseHref,
    IReadOnlyList<string> Links);

/// <summary>
/// HTML'den SEO ölçümlerini çıkarır. SAF: ağ, disk ve zaman yok — bu yüzden tamamı testlerle
/// sabitlenebilir.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden düzenli ifade, neden tam bir HTML ayrıştırıcı değil:</b> panelin hiç dış bağımlılığı
/// yok ve bir HTML ayrıştırıcı paketi (AngleSharp/HtmlAgilityPack) eklemek, kurulum paketine
/// yalnız başlık/açıklama okumak için yeni bir bağımlılık sokardı. Aranan şey birkaç etiket ve
/// öznitelik; bunun için tam ayrıştırma gerekmiyor.
/// </para>
/// <para>
/// <b>SINIRI (dürüstlük):</b> yorum satırları ile <c>script</c>/<c>style</c> gövdeleri önce
/// atılır (aksi hâlde JavaScript içindeki dizeler "bağlantı" sanılırdı), ama bu yine de bir
/// tarayıcı motoru değildir: JavaScript ile ÜRETİLEN içerik ve bağlantılar GÖRÜLMEZ. Tek sayfalık
/// uygulamalarda (SPA) rapor bu yüzden eksik çıkar; bu, aracın bilinen sınırıdır.
/// </para>
/// </remarks>
public static partial class SeoHtmlAnalyzer
{
    /// <summary>Başlık/açıklama için üst uzunluk — çöp veriyi rapora taşımamak için kırpılır.</summary>
    private const int MaxTextLength = 300;

    public static SeoPageParse Parse(string? html)
    {
        var content = html ?? string.Empty;
        if (content.Length == 0)
        {
            return new SeoPageParse(string.Empty, 0, string.Empty, 0, 0, 0, false, string.Empty, string.Empty, []);
        }

        // Yorumlar ve betik/biçem gövdeleri ölçümün DIŞINDA: içlerindeki "<a href=..." metni
        // sayfada gerçekten var olan bir bağlantı değildir.
        var temiz = ScriptOrStyle().Replace(Comment().Replace(content, " "), " ");

        var titles = TitleTag().Matches(temiz);
        var title = titles.Count > 0 ? Normalize(titles[0].Groups[1].Value) : string.Empty;

        var description = string.Empty;
        var noIndex = false;
        foreach (Match meta in MetaTag().Matches(temiz))
        {
            var attributes = ReadAttributes(meta.Value);
            if (!attributes.TryGetValue("name", out var name))
            {
                continue;
            }

            attributes.TryGetValue("content", out var value);
            if (description.Length == 0 && string.Equals(name, "description", StringComparison.OrdinalIgnoreCase))
            {
                description = Normalize(value ?? string.Empty);
            }
            else if (string.Equals(name, "robots", StringComparison.OrdinalIgnoreCase)
                && (value ?? string.Empty).Contains("noindex", StringComparison.OrdinalIgnoreCase))
            {
                noIndex = true;
            }
        }

        var canonical = string.Empty;
        foreach (Match link in LinkTag().Matches(temiz))
        {
            var attributes = ReadAttributes(link.Value);
            if (attributes.TryGetValue("rel", out var rel)
                && string.Equals(rel.Trim(), "canonical", StringComparison.OrdinalIgnoreCase)
                && attributes.TryGetValue("href", out var href))
            {
                canonical = Normalize(href);
                break;
            }
        }

        var baseHref = string.Empty;
        var baseMatch = BaseTag().Match(temiz);
        if (baseMatch.Success && ReadAttributes(baseMatch.Value).TryGetValue("href", out var baseValue))
        {
            baseHref = baseValue.Trim();
        }

        var imageCount = 0;
        var imagesWithoutAlt = 0;
        foreach (Match image in ImageTag().Matches(temiz))
        {
            imageCount++;
            var attributes = ReadAttributes(image.Value);
            // "alt yok" ile "alt=''" AYNI sayılır: ikisi de ekran okuyucuya ve arama motoruna
            // hiçbir şey söylemez. (Bilinçli boş alt yalnız dekoratif görsellerde doğrudur;
            // bu yüzden bulgunun önem derecesi "uyarı", "hata" değil.)
            if (!attributes.TryGetValue("alt", out var alt) || alt.Trim().Length == 0)
            {
                imagesWithoutAlt++;
            }
        }

        var links = new List<string>();
        foreach (Match anchor in AnchorTag().Matches(temiz))
        {
            if (ReadAttributes(anchor.Value).TryGetValue("href", out var href) && href.Trim().Length > 0)
            {
                links.Add(WebUtility.HtmlDecode(href.Trim()));
            }
        }

        return new SeoPageParse(
            title,
            titles.Count,
            description,
            H1Tag().Matches(temiz).Count,
            imageCount,
            imagesWithoutAlt,
            noIndex,
            canonical,
            baseHref,
            links);
    }

    /// <summary>Etiket metnindeki öznitelikleri okur (tırnaklı ya da tırnaksız).</summary>
    public static Dictionary<string, string> ReadAttributes(string tag)
    {
        var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in Attribute().Matches(tag))
        {
            var name = match.Groups[1].Value;
            if (attributes.ContainsKey(name))
            {
                continue;
            }

            var value = match.Groups[3].Success ? match.Groups[3].Value
                : match.Groups[4].Success ? match.Groups[4].Value
                : match.Groups[5].Value;

            attributes[name] = WebUtility.HtmlDecode(value);
        }

        return attributes;
    }

    /// <summary>HTML varlıklarını çözer, boşlukları tekler ve kırpar.</summary>
    private static string Normalize(string value)
    {
        var text = WebUtility.HtmlDecode(value ?? string.Empty);
        text = Whitespace().Replace(text, " ").Trim();
        return text.Length <= MaxTextLength ? text : text[..MaxTextLength];
    }

    [GeneratedRegex("<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex Comment();

    [GeneratedRegex("<(script|style)\\b[^>]*>.*?</\\1\\s*>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ScriptOrStyle();

    [GeneratedRegex("<title\\b[^>]*>(.*?)</title\\s*>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex TitleTag();

    [GeneratedRegex("<meta\\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex MetaTag();

    [GeneratedRegex("<link\\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex LinkTag();

    [GeneratedRegex("<base\\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex BaseTag();

    [GeneratedRegex("<img\\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex ImageTag();

    [GeneratedRegex("<a\\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex AnchorTag();

    [GeneratedRegex("<h1\\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex H1Tag();

    [GeneratedRegex("([a-zA-Z_:][-a-zA-Z0-9_:.]*)\\s*=\\s*(\"([^\"]*)\"|'([^']*)'|([^\\s\"'>]+))")]
    private static partial Regex Attribute();

    [GeneratedRegex("\\s+")]
    private static partial Regex Whitespace();
}
