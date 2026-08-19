using System.Xml.Linq;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Infrastructure.Iis;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Plesk paritesindeki üç IIS ayarının (özel hata sayfaları, MIME türleri, HSTS) ÜRETTİĞİ
/// web.config'i sabitler.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden web.config XML'i ölçülüyor:</b> bu özelliklerin tamamı tek bir dosyaya yazmaktan
/// ibarettir ve o dosyadaki tek bir fazlalık sitenin TAMAMINI 500.19 ile düşürür. Panelde bu
/// tam olarak yaşandı: ipSecurity/httpRedirect/urlCompression bölümleri koşulsuz yazıldığı için
/// kullanıcı hiçbir ayarı değiştirmeden, yalnızca sekmeyi açarak siteyi düşürebiliyordu.
/// Yani burada korunan şey "ayar kaydedildi mi" değil, "site ayakta kalıyor mu"dur.
/// </para>
/// <para>
/// Gerçek IIS gerekmiyor: <c>UpdateWebConfig</c> saf bir XML üreticisidir, testler geçici bir
/// klasöre yazdırıp sonucu okur.
/// </para>
/// </remarks>
public sealed class SiteHostingIisSettingsTests
{
    // ---------------------------------------------------------------- özel hata sayfaları

    [Fact]
    public void Ozel_hata_sayfasi_once_remove_sonra_error_yazar()
    {
        using var site = new GeciciSite();
        var request = Istek();
        request.CustomErrorPages =
        [
            new SiteErrorPageDto { StatusCode = 404, Path = "/hatalar/404.html" },
            new SiteErrorPageDto { StatusCode = 500, Path = "/hatalar/500.html" }
        ];

        var httpErrors = site.Yaz(request).Element("httpErrors")!;

        // <remove> OLMADAN 500.19: IIS'in kendi yapılandırması 404 ve 500 için zaten birer <error>
        // tanımlar, koleksiyon anahtarı statusCode'dur ve mükerrer giriş siteyi komple düşürür.
        var cocuklar = httpErrors.Elements().Select(item => item.Name.LocalName).ToArray();
        Assert.Equal(["remove", "error", "remove", "error"], cocuklar);

        var dortYuzDort = httpErrors.Elements("error").Single(item => (string?)item.Attribute("statusCode") == "404");
        Assert.Equal("/hatalar/404.html", (string?)dortYuzDort.Attribute("path"));

        // ExecuteURL: yol aynı uygulamada çalıştırılır. File (mutlak disk yolu) yazılsaydı kiracı
        // komşu sitenin ya da Windows'un dosyasını hata sayfası diye yayınlayabilirdi.
        Assert.Equal("ExecuteURL", (string?)dortYuzDort.Attribute("responseMode"));

        var kaldirma = httpErrors.Elements("remove").First();
        Assert.Equal("404", (string?)kaldirma.Attribute("statusCode"));
        Assert.Equal("-1", (string?)kaldirma.Attribute("subStatusCode"));
    }

    [Fact]
    public void Ekrandan_kaldirilan_hata_sayfasi_web_configde_KALMAZ()
    {
        using var site = new GeciciSite();
        var ilk = Istek();
        ilk.CustomErrorPages = [new SiteErrorPageDto { StatusCode = 404, Path = "/eski.html" }];
        site.Yaz(ilk);

        // Kullanıcı kaydı sildi: dosyada iz kalırsa "sildim" der ama IIS eski sayfayı sunmaya devam eder.
        var httpErrors = site.Yaz(Istek()).Element("httpErrors")!;

        Assert.Empty(httpErrors.Elements("error"));
        Assert.Empty(httpErrors.Elements("remove"));
        // Var olan davranış korunuyor mu: ayrıntılı hata anahtarı hâlâ yazılıyor.
        Assert.Equal("DetailedLocalOnly", (string?)httpErrors.Attribute("errorMode"));
    }

    [Theory]
    [InlineData("/../../windows/win.ini")]  // klasik dizinden çıkma
    [InlineData("/..%2fgizli.html")]        // yüzde kaçışı + ".."
    [InlineData("\\\\sunucu\\pay\\404.html")] // UNC
    [InlineData("//saldirgan.example/404.html")] // protokol-göreli URL
    [InlineData("http://saldirgan.example/404.html")] // mutlak dış URL
    [InlineData("C:/Windows/win.ini")]      // mutlak yerel yol
    [InlineData("hatalar/404.html")]        // göreli (kök işareti yok)
    [InlineData("/%windir%/win.ini")]       // IIS path özniteliği ortam değişkeni genişletir
    [InlineData("/con")]                    // Windows ayrılmış aygıt adı -> "\\.\con"
    [InlineData("/gizli\u0000.html")]       // gömülü denetim karakteri
    public void Site_disina_cikan_hata_sayfasi_yolu_REDDEDILIR(string yol)
    {
        using var site = new GeciciSite();
        var request = Istek();
        request.CustomErrorPages = [new SiteErrorPageDto { StatusCode = 404, Path = yol }];

        var hata = Assert.Throws<AppException>(() => site.Yaz(request));
        Assert.Equal("sites.invalid_error_page", hata.Message);
    }

    [Fact]
    public void Ayrilmis_aygit_adini_yalniz_KAPSAMA_denetimi_yakalar()
    {
        // Bu test, kapsama denetiminin "her ihtimale karşı" değil GEREKLİ olduğunu sabitler:
        // "/con" bütün biçim denetimlerinden geçer (kökle başlar, "..", ters bölü, ':' yok),
        // ama Windows onu "\\.\con" aygıt yoluna çevirir.
        var kok = Path.Combine(Path.GetTempPath(), "ada-hosting-kapsama");
        Assert.False(PathContainment.IsInsideRoot(kok, Path.Combine(kok, "con")));
        Assert.True(PathContainment.IsInsideRoot(kok, Path.Combine(kok, "404.html")));
    }

    [Theory]
    [InlineData(399)]
    [InlineData(1000)]
    [InlineData(0)]
    public void IIS_araligi_disindaki_durum_kodu_REDDEDILIR(int kod)
    {
        using var site = new GeciciSite();
        var request = Istek();
        request.CustomErrorPages = [new SiteErrorPageDto { StatusCode = kod, Path = "/404.html" }];

        // IIS şeması 400-999 dayatır; dışına çıkan değer web.config'i geçersiz kılar -> 500.19.
        Assert.Throws<AppException>(() => site.Yaz(request));
    }

    [Fact]
    public void Ayni_durum_kodu_iki_kez_REDDEDILIR()
    {
        using var site = new GeciciSite();
        var request = Istek();
        request.CustomErrorPages =
        [
            new SiteErrorPageDto { StatusCode = 404, Path = "/a.html" },
            new SiteErrorPageDto { StatusCode = 404, Path = "/b.html" }
        ];

        // Mükerrer koleksiyon anahtarı = 500.19. Sessizce birini atmak, kullanıcının hangisinin
        // geçerli olduğunu bilmemesi demekti.
        Assert.Throws<AppException>(() => site.Yaz(request));
    }

    // ---------------------------------------------------------------------- MIME türleri

    [Fact]
    public void Mime_eslemesi_remove_ve_mimeMap_ciftiyle_yazilir_uzanti_normallesir()
    {
        using var site = new GeciciSite();
        var request = Istek();
        request.MimeTypes =
        [
            new SiteMimeTypeDto { Extension = "WOFF2", MimeType = "font/woff2" },
            new SiteMimeTypeDto { Extension = ".json", MimeType = "application/json; charset=utf-8" }
        ];

        var staticContent = site.Yaz(request).Element("staticContent")!;

        // Kullanıcı "WOFF2" yazdı; IIS yalnız ".woff2" biçimini kabul eder.
        var woff = staticContent.Elements("mimeMap").Single(item => (string?)item.Attribute("fileExtension") == ".woff2");
        Assert.Equal("font/woff2", (string?)woff.Attribute("mimeType"));

        // Var olan bir uzantıyı (.json IIS'te tanımlıdır) EZMENİN tek yolu önce <remove>.
        Assert.Contains(staticContent.Elements("remove"), item => (string?)item.Attribute("fileExtension") == ".json");
    }

    [Fact]
    public void Mime_bolumu_ASLA_clear_ile_temizlenmez()
    {
        using var site = new GeciciSite();
        var request = Istek();
        request.MimeTypes = [new SiteMimeTypeDto { Extension = ".woff2", MimeType = "font/woff2" }];

        var staticContent = site.Yaz(request).Element("staticContent")!;

        // <clear/> buraya yazılırsa .html/.css/.js dahil TÜM varsayılan eşlemeler silinir ve site
        // her statik dosyaya 404.3 döner — yani "bir MIME türü ekledim" isteği siteyi komple
        // yayından kaldırır. httpErrors'ta güvenli olan şey burada felakettir.
        Assert.Empty(staticContent.Elements("clear"));
    }

    [Fact]
    public void Mime_listesi_bosalinca_bolum_kaldirilir_ama_diger_ayarlar_korunur()
    {
        using var site = new GeciciSite();
        var request = Istek();
        request.MimeTypes = [new SiteMimeTypeDto { Extension = ".woff2", MimeType = "font/woff2" }];
        site.Yaz(request);

        // KULLANILMAYAN BÖLÜM YAZILMAZ (panelin mevcut kuralı): eşleme kalmadıysa bölüm gitmeli.
        Assert.Null(site.Yaz(Istek()).Element("staticContent"));

        // Ama bölümde başka bir ayar varsa ona dokunulmaz: onu silmek bizim işimiz değil.
        var belge = XDocument.Load(site.WebConfigYolu, LoadOptions.PreserveWhitespace);
        var sistem = belge.Element("configuration")!.Element("system.webServer")!;
        sistem.Add(new XElement("staticContent", new XElement("clientCache", new XAttribute("cacheControlMode", "UseMaxAge"))));
        belge.Save(site.WebConfigYolu);

        var sonrasi = site.Yaz(Istek()).Element("staticContent");
        Assert.NotNull(sonrasi);
        Assert.NotNull(sonrasi!.Element("clientCache"));
    }

    [Theory]
    [InlineData(".woff2", "font woff2")]        // boşluk
    [InlineData(".woff2", "fontwoff2")]         // eğik çizgi yok
    [InlineData(".woff2", "font/woff2\" evil=\"x")] // öznitelik kaçışı denemesi
    [InlineData("../gizli", "text/plain")]      // uzantı değil, yol
    [InlineData(".woff 2", "font/woff2")]       // uzantıda boşluk
    public void Gecersiz_MIME_girdisi_REDDEDILIR(string uzanti, string tur)
    {
        using var site = new GeciciSite();
        var request = Istek();
        request.MimeTypes = [new SiteMimeTypeDto { Extension = uzanti, MimeType = tur }];

        var hata = Assert.Throws<AppException>(() => site.Yaz(request));
        Assert.Equal("sites.invalid_mime_type", hata.Message);
    }

    [Fact]
    public void Ayni_uzanti_iki_kez_REDDEDILIR()
    {
        using var site = new GeciciSite();
        var request = Istek();
        request.MimeTypes =
        [
            new SiteMimeTypeDto { Extension = ".woff2", MimeType = "font/woff2" },
            new SiteMimeTypeDto { Extension = "woff2", MimeType = "application/octet-stream" }
        ];

        Assert.Throws<AppException>(() => site.Yaz(request));
    }

    // ------------------------------------------------------------------------------ HSTS

    [Theory]
    [InlineData(31_536_000, false, false, "max-age=31536000")]
    [InlineData(31_536_000, true, false, "max-age=31536000; includeSubDomains")]
    [InlineData(31_536_000, true, true, "max-age=31536000; includeSubDomains; preload")]
    [InlineData(0, false, false, "max-age=0")]
    public void HSTS_basligi_secilen_secenegi_birebir_yansitir(int sure, bool altAlanlar, bool preload, string beklenen)
    {
        using var site = new GeciciSite();
        var request = Istek();
        request.HstsEnabled = true;
        request.HstsMaxAgeSeconds = sure;
        request.HstsIncludeSubDomains = altAlanlar;
        request.HstsPreload = preload;

        var baslik = HstsBasligi(site.Yaz(request));

        Assert.Equal(beklenen, baslik);
    }

    [Fact]
    public void HSTS_kapatilinca_baslik_gider_ama_diger_guvenlik_basliklari_kalir()
    {
        using var site = new GeciciSite();
        var acik = Istek();
        acik.HstsEnabled = true;
        site.Yaz(acik);

        // WafService de aynı koleksiyona yazar; onun başlıklarını silmek sessiz bir güvenlik gerilemesi olurdu.
        var belge = XDocument.Load(site.WebConfigYolu, LoadOptions.PreserveWhitespace);
        belge.Descendants("customHeaders").Single()
            .Add(new XElement("add", new XAttribute("name", "X-Frame-Options"), new XAttribute("value", "SAMEORIGIN")));
        belge.Save(site.WebConfigYolu);

        var sonrasi = site.Yaz(Istek());

        Assert.Null(HstsBasligi(sonrasi));
        Assert.Contains(
            sonrasi.Descendants("customHeaders").Elements("add"),
            item => (string?)item.Attribute("name") == "X-Frame-Options");
    }

    [Fact]
    public void HSTS_kapaliyken_bos_customHeaders_bolumu_URETILMEZ()
    {
        using var site = new GeciciSite();

        // Kullanılmayan bölüm yazılmaz: her kayıtta boş bir <httpProtocol> biriktirmek gürültüdür.
        Assert.Null(site.Yaz(Istek()).Element("httpProtocol"));
    }

    [Theory]
    [InlineData(31_536_000, false, true)]   // preload var, includeSubDomains yok
    [InlineData(86_400, true, true)]        // preload var, süre 1 yıldan kısa
    [InlineData(63_072_001, false, false)]  // 2 yıl üstü
    [InlineData(-1, false, false)]          // negatif süre
    public void Etkisiz_veya_gecersiz_HSTS_yapilandirmasi_REDDEDILIR(int sure, bool altAlanlar, bool preload)
    {
        using var site = new GeciciSite();
        var request = Istek();
        request.HstsEnabled = true;
        request.HstsMaxAgeSeconds = sure;
        request.HstsIncludeSubDomains = altAlanlar;
        request.HstsPreload = preload;

        // preload listesine giriş max-age >= 1 yıl VE includeSubDomains ister. Şartsız yazılan
        // "preload" hiçbir işe yaramaz ama kullanıcı korunduğunu sanır.
        var hata = Assert.Throws<AppException>(() => site.Yaz(request));
        Assert.Equal("sites.invalid_hsts", hata.Message);
    }

    // ------------------------------------------------------------------------ yardımcılar

    private static string? HstsBasligi(XElement systemWebServer) =>
        (string?)systemWebServer
            .Descendants("customHeaders")
            .Elements("add")
            .FirstOrDefault(item => (string?)item.Attribute("name") == "Strict-Transport-Security")
            ?.Attribute("value");

    /// <summary>Yalnız yeni alanları taşıyan, geri kalanı geçerli varsayılan olan istek.</summary>
    private static UpdateSiteHostingSettingsRequest Istek() => new();

    /// <summary>Geçici bir site kökü; <c>Yaz</c> web.config üretip system.webServer'ı döndürür.</summary>
    private sealed class GeciciSite : IDisposable
    {
        public GeciciSite()
        {
            Kok = Path.Combine(Path.GetTempPath(), "ada-hosting-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Kok);
        }

        public string Kok { get; }

        public string WebConfigYolu => Path.Combine(Kok, "web.config");

        public XElement Yaz(UpdateSiteHostingSettingsRequest request)
        {
            SiteHostingSettingsService.UpdateWebConfig(Kok, request);
            return XDocument.Load(WebConfigYolu, LoadOptions.PreserveWhitespace)
                .Element("configuration")!
                .Element("system.webServer")!;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Kok, true);
            }
            catch
            {
                // Geçici klasör silinemezse test sonucunu etkilemez.
            }
        }
    }
}
