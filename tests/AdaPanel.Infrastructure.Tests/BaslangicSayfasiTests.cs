using AdaPanel.Infrastructure.Iis;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Yeni sitenin karşılama sayfası — <b>herkese açık</b> olduğu için içeriği bir güvenlik kararıdır.
/// </summary>
/// <remarks>
/// <para>
/// Sayfa, site kurulduğu andan içerik yüklenene kadar alan adını açan HERKESE görünür. Buradaki
/// testler "güzel mi" sorusunu ölçmez — ölçemez. Ölçtükleri şey, sayfanın ziyaretçiye
/// GÖSTERMEMESİ gerekenleri göstermediğidir.
/// </para>
/// <para>
/// Eski sayfa <c>WebAdministrationSiteService</c> içine gömülüydü ve yalnız gerçek bir IIS
/// kurulumunda görülebiliyordu. Sonuç: aylarca fark edilmeyen bozuk Türkçe ("yayinda",
/// "degistirebilirsiniz") ve yalnız koyu temada çalışan bir sayfa.
/// </para>
/// </remarks>
public sealed class BaslangicSayfasiTests
{
    /// <summary>
    /// SUNUCUDAKİ FİZİKSEL YOL SIZMAZ.
    /// </summary>
    /// <remarks>
    /// "Dosyalarınızı şuraya yükleyin: <c>C:\inetpub\wwwroot\musteri.com</c>" cümlesi yardımcı
    /// görünür ama sunucunun klasör düzenini ziyaretçiye açar. Yol panelde zaten var; sayfanın işi
    /// onu internete taşımak değil. Plesk ve cPanel'in varsayılan sayfaları da yol yazmaz.
    /// <para>
    /// Sürücü harfi deseni aranıyor çünkü yolun sayfaya sızması ancak böyle görünür.
    /// </para>
    /// </remarks>
    [Fact]
    public void Sunucudaki_fiziksel_yol_sayfada_gecmez()
    {
        var html = StarterPage.Render("musteri.com", "musteri.com", 80);

        Assert.DoesNotContain(@"C:\", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("inetpub", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("wwwroot", html, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// DIŞ KAYNAK YOKTUR: yazı tipi, betik ya da resim indirilmez.
    /// </summary>
    /// <remarks>
    /// İki gerekçe: sayfa internete çıkamayan bir sunucuda da aynı görünmeli, ve her yeni site
    /// üçüncü bir tarafa ziyaretçi bilgisi sızdırmamalı.
    /// </remarks>
    [Fact]
    public void Dis_kaynak_yuklenmez()
    {
        var html = StarterPage.Render("musteri.com", "musteri.com", 80);

        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<link", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<img", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("//fonts.", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://", html, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Site adı HTML olarak KAÇIRILIR — ad doğrulamasının doğruluğuna bağlı kalınmaz.
    /// </summary>
    [Fact]
    public void Site_adi_kacirilir()
    {
        var html = StarterPage.Render("<script>alert(1)</script>", "musteri.com", 80);

        Assert.DoesNotContain("<script>alert", html, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// İKİ TEMA da desteklenir — eski sayfa yalnız koyuydu.
    /// </summary>
    [Fact]
    public void Acik_ve_koyu_tema_desteklenir()
    {
        var html = StarterPage.Render("musteri.com", "musteri.com", 80);

        Assert.Contains("color-scheme: light dark", html, StringComparison.Ordinal);
        Assert.Contains("prefers-color-scheme: dark", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// Telefonda okunur: görünüm penceresi (viewport) bildirilir.
    /// </summary>
    [Fact]
    public void Mobil_gorunum_bildirilir()
    {
        var html = StarterPage.Render("musteri.com", "musteri.com", 80);

        Assert.Contains("name=\"viewport\"", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// Arama motoru bu sayfayı dizine almamalı.
    /// </summary>
    /// <remarks>
    /// Aksi hâlde müşterinin alan adı, gerçek içeriği yayına girmeden önce "Site yayında" başlıklı
    /// bir yer tutucuyla aranabilir hâle gelirdi.
    /// </remarks>
    [Fact]
    public void Arama_motoruna_kapalidir()
    {
        var html = StarterPage.Render("musteri.com", "musteri.com", 80);

        Assert.Contains("name=\"robots\" content=\"noindex\"", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// VARSAYILAN PORT ADRESTE GÖSTERİLMEZ; varsayılan olmayan port GÖSTERİLİR.
    /// </summary>
    /// <remarks>
    /// "musteri.com:80" adresi olduğundan karmaşık gösterir ve kullanıcıya adresi öyle yazması
    /// gerektiğini düşündürür. Ama 8080 gibi bir port GERÇEKTEN gereklidir; onu gizlemek siteyi
    /// açılamaz gösterirdi.
    /// </remarks>
    [Theory]
    [InlineData(80, false)]
    [InlineData(443, false)]
    [InlineData(8080, true)]
    public void Adres_portu_yalniz_gerektiginde_gosterir(int port, bool portGorunur)
    {
        var html = StarterPage.Render("musteri.com", "musteri.com", port);

        Assert.Equal(portGorunur, html.Contains($"musteri.com:{port}", StringComparison.Ordinal));
    }

    /// <summary>
    /// ADRES, SİTE ADIYLA AYNIYSA YAZILMAZ — önizlemede görülen kusur.
    /// </summary>
    /// <remarks>
    /// İlk yazımda adres her zaman basılıyordu; site adı ile host adı aynı olduğunda (en olağan
    /// durum) aynı metin alt alta İKİ KEZ çıkıyor ve sayfa hatalı görünüyordu. Bu testler
    /// tarayıcıda gözle bakılmasaydı yazılmazdı; "yeşil test" sayfanın doğru göründüğünü
    /// kanıtlamaz.
    /// </remarks>
    [Fact]
    public void Adres_site_adiyla_ayniysa_tekrarlanmaz()
    {
        var html = StarterPage.Render("musteri.com", "musteri.com", 80);

        var kere = html.Split("musteri.com").Length - 1;
        Assert.Equal(2, kere); // yalnız <title> ve <h1>
        Assert.DoesNotContain("class=\"adres\"", html, StringComparison.Ordinal);
    }

    /// <summary>Adres FARKLIYSA gösterilir — bilgi kaybı olmamalı.</summary>
    [Fact]
    public void Adres_farkliysa_gosterilir()
    {
        var html = StarterPage.Render("Musteri Sitesi", "musteri.com", 8080);

        Assert.Contains("class=\"adres\"", html, StringComparison.Ordinal);
        Assert.Contains("musteri.com:8080", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// Host adı boşsa sayfa yine üretilir (bağlaması host adsız site).
    /// </summary>
    [Fact]
    public void Host_adi_bos_olsa_da_sayfa_uretilir()
    {
        var html = StarterPage.Render("musteri.com", "", 8080);

        Assert.Contains("localhost:8080", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// TÜRKÇE METİN DOĞRU YAZILIR — eski sayfanın asıl kusuru buydu.
    /// </summary>
    /// <remarks>
    /// Eski metin "yayinda", "Dosyalarinizi", "klasore", "baslangic", "degistirebilirsiniz" diyordu.
    /// Müşterinin alan adını ilk açtığında gördüğü sayfa buydu.
    /// </remarks>
    [Fact]
    public void Turkce_metin_dogru_yazilir()
    {
        var html = StarterPage.Render("musteri.com", "musteri.com", 80);

        Assert.Contains("Site yayında", html, StringComparison.Ordinal);
        Assert.Contains("yüklenmediği", html, StringComparison.Ordinal);
        Assert.Contains("Dosya Yöneticisi", html, StringComparison.Ordinal);

        // Eski, karaktersiz yazımlar geri gelmemeli.
        Assert.DoesNotContain("yayinda", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Dosyalarinizi", html, StringComparison.Ordinal);
        Assert.DoesNotContain("degistirebilirsiniz", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// Kullanıcıya SIRADAKİ ADIM söylenir — sayfanın var oluş sebebi.
    /// </summary>
    /// <remarks>
    /// Özellikle "bu index.html'i silin" adımı: yerinde bırakılırsa yüklenen gerçek sayfayı
    /// gölgeler ve kullanıcı "dosyalarımı yükledim ama site değişmedi" durumuna düşer.
    /// </remarks>
    [Fact]
    public void Siradaki_adimlar_yazilir()
    {
        var html = StarterPage.Render("musteri.com", "musteri.com", 80);

        Assert.Contains("index.html", html, StringComparison.Ordinal);
        Assert.Contains("FTP", html, StringComparison.Ordinal);
        Assert.Contains("gölgeler", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// ÜÇ YOL DA SUNULUR — Plesk'in varsayılan sayfasının izlediği desen.
    /// </summary>
    /// <remarks>
    /// Plesk'in belgeleri, yeni bir alan adının içeriğini üç yoldan birinin değiştirdiğini söylüyor:
    /// dosya yöneticisi, FTP ve otomatik uygulama kurucu. Sayfa yalnız dosya yüklemeyi anlatsaydı,
    /// panelin uygulama kurma özelliği yeni kullanıcı için görünmez kalırdı.
    /// </remarks>
    [Fact]
    public void Uc_yol_da_sunulur()
    {
        var html = StarterPage.Render("musteri.com", "musteri.com", 80);

        Assert.Contains("Dosya Yöneticisi", html, StringComparison.Ordinal);
        Assert.Contains("FTP", html, StringComparison.Ordinal);
        Assert.Contains("WordPress", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// "Bu dosyayı silin" uyarısı PROFİLİN GERÇEK dosyasını yazar.
    /// </summary>
    /// <remarks>
    /// Karma profilde sayfa <c>default.asp</c>, PHP profilinde <c>index.php</c> olarak yazılıyor.
    /// Uyarı her zaman "index.html" deseydi kullanıcı var olmayan bir dosyayı arar, gerçek yer
    /// tutucu yerinde kalır ve kendi sayfasını gölgelemeye devam ederdi.
    /// </remarks>
    [Theory]
    [InlineData("default.asp")]
    [InlineData("index.php")]
    [InlineData("Default.aspx")]
    public void Silinecek_dosya_adi_profile_gore_yazilir(string dosya)
    {
        var html = StarterPage.Render("musteri.com", "musteri.com", 80, new StarterPageOptions(DosyaAdi: dosya));

        Assert.Contains($"<strong>{dosya}</strong>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<strong>index.html</strong>", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// PANELİN İŞARETİ sayfada bulunur — kullanıcının dosyasıyla karışmaması için.
    /// </summary>
    /// <remarks>
    /// <c>SiteProvisioningService</c> bu işarete bakarak dosyanın panele mi kullanıcıya mı ait
    /// olduğunu ayırır. İşaret kaybolursa panel kendi yer tutucusunu kullanıcı içeriği sanar ve
    /// profil değişiminde yetim bırakır.
    /// </remarks>
    [Fact]
    public void Panel_isareti_sayfada_bulunur()
    {
        var html = StarterPage.Render("musteri.com", "musteri.com", 80);

        Assert.Contains(StarterPage.Marker, html, StringComparison.Ordinal);
    }

    /// <summary>
    /// SUNUCUDA ÜRETİLEN satır KAÇIRILMAZ — sayfanın çalıştığının kanıtıdır.
    /// </summary>
    /// <remarks>
    /// Classic ASP'nin <c>&lt;%= Now() %&gt;</c> ifadesi kaçırılsaydı sayfada düz metin olarak
    /// görünür ve sunucunun gerçekten çalıştığını kanıtlamazdı. Bu HTML paneldendir, kullanıcı
    /// girdisi değildir.
    /// </remarks>
    [Fact]
    public void Sunucuda_uretilen_satir_kacirilmaz()
    {
        var html = StarterPage.Render("musteri.com", "musteri.com", 80,
            new StarterPageOptions(DinamikSatir: "<p class=\"row\"><%= Now() %></p>"));

        Assert.Contains("<%= Now() %>", html, StringComparison.Ordinal);
    }

    /// <summary>Profil rozeti ve açıklaması KAÇIRILIR (panel metni olsa da disiplin bozulmaz).</summary>
    [Fact]
    public void Rozet_ve_aciklama_kacirilir()
    {
        var html = StarterPage.Render("musteri.com", "musteri.com", 80,
            new StarterPageOptions(Rozet: "<b>x</b>", Aciklama: "<i>y</i>"));

        Assert.DoesNotContain("<b>x</b>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<i>y</i>", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// GÖZLE BAKMAK İÇİN: sayfanın gerçek çıktısını diske yazar.
    /// </summary>
    /// <remarks>
    /// Yukarıdaki testler "ne göstermemeli" sorusunu ölçer; "nasıl görünüyor" sorusunu bir test
    /// ölçemez. Bu dosya, değişikliği yapanın sayfayı tarayıcıda AÇIP bakması içindir.
    /// </remarks>
    [Fact]
    public void Onizleme_diske_yazilir()
    {
        var sade = Path.Combine(Path.GetTempPath(), "adapanel-baslangic-onizleme.html");
        File.WriteAllText(sade, StarterPage.Render("videotools.com.tr", "videotools.com.tr", 80));

        // KARMA PROFIL: kullanicinin canlida gordugu sayfa budur (default.asp). Onizlemede
        // <%= Now() %> calismayacagi icin ornek bir saat konuyor; olculen sey DUZENDIR.
        var karma = Path.Combine(Path.GetTempPath(), "adapanel-baslangic-onizleme-karma.html");
        File.WriteAllText(karma, StarterPage.Render("videotools.com.tr", "videotools.com.tr", 80,
            new StarterPageOptions(
                DosyaAdi: "default.asp",
                Rozet: "ASP · ASP.NET · PHP",
                Aciklama: "Bu sunucu Classic ASP, ASP.NET ve PHP çalıştırmaya hazır.",
                DinamikSatir: "<p class=\"row\"><span class=\"k\">Sunucu saati</span>"
                    + "<span class=\"v\">29.09.2026 18:07:42</span></p>",
                EkBaglanti: "<p class=\"links\"><a href=\"php-info.php\">PHP doğrulaması →</a></p>")));

        Assert.True(File.Exists(sade));
        Assert.True(File.Exists(karma));
    }
}
