using System.Net;
using System.Net.Sockets;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Infrastructure.Seo;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// SEO TARAYICISININ SSRF KAPISI — bu dosyanın koruduğu şey tek bir davranış değil, bir SINIF:
/// "panel, kendisine söylenen herhangi bir adrese istek atar" hatası.
/// </summary>
/// <remarks>
/// <para>
/// <b>Tehdit:</b> tarayıcı sayfaların İÇİNDEKİ bağlantıları izler ve o sayfaların içeriğini
/// kiracının kendisi yazar. Bağlantılar körü körüne izlenseydi kiracı, kendi sayfasına
/// <c>&lt;a href="http://169.254.169.254/latest/meta-data/"&gt;</c> koyarak paneli bulut meta veri
/// servisine, <c>http://127.0.0.1:8890/</c> ile panelin KENDİ API'sine ya da <c>http://10.0.0.5/</c>
/// ile iç ağa istek attırabilirdi. Panel LocalSystem koştuğu için bedeli yüksektir.
/// </para>
/// <para>
/// <b>İki katman ayrı ayrı sınanır:</b> (1) <see cref="SeoLinkPolicy"/> kararı — yabancı konak
/// GEZİLMEZ; (2) <see cref="SeoHttpTransport"/> taşıması — soket, istenen ad ne olursa olsun
/// loopback'e bağlanır. İkinci katman, birincisinde bir hata olsa bile isteğin makineden
/// çıkamayacağını garanti eder.
/// </para>
/// </remarks>
public sealed class SeoSsrfGuardTests
{
    private static SeoCrawlTarget Target(params string[] hosts)
    {
        var allowed = SeoLinkPolicy.BuildAllowedHosts(hosts.Length == 0 ? ["ornek.test"] : hosts);
        return new SeoCrawlTarget
        {
            Host = hosts.Length == 0 ? "ornek.test" : hosts[0],
            Port = 80,
            UseTls = false,
            AllowedHosts = allowed
        };
    }

    // --- 1. KATMAN: bağlantı kararı ---

    /// <summary>
    /// Panelin barındırmadığı adresler ASLA gezilmez. Bunlar SSRF'in klasik hedefleri: bulut meta
    /// veri servisi, loopback (panelin kendi API'si), iç ağ ve dış internet.
    /// </summary>
    [Theory]
    [InlineData("http://169.254.169.254/latest/meta-data/")]          // AWS/Azure/GCP meta veri
    [InlineData("http://[fd00:ec2::254]/latest/meta-data/")]          // IPv6 meta veri
    [InlineData("http://metadata.google.internal/computeMetadata/")]  // GCP ad ile
    [InlineData("http://localhost/admin")]
    [InlineData("http://localhost:8890/api/v1/system/overview")]      // panelin KENDİ API'si
    [InlineData("http://127.0.0.1/")]
    [InlineData("http://127.0.0.1:8890/")]
    [InlineData("http://[::1]/")]
    [InlineData("http://10.0.0.5/yonetim")]
    [InlineData("http://192.168.1.1/")]
    [InlineData("http://172.16.0.9/")]
    [InlineData("https://baskasite.test/")]
    [InlineData("//169.254.169.254/latest/meta-data/")]               // protokolsüz mutlak adres
    public void PanelinBarindirmadigiAdresler_GEZILMEZ(string href)
    {
        var karar = SeoLinkPolicy.Classify(href, "/", Target());

        Assert.NotEqual(SeoLinkKind.Internal, karar.Kind);
    }

    /// <summary>
    /// Kimlik gömülü adres (<c>http://kullanici@konak/</c>): bazı ayrıştırıcılar konağı yanlış
    /// okur, bu yüzden hiç değerlendirilmez.
    /// </summary>
    [Theory]
    [InlineData("http://ornek.test@169.254.169.254/")]
    [InlineData("http://kullanici:parola@169.254.169.254/")]
    public void KimlikGomuluAdres_GEZILMEZ(string href)
    {
        Assert.NotEqual(SeoLinkKind.Internal, SeoLinkPolicy.Classify(href, "/", Target()).Kind);
    }

    /// <summary>
    /// HTTP olmayan şemalar ADRES KURULMADAN ÖNCE elenir.
    /// <para>
    /// Gerekçe kodu ("scheme_prefix") hangi kapının çalıştığını söyler: erken çıkış olmadan
    /// <c>data:text/html;base64,…</c> gibi megabaytlık bir değer önce bir <c>Uri</c> nesnesine
    /// çevrilir, sonra atılırdı. Sonuç aynı olduğu için bu farkı ölçen tek şey gerekçe kodudur —
    /// mutasyon testi erken çıkışın silinmesinin HİÇBİR testi kırmadığını gösterince eklendi.
    /// </para>
    /// </summary>
    [Fact]
    public void HttpDisiSemalar_ADRES_KURULMADAN_elenir()
    {
        var karar = SeoLinkPolicy.Classify("data:text/html;base64,PHNjcmlwdD4=", "/", Target());

        Assert.Equal(SeoLinkKind.Skipped, karar.Kind);
        Assert.Equal("scheme_prefix", karar.Reason);
    }

    /// <summary>HTTP olmayan şemalar istek üretmez (dosya okuma, veri gömme, betik çalıştırma).</summary>
    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html;base64,PHNjcmlwdD4=")]
    [InlineData("file:///C:/Windows/win.ini")]
    [InlineData("ftp://ornek.test/dosya")]
    [InlineData("mailto:posta@ornek.test")]
    [InlineData("tel:+905551112233")]
    [InlineData("gopher://169.254.169.254:70/")]
    public void HttpDisiSemalar_ATLANIR(string href)
    {
        Assert.Equal(SeoLinkKind.Skipped, SeoLinkPolicy.Classify(href, "/", Target()).Kind);
    }

    /// <summary>
    /// Sitenin KENDİ adresleri gezilir — koruma, aracın işini yapmasını engellememeli.
    /// (Bu test olmadan "her şeyi reddet" mutasyonu güvenlik testlerini geçerdi.)
    /// </summary>
    [Theory]
    [InlineData("/hakkimizda", "/hakkimizda")]
    [InlineData("urunler", "/urunler")]
    [InlineData("http://ornek.test/blog", "/blog")]
    [InlineData("https://ornek.test/blog?sayfa=2", "/blog?sayfa=2")]
    [InlineData("//ornek.test/iletisim", "/iletisim")]
    [InlineData("/sayfa#bolum", "/sayfa")]
    public void SitenınKendiSayfalari_GEZILIR(string href, string beklenenYol)
    {
        var karar = SeoLinkPolicy.Classify(href, "/", Target());

        Assert.Equal(SeoLinkKind.Internal, karar.Kind);
        Assert.Equal(beklenenYol, karar.Value);
    }

    /// <summary>
    /// Site birden çok alan adına bağlıysa hepsi kendisidir; bağlı OLMAYAN bir alt alan adı
    /// (ör. www) dış bağlantıdır — başkasına ait olabilir.
    /// </summary>
    [Fact]
    public void YalnizBAGLI_alanAdlari_iceridir()
    {
        var hedef = Target("ornek.test", "magaza.ornek.test");

        Assert.Equal(SeoLinkKind.Internal, SeoLinkPolicy.Classify("https://magaza.ornek.test/urun", "/", hedef).Kind);
        Assert.Equal(SeoLinkKind.External, SeoLinkPolicy.Classify("https://www.ornek.test/", "/", hedef).Kind);
    }

    /// <summary>
    /// Standart olmayan port DIŞ sayılır: kendi alan adı bile olsa loopback üzerinde rastgele bir
    /// porta (panelin 8890'ı, veritabanı, WinRM) istek atmanın kapısı açık kalmamalı.
    /// </summary>
    [Theory]
    [InlineData("http://ornek.test:8890/api/v1/system/overview")]
    [InlineData("http://ornek.test:3306/")]
    [InlineData("http://ornek.test:5985/wsman")]
    public void StandartOlmayanPort_GEZILMEZ(string href)
    {
        Assert.NotEqual(SeoLinkKind.Internal, SeoLinkPolicy.Classify(href, "/", Target()).Kind);
    }

    // --- İstek adresi kurulumu (son kapı) ---

    /// <summary>
    /// <c>Uri</c> birleştirmesinde <c>//konak/yol</c> KONAĞI DEĞİŞTİRİR. Adres kurucusunun son
    /// doğrulaması tam olarak bu tuzağı kapatır.
    /// </summary>
    [Theory]
    [InlineData("//169.254.169.254/latest/meta-data/")]
    [InlineData("//baskasite.test/")]
    [InlineData("http://169.254.169.254/")]
    [InlineData("hakkimizda")]
    [InlineData("")]
    [InlineData("/yol\u0000/bos")]
    // TERS EGIK CIZGI: OLCULDU (bu makinede, .NET 8) --
    // Uri.TryCreate(http://ornek.test/, "/\169.254.169.254/x") -> konak ornek.test KALIR,
    // yol "//169.254.169.254/x" olur. Yani tek egik cizgiyle baslayan gorelide konak DEGISMEZ;
    // bu bir SSRF vektoru DEGILDIR. Yine de reddedilir: hicbir mesru yolda bulunmaz ve
    // TryNormalizeStartPath ile ayni kurali uygular (iki kapi ayni seyi soylemeli).
    [InlineData("/\\169.254.169.254/latest/meta-data/")]
    [InlineData("/\\baskasite.test/")]
    public void IstekAdresi_KURULAMAZ(string yol)
    {
        Assert.False(SeoLinkPolicy.TryBuildRequestUri(Target(), yol, out var adres));
        Assert.Null(adres);
    }

    [Fact]
    public void IstekAdresi_HEDEFIN_konagiyla_kurulur()
    {
        Assert.True(SeoLinkPolicy.TryBuildRequestUri(Target(), "/urunler?sayfa=2", out var adres));
        Assert.NotNull(adres);
        Assert.Equal("ornek.test", adres!.Host);
        Assert.Equal(80, adres.Port);
        Assert.Equal("/urunler?sayfa=2", adres.PathAndQuery);
    }

    /// <summary>Kullanıcının verdiği başlangıç yolu hedefi değiştiremez.</summary>
    [Theory]
    [InlineData("//169.254.169.254/")]
    [InlineData("http://169.254.169.254/")]
    [InlineData("hakkimizda")]
    [InlineData("/yol boslukla")]
    [InlineData("/..\\..\\windows")]
    [InlineData("/gizli/../../kok")]
    public void BaslangicYolu_REDDEDILIR(string deger)
    {
        Assert.False(SeoLinkPolicy.TryNormalizeStartPath(deger, out _));
    }

    [Theory]
    [InlineData("", "/")]
    [InlineData("   ", "/")]
    [InlineData("/blog", "/blog")]
    [InlineData("/blog/2026?etiket=seo", "/blog/2026?etiket=seo")]
    public void BaslangicYolu_KABUL_EDILIR(string deger, string beklenen)
    {
        Assert.True(SeoLinkPolicy.TryNormalizeStartPath(deger, out var yol));
        Assert.Equal(beklenen, yol);
    }

    // --- 2. KATMAN: taşıma ---

    /// <summary>
    /// SABİTLENMİŞ SOKET: istenen konak adı <b>169.254.169.254</b> olsa bile bağlantı, sabitlenen
    /// loopback adresine kurulur. Bu test gerçek bir dinleyiciyle ölçer — "ad çözümlemesi
    /// yapılmıyor" iddiası böylece kanıtlanır, varsayılmaz.
    /// </summary>
    [Fact]
    public async Task Soket_ISTENEN_ADI_YOK_SAYAR_ve_loopbacke_baglanir()
    {
        var dinleyici = new TcpListener(IPAddress.Loopback, 0);
        dinleyici.Start();
        try
        {
            var sabit = (IPEndPoint)dinleyici.LocalEndpoint;
            var kabul = dinleyici.AcceptTcpClientAsync();

            await using var akis = await SeoHttpTransport.ConnectAsync(
                sabit,
                new DnsEndPoint("169.254.169.254", 80),
                CancellationToken.None);

            using var baglanan = await kabul.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
            var uzak = (IPEndPoint)baglanan.Client.RemoteEndPoint!;

            Assert.True(IPAddress.IsLoopback(uzak.Address), $"Baglanti loopback DISINA gitti: {uzak.Address}");
        }
        finally
        {
            dinleyici.Stop();
        }
    }

    // --- Uçtan uca: kiracının HTML'i tarayıcıyı yönlendiremez ---

    /// <summary>
    /// GERÇEK AKIŞ: kiracının sayfası meta veri servisine, panelin API'sine ve iç ağa bağlantı
    /// verir. Tarayıcı bu adreslere İSTEK ATMAMALI; yalnız kendi sayfalarını gezmeli.
    /// </summary>
    [Fact]
    public async Task Tarayici_sayfadaki_YABANCI_adreslere_istek_ATMAZ()
    {
        const string html = """
            <html><head><title>Ana sayfa</title></head><body>
              <h1>Merhaba</h1>
              <a href="/hakkimizda">Hakkimizda</a>
              <a href="http://169.254.169.254/latest/meta-data/">meta</a>
              <a href="http://127.0.0.1:8890/api/v1/system/overview">panel</a>
              <a href="http://10.0.0.5/yonetim">ic ag</a>
              <a href="//169.254.169.254/">protokolsuz</a>
              <a href="file:///C:/Windows/win.ini">dosya</a>
              <a href="https://baskasite.test/">disari</a>
            </body></html>
            """;

        var kayit = new SeoFakeHandler((_, path) => SeoFakeHandler.Html(html));
        using var client = new HttpClient(kayit);
        var crawler = new SeoCrawler(client, SeoCrawlLimits.Default);

        await crawler.CrawlAsync(Target(), "/", null, CancellationToken.None);

        Assert.All(kayit.Requests, adres =>
            Assert.Equal("ornek.test", adres.Host));
        Assert.DoesNotContain(kayit.Requests, adres => adres.AbsoluteUri.Contains("169.254", StringComparison.Ordinal));
        Assert.DoesNotContain(kayit.Requests, adres => adres.Port == 8890);
        // Kendi sayfasını gezdiğini de doğrula: "hiç istek atma" mutasyonu bu testi geçmesin.
        Assert.Contains(kayit.Requests, adres => adres.AbsolutePath == "/hakkimizda");
    }

    /// <summary>
    /// YÖNLENDİRME İLE KAÇIŞ: sayfa <c>302 Location: http://169.254.169.254/</c> döndürürse
    /// tarayıcı bunu İZLEMEMELİ. (HttpClient'ın otomatik yönlendirmesi açık olsaydı bu kapı
    /// sessizce atlanırdı.)
    /// </summary>
    [Fact]
    public async Task Yonlendirme_SITE_DISINA_izlenmez()
    {
        var kayit = new SeoFakeHandler((_, path) => path == "/"
            ? SeoFakeHandler.Redirect("http://169.254.169.254/latest/meta-data/")
            : SeoFakeHandler.Html("<html><title>x</title></html>"));

        using var client = new HttpClient(kayit);
        var crawler = new SeoCrawler(client, SeoCrawlLimits.Default);

        var sonuc = await crawler.CrawlAsync(Target(), "/", null, CancellationToken.None);

        Assert.All(kayit.Requests, adres => Assert.Equal("ornek.test", adres.Host));
        Assert.Single(kayit.Requests);
        Assert.Equal(302, sonuc.Pages[0].StatusCode);
    }

    /// <summary>
    /// SITEMAP İLE KAÇIŞ: sitemap.xml'i site sahibi yazar. İçindeki yabancı adresler DENENMEZ,
    /// yalnız raporlanır.
    /// </summary>
    [Fact]
    public void Sitemapteki_yabanci_adresler_ICERIDE_sayilmaz()
    {
        const string xml = """
            <urlset>
              <url><loc>https://ornek.test/</loc></url>
              <url><loc>http://169.254.169.254/latest/meta-data/</loc></url>
              <url><loc>http://127.0.0.1:8890/api/v1/system/overview</loc></url>
            </urlset>
            """;

        var hedef = Target();
        var kararlar = SeoRobotsAnalyzer.ParseSitemapLocations(xml)
            .Select(loc => SeoLinkPolicy.Classify(loc, "/", hedef))
            .ToArray();

        Assert.Equal(SeoLinkKind.Internal, kararlar[0].Kind);
        Assert.NotEqual(SeoLinkKind.Internal, kararlar[1].Kind);
        Assert.NotEqual(SeoLinkKind.Internal, kararlar[2].Kind);
    }

    /// <summary>
    /// Konak adı olmayan (yalnız IP:port bağlamalı) site TARANAMAZ: Host başlığı olmadan IIS'in
    /// hangi siteyi döneceği belirsizdir ve ölçüm BAŞKA bir siteyi ölçebilirdi.
    /// </summary>
    [Fact]
    public void KonakAdiOlmayanSite_taranamaz()
    {
        var site = new SiteDto
        {
            Name = "ipsite",
            Bindings = [new SiteBindingDto { Protocol = "http", HostName = string.Empty, IpAddress = "10.0.0.5", Port = 80 }]
        };

        var cozum = SeoTargetResolver.Resolve(site);

        Assert.Null(cozum.Target);
        Assert.Equal("no_host", cozum.Reason);
    }

    /// <summary>
    /// Hedef YALNIZCA sitenin IIS bağlamalarından türer. Bu, kapsam denetiminden geçen site adının
    /// tarama hedefini de kilitlediği anlamına gelir — istek gövdesinde adres alanı YOKTUR.
    /// </summary>
    [Fact]
    public void Hedef_yalniz_SITENIN_baglamalarindan_turer()
    {
        var site = new SiteDto
        {
            Name = "ornek.test",
            Bindings =
            [
                new SiteBindingDto { Protocol = "http", HostName = "ornek.test", Port = 80 },
                new SiteBindingDto { Protocol = "https", HostName = "ornek.test", Port = 443 },
                new SiteBindingDto { Protocol = "http", HostName = "magaza.ornek.test", Port = 80 }
            ]
        };

        var hedef = SeoTargetResolver.Resolve(site).Target;

        Assert.NotNull(hedef);
        Assert.Equal("ornek.test", hedef!.Host);
        Assert.Equal(443, hedef.Port);          // https tercih edilir
        Assert.True(hedef.UseTls);
        Assert.Contains("magaza.ornek.test", hedef.AllowedHosts);
        Assert.DoesNotContain("169.254.169.254", hedef.AllowedHosts);
    }
}
