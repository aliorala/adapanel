using AdaPanel.Core.Dtos.Seo;
using AdaPanel.Infrastructure.Seo;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// SEO denetiminin KARAR katmanı: HTML'den ne okunduğu, neyin sorun sayıldığı, robots/sitemap'in
/// nasıl yorumlandığı ve iki taramanın nasıl karşılaştırıldığı.
/// </summary>
/// <remarks>
/// Bu katmanın tamamı saf fonksiyonlardır; ağ olmadan sınanabilir olması bilinçli bir tasarım
/// kararıdır (eşik değerleri ancak böyle tek tek doğrulanabilir).
/// </remarks>
public sealed class SeoAuditRulesTests
{
    private static SeoPageDto Page(
        string path = "/",
        string title = "Baslik",
        string description = "Aciklama",
        int h1 = 1,
        int images = 0,
        int imagesWithoutAlt = 0,
        long ms = 100,
        int status = 200,
        bool noIndex = false,
        int redirectDepth = 0) => new()
        {
            Path = path,
            Title = title,
            MetaDescription = description,
            H1Count = h1,
            ImageCount = images,
            ImagesWithoutAlt = imagesWithoutAlt,
            ResponseMs = ms,
            StatusCode = status,
            ContentType = "text/html",
            NoIndex = noIndex,
            RedirectDepth = redirectDepth
        };

    private static SeoRobotsDto Robots() => new() { Found = true, SitemapUrls = ["https://ornek.test/sitemap.xml"] };

    private static SeoSitemapDto Sitemap() => new() { Found = true, Path = "/sitemap.xml", UrlCount = 3 };

    private static IReadOnlyList<SeoFindingDto> Degerlendir(params SeoPageDto[] pages)
        => SeoFindingRules.Evaluate(pages, Robots(), Sitemap(), false);

    // --- HTML okuma ---

    [Fact]
    public void HTML_olcumleri_okunur()
    {
        const string html = """
            <html><head>
              <title>  Ana   sayfa </title>
              <meta name="description" content="Kisa &amp; oz aciklama">
              <link rel="canonical" href="https://ornek.test/">
            </head><body>
              <h1>Baslik</h1><h1>Ikinci</h1>
              <img src="a.png" alt="anlamli">
              <img src="b.png">
              <img src="c.png" alt="  ">
              <a href="/bir">bir</a><a href='/iki'>iki</a>
            </body></html>
            """;

        var olcum = SeoHtmlAnalyzer.Parse(html);

        Assert.Equal("Ana sayfa", olcum.Title);                  // boşluklar teklenir, kırpılır
        Assert.Equal("Kisa & oz aciklama", olcum.MetaDescription); // HTML varlıkları çözülür
        Assert.Equal(2, olcum.H1Count);
        Assert.Equal(3, olcum.ImageCount);
        Assert.Equal(2, olcum.ImagesWithoutAlt);                 // eksik alt + boş alt
        Assert.Equal(["/bir", "/iki"], olcum.Links);
        Assert.False(olcum.NoIndex);
        Assert.Equal("https://ornek.test/", olcum.Canonical);
    }

    /// <summary>
    /// Betik gövdesindeki ve yorum içindeki metin sayfa içeriği DEĞİLDİR; bağlantı sayılırsa
    /// tarayıcı var olmayan sayfaları gezmeye çalışır.
    /// </summary>
    [Fact]
    public void Betik_ve_yorum_icerigi_olcume_girmez()
    {
        const string html = """
            <html><head><title>t</title></head><body>
              <script>var s = '<a href="/betikten">x</a>';</script>
              <!-- <a href="/yorumdan">y</a> -->
              <a href="/gercek">g</a>
            </body></html>
            """;

        var olcum = SeoHtmlAnalyzer.Parse(html);

        Assert.Equal(["/gercek"], olcum.Links);
    }

    [Theory]
    [InlineData("<meta name=\"robots\" content=\"noindex, nofollow\">", true)]
    [InlineData("<meta content=\"NOINDEX\" name=\"ROBOTS\">", true)]
    [InlineData("<meta name=\"robots\" content=\"index, follow\">", false)]
    public void Noindex_okunur(string meta, bool beklenen)
    {
        Assert.Equal(beklenen, SeoHtmlAnalyzer.Parse($"<html><head>{meta}</head></html>").NoIndex);
    }

    // --- Kurallar ---

    [Fact]
    public void BASLIK_YOKSA_hata_uretilir()
    {
        var bulgu = Assert.Single(Degerlendir(Page(title: "")), finding => finding.Key == "missing_title");
        Assert.Equal("error", bulgu.Severity);
    }

    [Fact]
    public void ACIKLAMA_ve_H1_yoksa_uyari_uretilir()
    {
        var bulgular = Degerlendir(Page(description: string.Empty, h1: 0));

        Assert.Contains(bulgular, finding => finding.Key == "missing_description" && finding.Severity == "warn");
        Assert.Contains(bulgular, finding => finding.Key == "missing_h1" && finding.Severity == "warn");
    }

    [Fact]
    public void AYNI_BASLIKLI_sayfalar_kopya_sayilir()
    {
        var bulgular = Degerlendir(
            Page(path: "/a", title: "Ayni"),
            Page(path: "/b", title: "Ayni"),
            Page(path: "/c", title: "Farkli"));

        var bulgu = Assert.Single(bulgular, finding => finding.Key == "duplicate_title");
        Assert.Contains("/a", bulgu.Detail, StringComparison.Ordinal);
        Assert.Contains("/b", bulgu.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("/c", bulgu.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void ALT_METNI_OLMAYAN_gorsel_bulunur()
    {
        var bulgu = Assert.Single(Degerlendir(Page(images: 8, imagesWithoutAlt: 3)), finding => finding.Key == "image_without_alt");
        Assert.Equal("3/8", bulgu.Detail);
    }

    [Fact]
    public void YAVAS_SAYFA_esik_ustunde_bulunur()
    {
        Assert.DoesNotContain(Degerlendir(Page(ms: SeoFindingRules.SlowPageMs)), finding => finding.Key == "slow_page");
        Assert.Contains(Degerlendir(Page(ms: SeoFindingRules.SlowPageMs + 1)), finding => finding.Key == "slow_page");
    }

    [Fact]
    public void YONLENDIRME_ZINCIRI_bulunur()
    {
        Assert.DoesNotContain(Degerlendir(Page(redirectDepth: 1)), finding => finding.Key == "redirect_chain");
        Assert.Contains(Degerlendir(Page(redirectDepth: 2)), finding => finding.Key == "redirect_chain");
    }

    [Fact]
    public void OLCULEMEYEN_sayfa_SORUNSUZ_sayilmaz()
    {
        var sayfa = new SeoPageDto { Path = "/x", Error = "TaskCanceledException: zaman asimi" };

        var bulgu = Assert.Single(SeoFindingRules.Evaluate([sayfa], Robots(), Sitemap(), false), finding => finding.Key == "unreachable_page");
        Assert.Equal("error", bulgu.Severity);
    }

    /// <summary>
    /// Sınıra dayanan tarama, sitenin TAMAMI değildir. Bunu söylememek raporun üretebileceği en
    /// zararlı yalan olurdu: kullanıcı hiç bakılmamış sayfaları sorunsuz sanardı.
    /// </summary>
    [Fact]
    public void SINIRA_DAYANAN_tarama_bunu_SOYLER()
    {
        Assert.Contains(
            SeoFindingRules.Evaluate([Page()], Robots(), Sitemap(), true),
            finding => finding.Key == "crawl_limit_reached");
    }

    [Fact]
    public void BULGU_LISTESI_kirpilirsa_bu_da_SOYLENIR()
    {
        var sayfalar = Enumerable.Range(0, SeoFindingRules.MaxFindings + 50)
            .Select(i => Page(path: $"/s{i}", title: string.Empty, description: string.Empty))
            .ToArray();

        var bulgular = SeoFindingRules.Evaluate(sayfalar, Robots(), Sitemap(), false);

        Assert.Equal(SeoFindingRules.MaxFindings + 1, bulgular.Count);
        Assert.Contains(bulgular, finding => finding.Key == "findings_truncated");
    }

    // --- robots.txt ---

    [Fact]
    public void ROBOTS_hepsini_yasakliyorsa_HATA_uretilir()
    {
        var parse = SeoRobotsAnalyzer.ParseRobots("User-agent: *\nDisallow: /");
        Assert.True(parse.DisallowsAll);

        var robots = new SeoRobotsDto { Found = true, DisallowsAll = true, SitemapUrls = ["/sitemap.xml"] };
        var bulgu = Assert.Single(
            SeoFindingRules.Evaluate([Page()], robots, Sitemap(), false),
            finding => finding.Key == "robots_blocks_all");

        Assert.Equal("error", bulgu.Severity);
    }

    /// <summary>
    /// <c>Disallow: /</c> + <c>Allow: /blog</c> siteyi TÜMÜYLE kapatmaz; bunu "kapalı" diye
    /// raporlamak yanlış alarm olurdu.
    /// </summary>
    [Fact]
    public void ALLOW_ile_kisitlanan_yasak_TUM_SITEYI_kapatmaz()
    {
        Assert.False(SeoRobotsAnalyzer.ParseRobots("User-agent: *\nDisallow: /\nAllow: /blog").DisallowsAll);
    }

    /// <summary>Yasak BAŞKA bir robota aitse site herkese kapalı değildir.</summary>
    [Fact]
    public void BASKA_ROBOTUN_yasagi_tum_siteyi_kapatmaz()
    {
        Assert.False(SeoRobotsAnalyzer.ParseRobots("User-agent: BadBot\nDisallow: /").DisallowsAll);
    }

    [Fact]
    public void ROBOTS_sitemap_bildirimleri_okunur()
    {
        var parse = SeoRobotsAnalyzer.ParseRobots("""
            # yorum
            User-agent: *
            Disallow: /gizli
            Sitemap: https://ornek.test/sitemap.xml
            sitemap: https://ornek.test/haber-sitemap.xml
            """);

        Assert.False(parse.DisallowsAll);
        Assert.Equal(2, parse.SitemapUrls.Count);
    }

    [Fact]
    public void ROBOTS_yoksa_uyari_uretilir()
    {
        var bulgu = Assert.Single(
            SeoFindingRules.Evaluate([Page()], new SeoRobotsDto { StatusCode = 404 }, Sitemap(), false),
            finding => finding.Key == "robots_missing");

        Assert.Equal("warn", bulgu.Severity);
    }

    // --- sitemap.xml ---

    [Fact]
    public void SITEMAP_adresleri_okunur()
    {
        var adresler = SeoRobotsAnalyzer.ParseSitemapLocations("""
            <?xml version="1.0" encoding="UTF-8"?>
            <urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
              <url><loc>https://ornek.test/</loc><lastmod>2026-01-01</lastmod></url>
              <url><loc>https://ornek.test/blog?a=1&amp;b=2</loc></url>
            </urlset>
            """);

        Assert.Equal(2, adresler.Count);
        Assert.Equal("https://ornek.test/blog?a=1&b=2", adresler[1]);
    }

    [Fact]
    public void SITEMAPTE_ACILMAYAN_adres_uyari_uretir()
    {
        var sitemap = new SeoSitemapDto
        {
            Found = true,
            Path = "/sitemap.xml",
            UrlCount = 2,
            CheckedCount = 2,
            UnreachableUrls = ["/eski: HTTP 404"]
        };

        var bulgu = Assert.Single(
            SeoFindingRules.Evaluate([Page()], Robots(), sitemap, false),
            finding => finding.Key == "sitemap_unreachable_url");

        Assert.Contains("404", bulgu.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void SITEMAP_yoksa_uyari_uretilir()
    {
        Assert.Contains(
            SeoFindingRules.Evaluate([Page()], Robots(), new SeoSitemapDto { Path = "/sitemap.xml", StatusCode = 404 }, false),
            finding => finding.Key == "sitemap_missing");
    }

    // --- Geçmişle karşılaştırma ---

    [Fact]
    public void GECMISLE_KARSILASTIRMA_yeni_ve_cozulen_bulgulari_sayar()
    {
        SeoFindingDto Bulgu(string key, string url) => new() { Key = key, Url = url, Severity = "warn" };

        var onceki = new[] { Bulgu("missing_title", "/a"), Bulgu("missing_h1", "/b") };
        var simdiki = new[] { Bulgu("missing_h1", "/b"), Bulgu("slow_page", "/c") };

        var (yeni, cozulen) = SeoScanRunner.Compare(onceki, simdiki);

        Assert.Equal(1, yeni);      // slow_page /c
        Assert.Equal(1, cozulen);   // missing_title /a
    }

    /// <summary>
    /// İLK taramada karşılaştırma YAPILMAZ. Aksi hâlde her bulgu "yeni" sayılır ve kullanıcı,
    /// dün ortaya çıkmış gibi görünen bir listeyle karşılaşırdı.
    /// </summary>
    [Fact]
    public void ILK_TARAMADA_karsilastirma_yapilmaz()
    {
        var (yeni, cozulen) = SeoScanRunner.Compare(null, [new SeoFindingDto { Key = "missing_title", Url = "/" }]);

        Assert.Equal(0, yeni);
        Assert.Equal(0, cozulen);
    }

    [Fact]
    public void OZET_bulgulari_onem_derecesine_gore_sayar()
    {
        var rapor = new SeoScanReportDto
        {
            Id = "1",
            Status = "completed",
            PagesCrawled = 3,
            Findings =
            [
                new SeoFindingDto { Key = "missing_title", Severity = "error" },
                new SeoFindingDto { Key = "missing_h1", Severity = "warn" },
                new SeoFindingDto { Key = "noindex", Severity = "info" },
                new SeoFindingDto { Key = "broken_link", Severity = "error" }
            ]
        };

        var ozet = SeoScanService.Summarize(rapor);

        Assert.Equal(2, ozet.ErrorCount);
        Assert.Equal(1, ozet.WarningCount);
        Assert.Equal(1, ozet.InfoCount);
    }
}
