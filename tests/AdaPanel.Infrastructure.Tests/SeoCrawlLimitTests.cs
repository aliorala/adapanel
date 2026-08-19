using System.Net;
using AdaPanel.Infrastructure.Seo;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// TARAMANIN KAYNAK SINIRLARI: sayfa sayısı, eşzamanlılık, istek süresi ve iptal.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden bu sınırlar bir güvenlik konusu:</b> tarayıcı, taradığı siteyle AYNI sunucuda koşar.
/// Sınırsız bir tarayıcı, panelin müşterinin sitesine (ve kendi sunucusuna) karşı kullandığı bir
/// yük aracı olurdu — üstelik bunu tetiklemek tek bir düğmeye basmak kadar kolaydır.
/// </para>
/// <para>
/// Sınırlar ÖLÇÜLEREK doğrulanır: kaç isteğin atıldığı ve aynı anda kaçının uçuşta olduğu sahte
/// sunucudan okunur (bkz. <see cref="SeoFakeHandler"/>). "Kod böyle yazılmış" bir kanıt değildir.
/// </para>
/// </remarks>
public sealed class SeoCrawlLimitTests
{
    private static SeoCrawlTarget Target() => new()
    {
        Host = "ornek.test",
        Port = 80,
        UseTls = false,
        AllowedHosts = SeoLinkPolicy.BuildAllowedHosts(["ornek.test"])
    };

    /// <summary>Her sayfası 30 yeni bağlantı veren site (sonsuza yakın tarama kaynağı).</summary>
    private static string SayfaBaglantilarla(int adet)
    {
        var links = string.Join(string.Empty, Enumerable.Range(1, adet).Select(i => $"<a href=\"/s{i}-{Guid.NewGuid():N}\">s</a>"));
        return $"<html><head><title>t</title></head><body><h1>b</h1>{links}</body></html>";
    }

    [Fact]
    public async Task TOPLAM_SAYFA_SINIRI_asilmaz()
    {
        var handler = new SeoFakeHandler((_, _) => SeoFakeHandler.Html(SayfaBaglantilarla(30)));
        using var client = new HttpClient(handler);
        var limits = SeoCrawlLimits.Default with { MaxPages = 5 };

        var sonuc = await new SeoCrawler(client, limits).CrawlAsync(Target(), "/", null, CancellationToken.None);

        Assert.Equal(5, sonuc.Pages.Count);
        Assert.Equal(5, handler.Requests.Count);
        // Kırpılmış tarama "temiz" gibi görünmemeli: rapor bunu SÖYLEMELİ.
        Assert.True(sonuc.LimitReached);
    }

    [Fact]
    public async Task ESZAMANLI_ISTEK_SINIRI_asilmaz()
    {
        var handler = new SeoFakeHandler((_, _) => SeoFakeHandler.Html(SayfaBaglantilarla(10)))
        {
            // Gecikme olmadan istekler sırayla biter ve eşzamanlılık hiç ölçülemezdi.
            Delay = TimeSpan.FromMilliseconds(60)
        };

        using var client = new HttpClient(handler);
        var limits = SeoCrawlLimits.Default with { MaxPages = 12, MaxConcurrency = 2 };

        await new SeoCrawler(client, limits).CrawlAsync(Target(), "/", null, CancellationToken.None);

        Assert.True(handler.MaxConcurrent <= 2, $"EsZamanli istek siniri asildi: {handler.MaxConcurrent}");
        Assert.True(handler.MaxConcurrent >= 2, "EsZamanlilik hic kullanilmamis; olcum anlamsiz.");
    }

    [Fact]
    public async Task ISTEK_ZAMAN_ASIMI_taramayi_kilitlemez()
    {
        // Gecikme ile eşik ARASI bilerek çok geniş (30 sn ↔ 10 sn). Eskiden 5 sn gecikmeye karşı
        // "4 sn'den az sürdü" deniyordu; aradaki tek saniyelik pay TAM TAKIM YÜKÜ altında kapanıyor
        // ve test rastgele kırmızı veriyordu. Rastgele kırmızı veren bir test, ilerideki gerçek bir
        // arızanın "yine o test" diye elenmesine yol açar. Ölçülen şey değişmedi; yalnız iki durum
        // arasındaki mesafe, makinenin hızından etkilenmeyecek kadar açıldı.
        var handler = new SeoFakeHandler((_, _) => SeoFakeHandler.Html("<html><title>t</title></html>"))
        {
            Delay = TimeSpan.FromSeconds(30)
        };

        using var client = new HttpClient(handler);
        var limits = SeoCrawlLimits.Default with { MaxPages = 1, RequestTimeout = TimeSpan.FromMilliseconds(150) };

        var basladi = DateTime.UtcNow;
        var sonuc = await new SeoCrawler(client, limits).CrawlAsync(Target(), "/", null, CancellationToken.None);
        var gecen = DateTime.UtcNow - basladi;

        Assert.Single(sonuc.Pages);
        // ASIL SİNYAL BU: zaman aşımı uygulanmasaydı istek 30 sn sonra BAŞARIYLA dönerdi ve
        // Error boş olurdu. Yani hata metninin varlığı, saatten bağımsız olarak kuralı kanıtlar.
        Assert.NotEmpty(sonuc.Pages[0].Error);      // "ölçülemedi" — sessizce "sorun yok" DEĞİL
        Assert.True(gecen < TimeSpan.FromSeconds(10), $"Zaman asimi uygulanmamis: {gecen}");
    }

    [Fact]
    public async Task IPTAL_taramayi_DURDURUR_ama_olculeni_korur()
    {
        using var iptal = new CancellationTokenSource();
        var handler = new SeoFakeHandler((_, _) =>
        {
            // İlk sayfadan sonra iptal: gerçek kullanıcı davranışının karşılığı.
            iptal.CancelAfter(TimeSpan.FromMilliseconds(30));
            return SeoFakeHandler.Html(SayfaBaglantilarla(20));
        })
        {
            Delay = TimeSpan.FromMilliseconds(50)
        };

        using var client = new HttpClient(handler);
        var limits = SeoCrawlLimits.Default with { MaxPages = 100 };

        var sonuc = await new SeoCrawler(client, limits).CrawlAsync(Target(), "/", null, iptal.Token);

        Assert.True(sonuc.Interrupted);
        Assert.True(sonuc.Pages.Count < 100, "Iptal taramayi durdurmadi.");
    }

    [Fact]
    public async Task AYNI_SAYFA_iki_kez_gezilmez()
    {
        var handler = new SeoFakeHandler((_, path) => path switch
        {
            "/" => SeoFakeHandler.Html("<html><title>a</title><body><a href=\"/b\">b</a><a href=\"/b\">b</a></body></html>"),
            _ => SeoFakeHandler.Html("<html><title>b</title><body><a href=\"/\">a</a></body></html>")
        });

        using var client = new HttpClient(handler);

        var sonuc = await new SeoCrawler(client, SeoCrawlLimits.Default).CrawlAsync(Target(), "/", null, CancellationToken.None);

        Assert.Equal(2, sonuc.Pages.Count);
        Assert.Equal(2, handler.Requests.Count);
    }

    /// <summary>
    /// Sayfa başına bayt sınırı: sınırın ötesindeki içerik OKUNMAZ. Sınırsız okuma, tek bir dev
    /// dosyayla panelin belleğini tüketmenin en kolay yoluydu.
    /// </summary>
    [Fact]
    public async Task SAYFA_BASINA_BAYT_SINIRI_uygulanir()
    {
        var dolgu = new string('x', 5000);
        var handler = new SeoFakeHandler((_, _) =>
            SeoFakeHandler.Html($"<html><body>{dolgu}<title>gec-gelen-baslik</title></body></html>"));

        using var client = new HttpClient(handler);
        var limits = SeoCrawlLimits.Default with { MaxPages = 1, MaxResponseBytes = 512 };

        var sonuc = await new SeoCrawler(client, limits).CrawlAsync(Target(), "/", null, CancellationToken.None);

        // Başlık sınırın ötesinde kaldığı için OKUNAMAZ — kesme gerçekten olmuş demektir.
        Assert.Empty(sonuc.Pages[0].Title);
    }

    [Theory]
    [InlineData(0, SeoCrawlLimits.DefaultMaxPages)]
    [InlineData(-5, SeoCrawlLimits.DefaultMaxPages)]
    [InlineData(10, 10)]
    [InlineData(10_000, SeoCrawlLimits.HardMaxPages)]
    public void SAYFA_SINIRI_kirpilir(int istenen, int beklenen)
    {
        Assert.Equal(beklenen, SeoCrawlLimits.FromRequest(istenen).MaxPages);
    }

    /// <summary>
    /// Yönlendirme zinciri sonsuza kadar izlenmez; kendi kendine dönen bir yönlendirme taramayı
    /// kilitlerdi.
    /// </summary>
    [Fact]
    public async Task YONLENDIRME_ZINCIRI_sinirlidir()
    {
        // Her yol bir sonrakine yönlendirir: "/" → "/r" → "/rr" → ...
        var handler = new SeoFakeHandler((_, path) => SeoFakeHandler.Redirect(path == "/" ? "/r" : path + "r"));
        using var client = new HttpClient(handler);

        var sonuc = await new SeoCrawler(client, SeoCrawlLimits.Default with { MaxPages = 1 })
            .CrawlAsync(Target(), "/", null, CancellationToken.None);

        Assert.Equal(SeoCrawler.MaxRedirects + 1, handler.Requests.Count);
        Assert.Equal(SeoCrawler.MaxRedirects, sonuc.Pages[0].RedirectDepth);
    }

    /// <summary>Kırık iç bağlantı: 404 dönen sayfa, KAYNAK sayfasıyla birlikte raporlanır.</summary>
    [Fact]
    public async Task KIRIK_IC_BAGLANTI_kaynagiyla_bulunur()
    {
        var handler = new SeoFakeHandler((_, path) => path == "/yok"
            ? SeoFakeHandler.Status(HttpStatusCode.NotFound)
            : SeoFakeHandler.Html("<html><head><title>Ana</title><meta name=\"description\" content=\"a\"></head><body><h1>b</h1><a href=\"/yok\">kirik</a></body></html>"));

        using var client = new HttpClient(handler);
        var sonuc = await new SeoCrawler(client, SeoCrawlLimits.Default).CrawlAsync(Target(), "/", null, CancellationToken.None);

        var kirik = Assert.Single(sonuc.Pages, page => page.Path == "/yok");
        Assert.Equal(404, kirik.StatusCode);
        Assert.Equal("/", kirik.LinkedFrom);

        var bulgular = SeoFindingRules.Evaluate(sonuc.Pages, new() { Found = true }, new() { Found = true }, false);
        var bulgu = Assert.Single(bulgular, finding => finding.Key == "broken_link");
        Assert.Equal("error", bulgu.Severity);
        Assert.Contains("/", bulgu.Detail, StringComparison.Ordinal);
    }
}
