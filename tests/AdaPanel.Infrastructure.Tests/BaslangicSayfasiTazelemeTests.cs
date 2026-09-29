using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Infrastructure.Iis;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Paneli güncelleyen kullanıcı, VAR OLAN sitesinde de yeni karşılama sayfasını görür mü?
/// </summary>
/// <remarks>
/// <para>
/// <b>KULLANICI CANLIDA İKİ KEZ BİLDİRDİ.</b> Karşılama sayfası yalnız site OLUŞTURULURKEN diske
/// yazılıyordu; panel güncellemesi diskteki dosyaya dokunmuyordu. Kullanıcı sonunda
/// <i>"o zaman sileyim tekrar eklerim"</i> dedi — panel, çalışan bir siteyi silip yeniden kurmaya
/// zorluyordu.
/// </para>
/// <para>
/// <b>BU TESTLERİN ASIL KONUSU TAZELEME DEĞİL, TAZELEMENİN SINIRLARIDIR.</b> Bir dosyanın üzerine
/// yazmak geri alınamaz; yanlış dosyaya dokunmak müşterinin sitesini yok etmek demektir. Üç sınır
/// ayrı ayrı ölçülüyor: kullanıcının dosyasına dokunulmaz, olmayan dosya oluşturulmaz, ve profil
/// diskten okunur.
/// </para>
/// </remarks>
public sealed class BaslangicSayfasiTazelemeTests
{
    /// <summary>PANELİN kendi yer tutucusu tazelenir.</summary>
    [Fact]
    public void Panelin_kendi_yer_tutucusu_tazelenir()
    {
        using var alan = new SiteAlani();
        alan.Yaz("default.asp", $"<!-- {StarterPage.Marker} --><html>ESKI SAYFA</html>");

        var yenilenen = StarterPageRefresh.Refresh(alan.Site);

        Assert.Equal(["default.asp"], yenilenen);
        var icerik = alan.Oku("default.asp");
        Assert.DoesNotContain("ESKI SAYFA", icerik, StringComparison.Ordinal);
        Assert.Contains("Sırada ne var?", icerik, StringComparison.Ordinal);
        Assert.Contains(StarterPage.Marker, icerik, StringComparison.Ordinal);
    }

    /// <summary>
    /// KULLANICININ DOSYASINA ASLA DOKUNULMAZ — bu sınıfın en önemli sınırı.
    /// </summary>
    /// <remarks>
    /// İşaret yoksa dosya kullanıcınındır. Kullanıcı panelin sayfasını alıp kendi sayfası hâline
    /// getirmiş de olabilir; üzerine yazmak onun emeğini geri alınamaz biçimde siler.
    /// </remarks>
    [Theory]
    [InlineData("default.asp")]
    [InlineData("index.php")]
    [InlineData("index.html")]
    [InlineData("Default.aspx")]
    public void Kullanicinin_dosyasina_dokunulmaz(string dosya)
    {
        using var alan = new SiteAlani();
        const string musteriIcerigi = "<html>MUSTERININ KENDI SITESI</html>";
        alan.Yaz(dosya, musteriIcerigi);

        var yenilenen = StarterPageRefresh.Refresh(alan.Site);

        Assert.Empty(yenilenen);
        Assert.Equal(musteriIcerigi, alan.Oku(dosya));
    }

    /// <summary>
    /// OLMAYAN DOSYA OLUŞTURULMAZ — "tazele" ile "eksikse yaz" aynı şey değildir.
    /// </summary>
    /// <remarks>
    /// İçeriği olan ama açılış belgesi bulunmayan bir sitede (ör. yalnız bir API yayınlayan site)
    /// yeni bir yer tutucu yazmak, çalışan siteyi birden yer tutucu gösterir hâle getirirdi.
    /// </remarks>
    [Fact]
    public void Olmayan_dosya_olusturulmaz()
    {
        using var alan = new SiteAlani();
        alan.Yaz("api.json", "{}");

        var yenilenen = StarterPageRefresh.Refresh(alan.Site);

        Assert.Empty(yenilenen);
        Assert.False(File.Exists(Path.Combine(alan.Kok, "default.asp")));
        Assert.False(File.Exists(Path.Combine(alan.Kok, "index.html")));
        Assert.False(File.Exists(Path.Combine(alan.Kok, "index.php")));
    }

    /// <summary>
    /// PROFİL DİSKTEN OKUNUR: <c>php-info.php</c> varsa karma, yoksa klasik ASP.
    /// </summary>
    /// <remarks>
    /// Profili ayrıca servislerden sormak, iki kaynağın ayrışabileceği üçüncü bir yer açardı.
    /// Hangi dosyanın var olduğu sitenin profilini zaten söyler.
    /// </remarks>
    [Fact]
    public void Karma_profil_php_dogrulama_baglantisi_tasir()
    {
        using var alan = new SiteAlani();
        alan.Yaz("default.asp", $"<!-- {StarterPage.Marker} -->eski");
        alan.Yaz("php-info.php", $"<?php /* {StarterPage.Marker} */");

        StarterPageRefresh.Refresh(alan.Site);

        var icerik = alan.Oku("default.asp");
        Assert.Contains("php-info.php", icerik, StringComparison.Ordinal);
        // ROZET KACIRILMIS HALDE ARANIR: HtmlEncode, ASCII disi ayraci (·) &#183; yapar.
        // Ham dizeyi aramak testi yanlis yere goturur; tarayicida dogru gorunur.
        Assert.Contains(System.Net.WebUtility.HtmlEncode("ASP · ASP.NET · PHP"), icerik, StringComparison.Ordinal);
    }

    /// <summary>Klasik ASP profilinde PHP bağlantısı YAZILMAZ — olmayan sayfaya yönlendirmeyiz.</summary>
    [Fact]
    public void Klasik_asp_profilinde_php_baglantisi_yazilmaz()
    {
        using var alan = new SiteAlani();
        alan.Yaz("default.asp", $"<!-- {StarterPage.Marker} -->eski");

        StarterPageRefresh.Refresh(alan.Site);

        var icerik = alan.Oku("default.asp");
        Assert.DoesNotContain("php-info.php", icerik, StringComparison.Ordinal);
        Assert.Contains("Classic ASP", icerik, StringComparison.Ordinal);
    }

    /// <summary>
    /// ASP.NET sayfasında <c>&lt;%@ Page %&gt;</c> yönergesi İLK satırda kalır.
    /// </summary>
    /// <remarks>
    /// Yönerge ilk satırda değilse ASP.NET sayfayı derleyemez ve site 500 döner. Tazeleme,
    /// çalışan bir sayfayı bozmamalı.
    /// </remarks>
    [Fact]
    public void Aspx_yonergesi_ilk_satirda_kalir()
    {
        using var alan = new SiteAlani();
        alan.Yaz("Default.aspx", $"<%@ Page Language=\"C#\" %>\n<!-- {StarterPage.Marker} -->eski");

        StarterPageRefresh.Refresh(alan.Site);

        var satirlar = alan.Oku("Default.aspx").Split('\n');
        Assert.StartsWith("<%@ Page", satirlar[0], StringComparison.Ordinal);
    }

    /// <summary>Birden çok yer tutucu varsa hepsi tazelenir ve HEPSİ raporlanır.</summary>
    [Fact]
    public void Birden_cok_yer_tutucu_hepsi_tazelenir()
    {
        using var alan = new SiteAlani();
        alan.Yaz("default.asp", $"<!-- {StarterPage.Marker} -->eski");
        alan.Yaz("index.html", $"<!-- {StarterPage.Marker} -->eski");

        var yenilenen = StarterPageRefresh.Refresh(alan.Site);

        Assert.Equal(2, yenilenen.Count);
        Assert.Contains("default.asp", yenilenen);
        Assert.Contains("index.html", yenilenen);
    }

    /// <summary>
    /// Klasör yoksa sessizce geçilir — "tazelenecek bir şey yok" bir ARIZA değildir.
    /// </summary>
    [Fact]
    public void Klasor_yoksa_hata_verilmez()
    {
        using var alan = new SiteAlani();
        var yokSite = new SiteDto
        {
            Name = "videotools.com.tr",
            PhysicalPath = Path.Combine(alan.Kok, "hic-olmayan")
        };

        Assert.Empty(StarterPageRefresh.Refresh(yokSite));
    }

    /// <summary>Silinecek dosya adı, TAZELENEN dosyanın adıyla aynı olur.</summary>
    /// <remarks>
    /// Yanlış ad, kullanıcıyı var olmayan bir dosyayı aramaya gönderir; gerçek yer tutucu yerinde
    /// kalır ve kendi sayfasını gölgelemeye devam eder.
    /// </remarks>
    [Fact]
    public void Uyari_tazelenen_dosyanin_adini_yazar()
    {
        using var alan = new SiteAlani();
        alan.Yaz("index.php", $"<?php /* {StarterPage.Marker} */");

        StarterPageRefresh.Refresh(alan.Site);

        Assert.Contains("<strong>index.php</strong>", alan.Oku("index.php"), StringComparison.Ordinal);
    }

    private sealed class SiteAlani : IDisposable
    {
        public SiteAlani()
        {
            Kok = Path.Combine(Path.GetTempPath(), $"adapanel-tazeleme-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Kok);
            Site = new SiteDto
            {
                Name = "videotools.com.tr",
                PhysicalPath = Kok,
                Bindings =
                [
                    new SiteBindingDto { HostName = "videotools.com.tr", Port = 80, Protocol = "http" }
                ]
            };
        }

        public string Kok { get; }

        public SiteDto Site { get; }

        public void Yaz(string ad, string icerik) => File.WriteAllText(Path.Combine(Kok, ad), icerik);

        public string Oku(string ad) => File.ReadAllText(Path.Combine(Kok, ad));

        // Geçici klasör BİLEREK silinmiyor: bu depoda silme yasağı var.
        public void Dispose()
        {
        }
    }
}
