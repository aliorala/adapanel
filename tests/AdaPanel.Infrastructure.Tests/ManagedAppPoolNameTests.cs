using System.Text.RegularExpressions;
using AdaPanel.Infrastructure.Iis;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// HAVUZ ADI TEK YERDEN URETILIR — iki taraf ayni adi soylemek ZORUNDA.
///
/// <para>
/// <b>Neden var — olculmus, sessiz bozulan sozlesme.</b> Havuz adi iki ayri yerde
/// uretiliyordu: havuzu GERCEKTEN kuran <c>WebAdministrationSiteService</c> site adini once
/// temizliyor (<c>[^A-Za-z0-9._-]</c> -> <c>-</c>), <c>WafService.IsolateAppPoolAsync</c> ise
/// HAM ekliyordu. Site adinin karakter kumesi sinirli DEGIL (yalniz kirpma + aygit adi denetimi
/// var), yani "Musteri Sitesi" gibi bir adda iki taraf FARKLI ad uretiyordu.
/// </para>
/// <para>
/// <b>Sonucu ve neden gorunmedigi:</b> izolasyon var olan havuzu bulamayip IKINCI bir havuz
/// aciyor; sitenin ACL'leri eski havuzun kimligine verildigi icin BOSA DUSUYOR. Panel yine de
/// "izole" diyordu, cunku olcum yalnizca ONEKE bakiyor — iki farkli havuz da ayni oneki tasir.
/// </para>
/// <para>
/// Bu, ayni surumde kapatilan UCUNCU ayni hata: FTP ev klasoru ve site yazma izni de
/// "panel bir ad/yol uretip dis sisteme veriyor, o sistemin aynisini bekledigini varsayiyor"
/// kalibindaydi.
/// </para>
/// </summary>
public sealed class ManagedAppPoolNameTests
{
    /// <summary>
    /// KAPATILAN HATANIN TA KENDISI: bosluklu/Turkce harfli ad iki tarafta da AYNI cikmali.
    /// </summary>
    [Theory]
    [InlineData("Musteri Sitesi", "AdaPanel.Musteri-Sitesi")]
    [InlineData("müşteri.com", "AdaPanel.m-teri.com")]
    [InlineData("site adı", "AdaPanel.site-ad-")]
    [InlineData("a b  c", "AdaPanel.a-b-c")]
    public void Ad_TEMIZLENEREK_uretilir(string siteAdi, string beklenen)
    {
        // Kucuk/buyuk harf farki isletim sistemine gore degisebilir; olculen sey TEMIZLEMEnin
        // yapilip yapilmadigi, harf donusumu degil.
        Assert.Equal(beklenen, ManagedAppPoolName.Build(siteAdi), ignoreCase: true);
    }

    [Fact]
    public void Zaten_temiz_ad_DEGISTIRILMEZ()
    {
        Assert.Equal("AdaPanel.ornek.com", ManagedAppPoolName.Build("ornek.com"));
        Assert.Equal("AdaPanel.alt-alan_1.ornek.com", ManagedAppPoolName.Build("alt-alan_1.ornek.com"));
    }

    [Fact]
    public void Uretilen_ad_HER_ZAMAN_yonetilen_sayilir()
    {
        // Uretim ile olcumun ayrismasi, panelin kendi actigi havuzu tanimamasi demekti.
        Assert.True(ManagedAppPoolName.IsManaged(ManagedAppPoolName.Build("Musteri Sitesi")));
        Assert.False(ManagedAppPoolName.IsManaged("DefaultAppPool"));
        Assert.False(ManagedAppPoolName.IsManaged(null));
    }

    /// <summary>
    /// URETILEN AD IIS ICIN GECERLI OLMALI — temizleme kumesinden kacan karakter kalmamali.
    /// </summary>
    /// <remarks>
    /// Alt sinir kendini denetler: ornek listesi bosalirsa test sessizce yesil kalmasin.
    /// </remarks>
    [Fact]
    public void Uretilen_adda_gecersiz_karakter_kalmaz()
    {
        string[] zorAdlar =
        [
            "Müşteri Sitesi", "site adı", "a/b\\c", "ör#nek!.com", "boşluk sonu ",
            "tırnak\"lı", "yüzde%li", "artı+lı", "ünlem!", "parantez(li)"
        ];

        Assert.True(zorAdlar.Length >= 8, "ornek listesi daralmis: test olcum yapmiyor olabilir.");

        foreach (var ad in zorAdlar)
        {
            var havuz = ManagedAppPoolName.Build(ad);
            Assert.Matches("^AdaPanel\\.[A-Za-z0-9._-]*$", havuz);
        }
    }

    /// <summary>
    /// TEKRAR GERI GELMESIN: onek ve temizleme, kaynakta BASKA bir yerde yeniden yazilmasin.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Kapsam BILEREK dar: yalnizca havuz adiyla ilgili iki dosyaya bakilir. Kirmizi yanarsa
    /// anlami, birinin adi yeniden ELLE uretmeye baslamasidir — ki bu hatanin ta kendisiydi.
    /// </para>
    /// <para>
    /// <b>DIZGENIN KENDISI YASAKLANMAZ, SEKLI YASAKLANIR.</b> Testin ilk hali
    /// <c>"AdaPanel."</c> dizgesini ariyordu ve HAKLI OLARAK kirmizi yandi: ayni dosyada
    /// <c>InternalSiteNamePrefix</c> diye AYRI bir sabit var ve o havuz degil panelin kendi
    /// SITE adlarini uretiyor (<c>AdaPanel.Webmail</c>, <c>AdaPanel.WebDb.PhpMyAdmin</c>). Ayni
    /// metin iki farkli kavrama hizmet ediyor; metni yasaklamak mesru kullanimi da vururdu ve
    /// nobetci "yanlis yerde kirmizi" yakan bir seye donerdi. Yasak, hatanin SEKLINE konur:
    /// bir havuz adi oneki sabiti yeniden tanimlamak ya da oneki elle bir adla BIRLESTIRMEK.
    /// </para>
    /// <para>
    /// Yorumlar elenerek taranir: bu dosyalarin yorumlarinda eski kod ORNEK olarak geciyor ve
    /// tarama onu gercek kod sanmamali.
    /// </para>
    /// </remarks>
    [Fact]
    public void Havuz_adi_baska_dosyada_ELLE_uretilmiyor()
    {
        var kok = KaynakKoku();
        string[] dosyalar =
        [
            Path.Combine(kok, "src", "AdaPanel.Infrastructure", "Waf", "WafService.cs"),
            Path.Combine(kok, "src", "AdaPanel.Infrastructure", "Iis", "WebAdministrationSiteService.cs")
        ];

        foreach (var yol in dosyalar)
        {
            var kaynak = YorumlariSil(File.ReadAllText(yol));
            var ad = Path.GetFileName(yol);

            // Kendi kendini denetleyen on kosul: devir gercekten yapildiysa dosya tek kaynagi anar.
            Assert.True(
                kaynak.Contains("ManagedAppPoolName", StringComparison.Ordinal),
                $"{ad} artik ManagedAppPoolName'i anmiyor: devir geri mi alindi?");

            // (a) HAVUZ oneki icin ikinci bir sabit tanimlanmasin. "SiteNamePrefix" gibi baska
            //     kavramlar bilerek disarida: onlar havuz adi degil.
            Assert.False(
                Regex.IsMatch(kaynak, @"\bconst\s+string\s+\w*PoolPrefix\s*="),
                $"{ad} icinde havuz oneki icin IKINCI bir sabit tanimlanmis; tek kaynak ManagedAppPoolName.");

            // (b) Onek elle bir adla BIRLESTIRILMESIN -- kapatilan hatanin tam sekli buydu
            //     (`ManagedPoolPrefix + site.Name`, temizleme atlanarak).
            Assert.False(
                Regex.IsMatch(kaynak, @"""AdaPanel\.""\s*\+"),
                $"{ad} icinde havuz adi ELLE birlestiriliyor; ManagedAppPoolName.Build kullanin.");
        }
    }

    [Fact]
    public void Yorum_temizleyici_gercekten_calisiyor()
    {
        Assert.DoesNotContain("\"AdaPanel.\"", YorumlariSil("// eski: const P = \"AdaPanel.\";"), StringComparison.Ordinal);
        Assert.DoesNotContain("\"AdaPanel.\"", YorumlariSil("/* const P = \"AdaPanel.\"; */"), StringComparison.Ordinal);
        Assert.Contains("\"AdaPanel.\"", YorumlariSil("const P = \"AdaPanel.\";"), StringComparison.Ordinal);
    }

    /// <summary>
    /// Yukaridaki iki yasagin GERCEKTEN yakaladiginin kaniti — sentetik girdiyle.
    /// </summary>
    /// <remarks>
    /// Tarama bugun iki dosya da temiz oldugu icin geciyor; desenler hic eslesmese de gecerdi.
    /// Kapsamin kendisi olculmeli, yoksa koruma sessizce bosa duser.
    /// </remarks>
    [Fact]
    public void Yasak_desenleri_gercekten_yakaliyor()
    {
        Assert.Matches(@"\bconst\s+string\s+\w*PoolPrefix\s*=", "    private const string ManagedPoolPrefix = \"AdaPanel.\";");
        Assert.Matches(@"""AdaPanel\.""\s*\+", "var p = \"AdaPanel.\" + site.Name;");

        // Mesru kullanim VURULMAMALI: site adi oneki bu yasaklarin hicbirine takilmaz.
        Assert.DoesNotMatch(@"\bconst\s+string\s+\w*PoolPrefix\s*=", "    private const string InternalSiteNamePrefix = \"AdaPanel.\";");
        Assert.DoesNotMatch(@"""AdaPanel\.""\s*\+", "    private const string InternalSiteNamePrefix = \"AdaPanel.\";");
    }

    private static string YorumlariSil(string kaynak)
    {
        var metin = Regex.Replace(kaynak, @"/\*.*?\*/", " ", RegexOptions.Singleline);

        var satirlar = metin.Split('\n').Select(satir =>
        {
            var tirnak = 0;
            for (var i = 0; i + 1 < satir.Length; i++)
            {
                if (satir[i] is '"')
                {
                    tirnak++;
                    continue;
                }

                if (satir[i] == '/' && satir[i + 1] == '/' && tirnak % 2 == 0)
                {
                    return satir[..i];
                }
            }

            return satir;
        });

        return string.Join("\n", satirlar);
    }

    private static string KaynakKoku()
    {
        var yol = AppContext.BaseDirectory;
        while (yol is not null && !File.Exists(Path.Combine(yol, "AdaPanel.sln")))
        {
            yol = Path.GetDirectoryName(yol);
        }

        Assert.NotNull(yol);
        return yol!;
    }
}
