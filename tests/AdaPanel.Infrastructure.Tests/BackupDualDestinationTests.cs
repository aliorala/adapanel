using AdaPanel.Core.Dtos.Backup;
using AdaPanel.Infrastructure.Automation;
using AdaPanel.Infrastructure.Backup;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Otomatik yedeğin iki sözü: (1) yedek HER ZAMAN yerel diske alınır, uzak hedef bunun ÜSTÜNE
/// gelir; (2) bir sitenin her şeyi TEK arşivde, içinde ayrılmış hâlde durur.
/// </summary>
public sealed class BackupDualDestinationTests
{
    // ---------------------------------------------------------------------
    // (2) Site ↔ veritabanı eşleşmesi
    // ---------------------------------------------------------------------

    [Fact]
    public void Sitenin_veritabanlari_o_sitenin_arsivine_girer()
    {
        var sahiplik = new List<(string, string, Guid, string)>
        {
            ("mysql", "magaza_db", Guid.NewGuid(), "magaza.com"),
            ("mysql", "magaza_log", Guid.NewGuid(), "magaza.com"),
            ("postgresql", "blog_db", Guid.NewGuid(), "blog.com")
        };

        var grup = AutomationService.GroupDatabasesBySite(sahiplik);

        Assert.Equal(2, grup["magaza.com"].Count);
        Assert.Contains(grup["magaza.com"], d => d.DatabaseName == "magaza_db" && d.Engine == "mysql");
        Assert.Contains(grup["magaza.com"], d => d.DatabaseName == "magaza_log");
        Assert.Single(grup["blog.com"]);
    }

    [Fact]
    public void Hicbir_siteye_bagli_OLMAYAN_veritabani_hicbir_site_arsivine_girmez()
    {
        // Site adı boş olan kayıt = panel dışında açılmış / siteye bağlanmamış veritabanı.
        // Bunlar KENDİ BAŞLARINA yedeklenmeye devam eder; bir sitenin arşivine sızmamalıdır.
        var sahiplik = new List<(string, string, Guid, string)>
        {
            ("mysql", "sahipsiz_db", Guid.NewGuid(), ""),
            ("mysql", "site_db", Guid.NewGuid(), "site.com")
        };

        var grup = AutomationService.GroupDatabasesBySite(sahiplik);

        Assert.Single(grup);
        Assert.DoesNotContain(grup.SelectMany(x => x.Value), d => d.DatabaseName == "sahipsiz_db");
    }

    [Fact]
    public void Site_adi_buyuk_kucuk_harf_farkiyla_da_eslesir()
    {
        // IIS site adları büyük/küçük harfe duyarsızdır; eşleşme kaçarsa o sitenin veritabanı
        // arşive HİÇ girmez ve kimse fark etmez.
        var sahiplik = new List<(string, string, Guid, string)>
        {
            ("mysql", "db1", Guid.NewGuid(), "Ornek.COM")
        };

        var grup = AutomationService.GroupDatabasesBySite(sahiplik);

        Assert.True(grup.ContainsKey("ornek.com"));
        Assert.True(grup.ContainsKey("ORNEK.com"));
    }

    [Fact]
    public void Bozuk_kayitlar_sessizce_atlanir_gecerliler_KALIR()
    {
        // Tek bozuk kayıt yüzünden bütün grubun düşmesi, o sitenin veritabanının sessizce
        // yedeksiz kalması demek olurdu.
        var sahiplik = new List<(string, string, Guid, string)>
        {
            ("", "adsiz_motor", Guid.NewGuid(), "site.com"),
            ("mysql", "", Guid.NewGuid(), "site.com"),
            ("mysql", "gecerli_db", Guid.NewGuid(), "site.com")
        };

        var grup = AutomationService.GroupDatabasesBySite(sahiplik);

        Assert.Single(grup["site.com"]);
        Assert.Equal("gecerli_db", grup["site.com"][0].DatabaseName);
    }

    // ---------------------------------------------------------------------
    // (1) Uzak hedef başarısızsa yerel kopya KAYBOLMAZ
    // ---------------------------------------------------------------------

    [Fact]
    public void Uzak_hedef_basarisizsa_durum_BASARILI_sayilmaz()
    {
        // Yedek yerel diskte VARDIR ama off-site kopyası YOKTUR. İkisini aynı kefeye koymak,
        // kullanıcının olmayan bir off-site kopyaya güvenmesi demektir -- bu, felaket anında
        // öğrenilecek en kötü şeydir.
        var durum = BackupService.DescribeDestinationStatus(
            remoteUri: string.Empty,
            remoteFailure: "227 Entering Passive Mode -- baglanti reddedildi");

        Assert.Equal("created_remote_failed", durum);
    }

    [Fact]
    public void Iki_hedef_de_tamamsa_durum_senkron_bildirilir()
    {
        var durum = BackupService.DescribeDestinationStatus(
            remoteUri: "ftp://yedek.ornek.com/adapanel/site-2026.zip",
            remoteFailure: null);

        Assert.Equal("created_remote_synced", durum);
    }

    [Fact]
    public void Yalniz_yerel_istendiginde_uzak_hata_uydurulmaz()
    {
        var durum = BackupService.DescribeDestinationStatus(remoteUri: string.Empty, remoteFailure: null);

        Assert.Equal("created", durum);
    }

    [Fact]
    public void Uzak_adres_dolu_olsa_bile_HATA_varsa_basarisiz_kazanir()
    {
        // Kısmi yükleme sonrası hata: adres oluşmuş olabilir ama dosya sağlam DEĞİLDİR.
        // "Adres var, demek ki gitti" varsayımı tam da bozuk yedeği başarılı göstermenin yoludur.
        var durum = BackupService.DescribeDestinationStatus(
            remoteUri: "ftp://yedek.ornek.com/adapanel/yarim.zip",
            remoteFailure: "baglanti yarida koptu");

        Assert.Equal("created_remote_failed", durum);
    }
}
