using System.Net;
using System.Net.Sockets;
using System.Text;
using AdaPanel.Core.Dtos.Backup;
using AdaPanel.Core.Dtos.Settings;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Backup;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Support;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// UZAK hedefte gun/kategori/kaynak duzeni.
/// </summary>
/// <remarks>
/// OLCULEN DURUM (0.5.5, kullanicinin canli sunucusu): yedekler diskte
/// <c>backups\2026-08-17\sites\...</c> olarak gun gun klasorlenirken UZAK hedefte (FTP/S3/WebDAV)
/// klasor yapisi HIC olusmuyordu -- butun dosyalar kokun altina duz iniyordu. Kok neden: gun
/// klasoru yalnizca YEREL yola eklenmisti; uzak yukleme dosya adindan baska bir sey bilmiyordu.
/// <para>
/// Bu testler duzeltmeyi degil, KIRILGAN NOKTALARINI korur:
/// (1) uzak yol yerelle AYNI gun/kategori/KAYNAK klasorunu icerir (tek kaynak),
/// (2) FTP'de "dizin zaten var" hatasi (ikinci gecenin NORMALI) yedegi BOZMAZ,
/// (3) "izin yok" YUTULMAZ -- kok sebep kullanicinin gordugu kayda gecer,
/// (4) S3'te MKD/MKCOL DENENMEZ (dizin kavrami yoktur), yalnizca anahtar oneki kullanilir,
/// (5) uzak silme (budama) KAYITTAKI yolu kullanir; ESKI duzende (kokun altinda duz) yuklenmis
///     yedekler de silinmeye devam eder,
/// (6) uzak yukleme basarisiz olsa bile YEREL kopya kaydedilir (0.5.1 davranisi),
/// (7) kimlik bilgisi RemoteUri'ye SIZMAZ.
/// </para>
/// </remarks>
public sealed class BackupRemoteLayoutTests
{
    /// <summary>Testlerdeki sitenin adi; 0.6.1'den beri UZAK yolda da KAYNAK klasoru olarak gecer.</summary>
    private const string Site = "shop.example.com";

    // =======================================================================================
    // 1) TEK KAYNAK: gun/kategori/kaynak karari
    // =======================================================================================

    [Fact]
    public void Yerel_klasor_ve_uzak_yol_AYNI_karardan_turer()
    {
        var an = new DateTime(2026, 8, 17, 23, 59, 30, DateTimeKind.Local);
        var yer = BackupLayout.Describe(@"C:\yedek", an, "sites", "ornek.com");

        Assert.Equal("2026-08-17", yer.Day);
        Assert.Equal("sites", yer.Category);
        Assert.Equal("ornek.com", yer.Source);
        Assert.Equal(Path.Combine(@"C:\yedek", "2026-08-17", "sites", "ornek.com"), yer.LocalDirectory);

        // Uzak klasor seviyeleri yerel yolun SIRASIYLA aynisi: ikisi ayri hesaplansaydi gece
        // yarisinda bir gun ayrisir ve ayni yedek iki farkli gunde gorunurdu.
        Assert.Equal(new[] { "2026-08-17", "sites", "ornek.com" }, yer.RemoteFolders);
    }

    [Fact]
    public void Kategori_yol_kacisi_iceremez()
    {
        // Kategori bugun sabit bir listeden geliyor; ama bu deger artik UZAK anahtara da giriyor.
        // Icinde ayirici/nokta tasiyan bir deger hem yerel yolu hem uzak anahtari hedefin disina
        // kacirabilirdi.
        var yer = BackupLayout.Describe(@"C:\yedek", new DateTime(2026, 8, 17), "../../etc");

        Assert.DoesNotContain("..", yer.Category, StringComparison.Ordinal);
        Assert.DoesNotContain("/", yer.Category, StringComparison.Ordinal);
        Assert.DoesNotContain(@"\", yer.Category, StringComparison.Ordinal);
        Assert.Equal(Path.Combine(@"C:\yedek", "2026-08-17", yer.Category), yer.LocalDirectory);
    }

    // =======================================================================================
    // 2) FTP -- gercek protokol (yerel taklit sunucu)
    // =======================================================================================

    [Fact]
    public async Task Ftp_yedegi_gun_ve_kategori_klasorune_yuklenir()
    {
        using var alan = new GeciciAlan();
        await using var sunucu = SahteFtpSunucusu.Baslat();

        var servis = alan.ServisKur();
        var kayit = await servis.BackupSiteAsync(SiteIstegi(sunucu.TabanAdres("yedekler")), alan.Iptal);

        var gun = alan.YerelGun(kayit);
        Assert.Equal("created_remote_synced", kayit.Status);
        Assert.Equal($"ftp://127.0.0.1:{sunucu.Port}/yedekler/{gun}/sites/{Site}/{kayit.Name}", kayit.RemoteUri);

        // Dosya GERCEKTEN o yola indi (sunucu ne aldigini soyluyor) ve bos degil.
        Assert.True(sunucu.Dosyalar.ContainsKey($"/yedekler/{gun}/sites/{Site}/{kayit.Name}"));
        Assert.True(sunucu.Dosyalar[$"/yedekler/{gun}/sites/{Site}/{kayit.Name}"].Length > 0);

        // ARA DIZINLER KENDILIGINDEN OLUSMAZ: her seviye icin MKD gerekir, YUKARIDAN ASAGIYA.
        Assert.Equal(
            new[] { "/yedekler", $"/yedekler/{gun}", $"/yedekler/{gun}/sites", $"/yedekler/{gun}/sites/{Site}" },
            sunucu.MkdYollari);
    }

    [Fact]
    public async Task Ftp_dizin_ZATEN_VARSA_ikinci_yedek_de_basarili_olur()
    {
        // IKINCI GECENIN NORMALI: dizinler dun olustu, bugun MKD 550 ("zaten var") donuyor.
        // Bu hatayi yedegin hatasi saymak, ilk geceden sonra HER GECE "uzak hedefe
        // gonderilemedi" demek olurdu.
        using var alan = new GeciciAlan();
        await using var sunucu = SahteFtpSunucusu.Baslat();

        var servis = alan.ServisKur();
        var birinci = await servis.BackupSiteAsync(SiteIstegi(sunucu.TabanAdres("yedekler")), alan.Iptal);
        Assert.Equal("created_remote_synced", birinci.Status);
        sunucu.MkdSonuclari.Clear();
        sunucu.Dosyalar.Clear();

        var ikinci = await servis.BackupSiteAsync(SiteIstegi(sunucu.TabanAdres("yedekler")), alan.Iptal);

        // Sunucu gercekten "zaten var" dedi (testin kendisi de dogrulanmis olsun).
        Assert.NotEmpty(sunucu.MkdSonuclari);
        Assert.All(sunucu.MkdSonuclari, sonuc => Assert.StartsWith("550", sonuc, StringComparison.Ordinal));

        Assert.Equal("created_remote_synced", ikinci.Status);
        Assert.DoesNotContain(ikinci.Warnings, uyari => uyari.MessageKey == "backup.remote_upload_failed");
        Assert.Contains($"/{alan.YerelGun(ikinci)}/sites/{Site}/", ikinci.RemoteUri, StringComparison.Ordinal);

        // Dosya GERCEKTEN gitti: "durum tamam" demek yetmez, sunucuda karsiligi olmali.
        Assert.Contains(
            sunucu.Dosyalar.Keys,
            yol => yol.EndsWith($"/{alan.YerelGun(ikinci)}/sites/{Site}/{ikinci.Name}", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Ftp_dizin_izni_YOKSA_sebep_yutulmaz_ama_yerel_kopya_kalir()
    {
        using var alan = new GeciciAlan();
        await using var sunucu = SahteFtpSunucusu.Baslat();
        sunucu.MkdIzinli = false; // chroot'lu hesap: yazma var, klasor acma YOK

        var servis = alan.ServisKur();
        var kayit = await servis.BackupSiteAsync(SiteIstegi(sunucu.TabanAdres("yedekler")), alan.Iptal);

        // (a) SESSIZ BASARI YOK.
        Assert.Equal("created_remote_failed", kayit.Status);

        // (b) KOK SEBEP kayitta: "izin yok" cevabi yutulmadi.
        var uyari = Assert.Single(kayit.Warnings, u => u.MessageKey == "backup.remote_upload_failed");
        Assert.Contains("Permission denied", uyari.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("MKD", uyari.Detail, StringComparison.Ordinal);

        // (c) YEREL KOPYA KAYBOLMAZ (0.5.1'de eklenen davranis): yedek diskte ve listede durur.
        Assert.True(File.Exists(kayit.Path));
        Assert.Contains(await servis.GetBackupsAsync(alan.Iptal), item => item.Id == kayit.Id);
    }

    [Fact]
    public async Task Ftp_kimlik_bilgisi_kayda_SIZMAZ()
    {
        using var alan = new GeciciAlan();
        await using var sunucu = SahteFtpSunucusu.Baslat();

        var servis = alan.ServisKur();
        var istek = SiteIstegi(sunucu.TabanAdres("yedekler"));
        istek.RemoteUserName = "yedekci";
        istek.RemotePassword = "Gizli-Parola-1!";

        var kayit = await servis.BackupSiteAsync(istek, alan.Iptal);

        Assert.Equal("created_remote_synced", kayit.Status);
        Assert.DoesNotContain("yedekci", kayit.RemoteUri, StringComparison.Ordinal);
        Assert.DoesNotContain("Gizli-Parola-1!", kayit.RemoteUri, StringComparison.Ordinal);
    }

    // =======================================================================================
    // 3) WebDAV -- ara koleksiyonlar MKCOL ile acilir
    // =======================================================================================

    [Fact]
    public async Task WebDav_yedegi_once_MKCOL_sonra_PUT_ile_gun_klasorune_gider()
    {
        using var alan = new GeciciAlan();
        var kayitci = new IstekKayitcisi();
        var servis = alan.ServisKur(kayitci);

        var istek = SiteIstegi("https://bulut.ornek.com/dav/yedek");
        istek.TargetType = "webdav";
        istek.RemoteUserName = "kullanici";
        istek.RemotePassword = "parola";

        var kayit = await servis.BackupSiteAsync(istek, alan.Iptal);
        var gun = alan.YerelGun(kayit);

        Assert.Equal("created_remote_synced", kayit.Status);

        // MKCOL YUKARIDAN ASAGIYA ve koleksiyon adresi '/' ile biter (RFC 4918).
        Assert.Equal(
            new[]
            {
                $"MKCOL https://bulut.ornek.com/dav/yedek/{gun}/",
                $"MKCOL https://bulut.ornek.com/dav/yedek/{gun}/sites/",
                $"MKCOL https://bulut.ornek.com/dav/yedek/{gun}/sites/{Site}/",
                $"PUT https://bulut.ornek.com/dav/yedek/{gun}/sites/{Site}/{kayit.Name}"
            },
            kayitci.Istekler);

        Assert.Equal($"https://bulut.ornek.com/dav/yedek/{gun}/sites/{Site}/{kayit.Name}", kayit.RemoteUri);
    }

    [Fact]
    public async Task WebDav_koleksiyon_zaten_varsa_405_yedegi_bozmaz()
    {
        // RFC 4918 9.3.1: var olan kaynak icin MKCOL 405 doner. Ikinci geceden itibaren BEKLENEN yanit.
        using var alan = new GeciciAlan();
        var kayitci = new IstekKayitcisi { MkcolYaniti = HttpStatusCode.MethodNotAllowed };
        var servis = alan.ServisKur(kayitci);

        var istek = SiteIstegi("https://bulut.ornek.com/dav/yedek");
        istek.TargetType = "webdav";

        var kayit = await servis.BackupSiteAsync(istek, alan.Iptal);

        Assert.Equal("created_remote_synced", kayit.Status);
        Assert.DoesNotContain(kayit.Warnings, u => u.MessageKey == "backup.remote_upload_failed");
        Assert.Contains($"/{alan.YerelGun(kayit)}/sites/{Site}/", kayit.RemoteUri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WebDav_koleksiyon_izni_yoksa_sebep_hata_ayrintisinda_durur()
    {
        using var alan = new GeciciAlan();
        var kayitci = new IstekKayitcisi
        {
            MkcolYaniti = HttpStatusCode.Forbidden,
            PutYaniti = HttpStatusCode.Conflict // ara koleksiyon yok -> PUT 409
        };
        var servis = alan.ServisKur(kayitci);

        var istek = SiteIstegi("https://bulut.ornek.com/dav/yedek");
        istek.TargetType = "webdav";

        var kayit = await servis.BackupSiteAsync(istek, alan.Iptal);

        Assert.Equal("created_remote_failed", kayit.Status);
        var uyari = Assert.Single(kayit.Warnings, u => u.MessageKey == "backup.remote_upload_failed");
        Assert.Contains("MKCOL", uyari.Detail, StringComparison.Ordinal);
        Assert.Contains("403", uyari.Detail, StringComparison.Ordinal);
        Assert.True(File.Exists(kayit.Path));
    }

    // =======================================================================================
    // 4) S3 -- dizin KAVRAMI YOK: yalnizca anahtar oneki
    // =======================================================================================

    [Fact]
    public async Task S3_de_MKD_MKCOL_DENENMEZ_anahtara_onek_eklenir()
    {
        using var alan = new GeciciAlan();
        var kayitci = new IstekKayitcisi();
        var servis = alan.ServisKur(kayitci);

        var istek = SiteIstegi("https://s3.eu-central-1.amazonaws.com/musteri-yedek");
        istek.TargetType = "s3";
        istek.RemoteUserName = "AKIAIOSFODNN7EXAMPLE";
        istek.RemotePassword = "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY";
        istek.RemoteRegion = "eu-central-1";

        var kayit = await servis.BackupSiteAsync(istek, alan.Iptal);
        var gun = alan.YerelGun(kayit);

        // TEK istek ve o da PUT: S3'te "klasor" diye bir nesne yoktur, MKCOL/MKD anlamsizdir
        // (API'de karsiligi da yoktur; denemek her yedekte bir cop istek demektir).
        var tekIstek = Assert.Single(kayitci.Istekler);
        Assert.Equal(
            $"PUT https://s3.eu-central-1.amazonaws.com/musteri-yedek/{gun}/sites/{Site}/{kayit.Name}",
            tekIstek);
        Assert.DoesNotContain(kayitci.Istekler, item => item.StartsWith("MKCOL", StringComparison.Ordinal));

        Assert.Equal("created_remote_synced", kayit.Status);
    }

    [Fact]
    public void S3_imzasi_anahtarin_TAMAMINI_kapsar()
    {
        // Imza yalnizca dosya adini kapsasaydi, onek eklenen istek "SignatureDoesNotMatch" ile
        // reddedilirdi. Kanonik yol segment segment kodlandigi icin onek imzaya girer.
        var an = new DateTimeOffset(2026, 8, 17, 3, 0, 0, TimeSpan.Zero);
        var duz = S3RequestSigner.Sign(
            "PUT", new Uri("https://s3.amazonaws.com/kova/yedek.zip"),
            "AKIA", "gizli", "eu-central-1", an, S3RequestSigner.UnsignedPayload);
        var onekli = S3RequestSigner.Sign(
            "PUT", new Uri("https://s3.amazonaws.com/kova/2026-08-17/sites/yedek.zip"),
            "AKIA", "gizli", "eu-central-1", an, S3RequestSigner.UnsignedPayload);

        Assert.NotEqual(duz.Authorization, onekli.Authorization);
    }

    // =======================================================================================
    // 5) UZAK SILME (budama) -- yol KAYITTAN turer, YENIDEN HESAPLANMAZ
    // =======================================================================================

    [Fact]
    public async Task Budama_YENI_duzendeki_uzak_kopyayi_gun_klasorunden_siler()
    {
        using var alan = new GeciciAlan();
        await using var sunucu = SahteFtpSunucusu.Baslat();

        var eskiUzakYol = $"/yedekler/2026-08-01/sites/site-20260801030000.zip";
        sunucu.DosyaKoy(eskiUzakYol);

        await alan.KayitlariYazAsync(
            EskiKayit(alan, "eski", $"ftp://127.0.0.1:{sunucu.Port}{eskiUzakYol}", gunOnce: 40),
            TazeKayit(alan, "taze"));

        var silinen = await alan.ServisKur().PruneAsync(
            retentionDays: 7,
            remote: new BackupPruneRemoteOptions { UserName = "u", Password = "p" },
            cancellationToken: alan.Iptal);

        Assert.Equal(1, silinen.Removed);
        Assert.Equal(new[] { eskiUzakYol }, sunucu.SilinenYollar);
        Assert.False(sunucu.Dosyalar.ContainsKey(eskiUzakYol));
    }

    [Fact]
    public async Task Budama_ESKI_duzende_kokun_altinda_duran_uzak_kopyayi_da_siler()
    {
        // GERIYE DONUK ZORUNLULUK: 0.5.5 ve oncesinde yuklenmis yedekler kokun altinda DUZ duruyor.
        // Silme yolu yeniden hesaplanirsa (gun klasoru eklenirse) bu dosyalar UZAKTA SONSUZA KADAR
        // KALIR ve kimse fark etmez. Yol kayitta saklandigi icin ondan turetilir.
        using var alan = new GeciciAlan();
        await using var sunucu = SahteFtpSunucusu.Baslat();

        var duzYol = "/yedekler/site-20260701030000.zip";
        sunucu.DosyaKoy(duzYol);

        await alan.KayitlariYazAsync(
            EskiKayit(alan, "eski", $"ftp://127.0.0.1:{sunucu.Port}{duzYol}", gunOnce: 40),
            TazeKayit(alan, "taze"));

        var silinen = await alan.ServisKur().PruneAsync(
            retentionDays: 7,
            remote: new BackupPruneRemoteOptions { UserName = "u", Password = "p" },
            cancellationToken: alan.Iptal);

        Assert.Equal(1, silinen.Removed);
        Assert.Equal(new[] { duzYol }, sunucu.SilinenYollar);
    }

    [Fact]
    public async Task Budama_WebDav_kopyasini_kayittaki_ADRESTEN_siler()
    {
        using var alan = new GeciciAlan();
        var kayitci = new IstekKayitcisi();

        const string uzak = "https://bulut.ornek.com/dav/yedek/2026-08-01/sites/site.zip";
        var eski = EskiKayit(alan, "eski", uzak, gunOnce: 40);
        eski.TargetType = "webdav";
        await alan.KayitlariYazAsync(eski, TazeKayit(alan, "taze"));

        var silinen = await alan.ServisKur(kayitci).PruneAsync(
            retentionDays: 7,
            remote: new BackupPruneRemoteOptions { UserName = "u", Password = "p" },
            cancellationToken: alan.Iptal);

        Assert.Equal(1, silinen.Removed);
        Assert.Equal(new[] { $"DELETE {uzak}" }, kayitci.Istekler);
    }

    // =======================================================================================
    // 6) BAGLANTI TESTI
    // =======================================================================================

    [Fact]
    public async Task Baglanti_testi_gecelik_yedegin_kullanacagi_ALT_DIZINI_de_dener()
    {
        // Testin isi "bu hedefe yedek gidebilir mi" sorusunu durustce cevaplamaktir. Tabana duz
        // bir dosya atmak, alt dizin acamayan (chroot'lu) hesaplarda "tamam" deyip geceleri
        // sessizce basarisiz olmak demekti.
        using var alan = new GeciciAlan();
        var kayitci = new IstekKayitcisi();
        var servis = alan.ServisKur(kayitci);

        var sonuc = await servis.TestRemoteTargetAsync(new BackupRemoteTestRequest
        {
            TargetType = "webdav",
            RemoteUrl = "https://bulut.ornek.com/dav/yedek"
        }, alan.Iptal);

        Assert.True(sonuc.Success);
        var gun = DateTime.Now.ToString("yyyy-MM-dd");
        Assert.Equal(
            new[]
            {
                $"MKCOL https://bulut.ornek.com/dav/yedek/{gun}/",
                $"MKCOL https://bulut.ornek.com/dav/yedek/{gun}/connection-test/",
                $"PUT https://bulut.ornek.com/dav/yedek/{gun}/connection-test/adapanel-connection-test.txt"
            },
            kayitci.Istekler);
    }

    [Fact]
    public async Task Baglanti_testi_klasor_acilamayan_hedefte_BASARISIZ_der()
    {
        using var alan = new GeciciAlan();
        var kayitci = new IstekKayitcisi
        {
            MkcolYaniti = HttpStatusCode.Forbidden,
            PutYaniti = HttpStatusCode.Conflict
        };

        var sonuc = await alan.ServisKur(kayitci).TestRemoteTargetAsync(new BackupRemoteTestRequest
        {
            TargetType = "webdav",
            RemoteUrl = "https://bulut.ornek.com/dav/yedek"
        }, alan.Iptal);

        Assert.False(sonuc.Success);
        Assert.Contains("MKCOL", sonuc.Details, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Yerel_hedefte_baglanti_testi_ag_istegi_YAPMAZ()
    {
        using var alan = new GeciciAlan();
        var kayitci = new IstekKayitcisi();

        var sonuc = await alan.ServisKur(kayitci).TestRemoteTargetAsync(
            new BackupRemoteTestRequest { TargetType = "local" }, alan.Iptal);

        Assert.True(sonuc.Success);
        Assert.Empty(kayitci.Istekler);
    }

    // =======================================================================================
    // 6b) TABAN ADRESIN SON PARCASI NOKTA ICERIYORSA (alan adi!) duzen dusmemeli
    // =======================================================================================

    /// <summary>
    /// ÖLÇÜLDÜ-BOZUK: kısa devre koşulu <c>Path.HasExtension(sonParca)</c> idi ve
    /// <c>Path.HasExtension("musteri.com")</c> TRUE döner. Uzak klasörü alan adıyla adlandırmak
    /// (en yaygın düzen) gün/kategori klasörünü VE dosya adını birden düşürüyordu: her gecelik
    /// yedek uzakta aynı adrese yazılıyor, panel hepsine "uzağa senkronlandı" diyordu.
    /// </summary>
    [Theory]
    [InlineData("musteri.com")]
    [InlineData("site.com.tr")]
    [InlineData("yedek.2026")]
    public async Task Nokta_iceren_klasor_adi_tek_dosya_SANILMAZ(string sonParca)
    {
        using var alan = new GeciciAlan();
        var kayitci = new IstekKayitcisi();

        var sonuc = await alan.ServisKur(kayitci).TestRemoteTargetAsync(new BackupRemoteTestRequest
        {
            TargetType = "webdav",
            RemoteUrl = $"https://bulut.ornek.com/dav/{sonParca}"
        }, alan.Iptal);

        Assert.True(sonuc.Success);
        var gun = DateTime.Now.ToString("yyyy-MM-dd");
        Assert.Equal(
            new[]
            {
                $"MKCOL https://bulut.ornek.com/dav/{sonParca}/{gun}/",
                $"MKCOL https://bulut.ornek.com/dav/{sonParca}/{gun}/connection-test/",
                $"PUT https://bulut.ornek.com/dav/{sonParca}/{gun}/connection-test/adapanel-connection-test.txt"
            },
            kayitci.Istekler);
    }

    /// <summary>
    /// Karşı yön: adresi GERÇEKTEN tek bir yedek dosyasına sabitleyen kurulumun ölçülmüş
    /// davranışı korunur — panelin ürettiği uzantılar <c>.zip</c> ve <c>.zip.enc</c>'dir.
    /// </summary>
    [Theory]
    [InlineData("yedek.zip")]
    [InlineData("yedek.zip.enc")]
    public async Task Yedek_dosyasina_sabitlenmis_adres_oldugu_gibi_kullanilir(string dosya)
    {
        using var alan = new GeciciAlan();
        var kayitci = new IstekKayitcisi();

        var sonuc = await alan.ServisKur(kayitci).TestRemoteTargetAsync(new BackupRemoteTestRequest
        {
            TargetType = "webdav",
            RemoteUrl = $"https://bulut.ornek.com/dav/{dosya}"
        }, alan.Iptal);

        Assert.True(sonuc.Success);
        Assert.Equal(new[] { $"PUT https://bulut.ornek.com/dav/{dosya}" }, kayitci.Istekler);
    }

    // =======================================================================================
    // 7) Hata SEBEBI kayda gecer (anahtar tek basina yeterli degil)
    // =======================================================================================

    [Fact]
    public void Uzak_hata_metni_ceviri_anahtarini_DEGIL_sunucu_yanitini_tasir()
    {
        var metin = BackupService.DescribeRemoteFailure(
            new AdaPanel.Core.Common.AppException(
                "backup.remote_upload_failed", 502, 502, "MKD ftp://h/y/2026-08-17 -> 550 Permission denied"));

        Assert.Contains("550 Permission denied", metin, StringComparison.Ordinal);
    }

    // =======================================================================================
    // Yardimcilar
    // =======================================================================================

    private static BackupSiteRequest SiteIstegi(string uzakAdres)
    {
        var ftp = uzakAdres.StartsWith("ftp://", StringComparison.OrdinalIgnoreCase);
        return new BackupSiteRequest
        {
            SiteName = "shop.example.com",
            IncludeFiles = true,
            IncludeConfig = false,
            TargetType = ftp ? "ftp" : "webdav",
            RemoteUrl = uzakAdres,
            // FTP kimliksiz kabul edilmez (hedef gecersiz sayilir); taklit sunucu her kimligi kabul eder.
            RemoteUserName = ftp ? "yedekci" : string.Empty,
            RemotePassword = ftp ? "parola" : string.Empty
        };
    }

    private static BackupRecordDto EskiKayit(GeciciAlan alan, string id, string uzakAdres, int gunOnce)
    {
        var yol = Path.Combine(alan.Kok, $"{id}.zip");
        File.WriteAllText(yol, "eski yedek");
        return new BackupRecordDto
        {
            Id = id,
            Name = $"{id}.zip",
            Type = "site",
            Source = "shop.example.com",
            Path = yol,
            Format = "zip",
            CreatedAtUtc = DateTime.UtcNow.AddDays(-gunOnce),
            TargetType = "ftp",
            RemoteUri = uzakAdres,
            Status = "created_remote_synced"
        };
    }

    /// <summary>
    /// Budamanin "kaynak korumasi" kurali: bir kaynagin GUNCEL yedegi yoksa eskisine dokunulmaz.
    /// Eski kaydin silinebilmesi icin ayni kaynaktan taze bir yedek gerekir.
    /// </summary>
    private static BackupRecordDto TazeKayit(GeciciAlan alan, string id)
    {
        var yol = Path.Combine(alan.Kok, $"{id}.zip");
        File.WriteAllText(yol, "taze yedek");
        return new BackupRecordDto
        {
            Id = id,
            Name = $"{id}.zip",
            Type = "site",
            Source = "shop.example.com",
            Path = yol,
            Format = "zip",
            CreatedAtUtc = DateTime.UtcNow,
            TargetType = "local",
            Status = "created"
        };
    }

    private sealed class GeciciAlan : IDisposable
    {
        private readonly CancellationTokenSource _iptal = new(TimeSpan.FromSeconds(60));

        public GeciciAlan()
        {
            Kok = Path.Combine(Path.GetTempPath(), $"adapanel-uzakyol-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Kok);
            DosyaDeposu = new JsonFileStore(
                Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = Kok }));
            SiteKoku = Path.Combine(Kok, "wwwroot", "shop");
            Directory.CreateDirectory(SiteKoku);
            File.WriteAllText(Path.Combine(SiteKoku, "index.php"), "MUSTERI ICERIGI");
        }

        public string Kok { get; }

        public string SiteKoku { get; }

        public JsonFileStore DosyaDeposu { get; }

        /// <summary>Testler ASILI KALMAMALI: sunucu taklidi bozulursa 60 saniyede hata verir.</summary>
        public CancellationToken Iptal => _iptal.Token;

        public BackupService ServisKur(IstekKayitcisi? kayitci = null) =>
            new(new SahteSiteServisi(SiteKoku),
                null!,
                null!,
                new SahteTercihServisi(Path.Combine(Kok, "backups")),
                null!,
                DosyaDeposu,
                new SahteIstemciFabrikasi(kayitci ?? new IstekKayitcisi()),
                // "IC AGA IZIN VER" ACIK KAPISI BU DOSYADA ACIK.
                //
                // Sebep somut: buradaki FTP testleri SahteFtpSunucusu ile 127.0.0.1 uzerinde
                // GERCEK bir dinleyici acar ve yedegi ona gonderir. Yani bu dosya, panelin
                // "kullanici bilerek IC AGDAKI bir hedefe yedek atiyor" senaryosunun ta kendisini
                // olcer -- kapi kapali olsaydi dordu de daha baglanmadan reddedilirdi.
                //
                // Bu, korumayi "testi gecsin diye" kapatmak DEGILDIR: acik kapinin var olma
                // gerekcesi tam olarak bu senaryodur (ic agdaki NAS'a yedek atmak mesrudur).
                // Adres politikasinin kendi olcumleri OutboundTargetGuardTests'tedir; orada
                // mutasyonla dogrulandi.
                runtimeOptions: Microsoft.Extensions.Options.Options.Create(
                    new AdaPanel.Infrastructure.Options.PanelRuntimeOptions { AllowInternalOutboundTargets = true }));

        public Task KayitlariYazAsync(params BackupRecordDto[] kayitlar) =>
            DosyaDeposu.WriteAsync("config/backups.json", kayitlar, Iptal);

        /// <summary>
        /// Kaydin YEREL yolundan gun klasorunu okur
        /// (<c>...\2026-08-17\sites\shop.example.com\dosya.zip</c>).
        /// <para>
        /// Beklenen gunu testte ayrica hesaplamak, gece yarisini gecen bir kosuda testi yanlis
        /// yere bakmaya iterdi; ustelik "uzak yol yerelle AYNI karardan turer" iddiasi ancak
        /// yerelden okuyarak olculebilir.
        /// </para>
        /// <para>
        /// Sabit sayida "Parent" ile YUKARI CIKILMAZ: kaynak klasoru eklendiginde derinlik
        /// degisti, siteye ait veritabani yedeginde bir kat daha degisiyor. Gun klasoru ADINDAN
        /// tanindigi icin bu yardimci duzen degistikce kirilmaz.
        /// </para>
        /// </summary>
        public string YerelGun(BackupRecordDto kayit)
        {
            var klasor = new DirectoryInfo(Path.GetDirectoryName(kayit.Path)!);
            while (klasor is not null)
            {
                if (DateTime.TryParseExact(
                        klasor.Name,
                        "yyyy-MM-dd",
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None,
                        out _))
                {
                    return klasor.Name;
                }

                klasor = klasor.Parent;
            }

            throw new InvalidOperationException($"Yedek yolunda gun klasoru yok: {kayit.Path}");
        }

        public void Dispose()
        {
            _iptal.Dispose();
            try
            {
                Directory.Delete(Kok, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>HTTP/WebDAV/S3 tarafinda hangi istegin hangi adrese gittigini kaydeder.</summary>
    private sealed class IstekKayitcisi
    {
        public List<string> Istekler { get; } = [];

        public HttpStatusCode MkcolYaniti { get; init; } = HttpStatusCode.Created;

        public HttpStatusCode PutYaniti { get; init; } = HttpStatusCode.OK;

        public HttpStatusCode DeleteYaniti { get; init; } = HttpStatusCode.NoContent;
    }

    private sealed class SahteIstemciFabrikasi(IstekKayitcisi kayitci) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new KayitEdenIsleyici(kayitci));
    }

    private sealed class KayitEdenIsleyici(IstekKayitcisi kayitci) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            // Govde GERCEKTEN okunur: yukleme akisinin tuketilmedigi bir taklit, gercek sunucuda
            // patlayan bir kodu yesil gosterebilir.
            if (request.Content is not null)
            {
                await request.Content.ReadAsByteArrayAsync(cancellationToken);
            }

            lock (kayitci.Istekler)
            {
                kayitci.Istekler.Add($"{request.Method.Method} {request.RequestUri}");
            }

            var durum = request.Method.Method switch
            {
                "MKCOL" => kayitci.MkcolYaniti,
                "DELETE" => kayitci.DeleteYaniti,
                _ => kayitci.PutYaniti
            };

            return new HttpResponseMessage(durum) { RequestMessage = request };
        }
    }

    private sealed class SahteTercihServisi(string yedekKoku) : IPanelPreferencesService
    {
        public Task<PanelPreferencesDto> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new PanelPreferencesDto { DefaultBackupRoot = yedekKoku });

        public Task<PanelPreferencesDto> UpdateAsync(UpdatePanelPreferencesRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class SahteSiteServisi(string uygulamaKoku) : IIisSiteService
    {
        public Task<SiteDto> GetSiteAsync(string siteName, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SiteDto
            {
                Name = siteName,
                PhysicalPath = uygulamaKoku,
                ApplicationRootPath = uygulamaKoku
            });

        public Task<IReadOnlyCollection<SiteDto>> GetSitesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<SiteDto> CreateSiteAsync(CreateSiteRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<SiteDto> UpdatePhysicalPathAsync(string siteName, string physicalPath, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task DeleteSiteAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<SiteDto> StartSiteAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<SiteDto> StopSiteAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<SiteDto> AddBindingAsync(string siteName, SiteBindingRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<SiteDto> RemoveBindingAsync(string siteName, RemoveSiteBindingRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyCollection<SiteDto>> ImportExistingSitesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    // =======================================================================================
    // Protokolu TAKLIT EDEN yerel FTP sunucusu
    //
    // Gercek bir FTP hesabi olmadan olcmek istedigimiz sey KOD DEGIL PROTOKOL DAVRANISI:
    //  - ara dizinler kendiliginden olusmaz (STOR, yolun tamamini hazir bekler),
    //  - var olan dizin icin MKD 550 doner (ikinci gecenin normali),
    //  - izin yoksa da 550 doner (ayni kod, farkli sebep).
    // Bu yuzden sunucu MKD/CWD/STOR/DELE'yi gercekten uygular; testler sunucunun gordugu
    // ISLEMLERI dogrular, kodun ne yaptigini varsaymaz.
    // =======================================================================================
    private sealed class SahteFtpSunucusu : IAsyncDisposable
    {
        private readonly TcpListener _dinleyici;
        private readonly CancellationTokenSource _iptal = new();
        private readonly Task _dongu;
        private readonly HashSet<string> _dizinler = new(StringComparer.Ordinal) { "/" };
        private readonly object _kilit = new();

        private SahteFtpSunucusu()
        {
            _dinleyici = new TcpListener(IPAddress.Loopback, 0);
            _dinleyici.Start();
            Port = ((IPEndPoint)_dinleyici.LocalEndpoint).Port;
            _dongu = Task.Run(() => KabulDongusuAsync(_iptal.Token));
        }

        public int Port { get; }

        /// <summary>MKD izni. <c>false</c> = chroot'lu hesap: yazma var, klasor acma yok.</summary>
        public bool MkdIzinli { get; set; } = true;

        public List<string> MkdYollari { get; } = [];

        /// <summary>MKD'ye verilen yanit kodlari ("257..." / "550...").</summary>
        public List<string> MkdSonuclari { get; } = [];

        public List<string> SilinenYollar { get; } = [];

        public Dictionary<string, byte[]> Dosyalar { get; } = new(StringComparer.Ordinal);

        public static SahteFtpSunucusu Baslat() => new();

        public string TabanAdres(string dizin) => $"ftp://127.0.0.1:{Port}/{dizin}";

        public void DosyaKoy(string yol)
        {
            lock (_kilit)
            {
                var dizin = yol[..yol.LastIndexOf('/')];
                var parcalar = dizin.Split('/', StringSplitOptions.RemoveEmptyEntries);
                var birikim = string.Empty;
                foreach (var parca in parcalar)
                {
                    birikim += "/" + parca;
                    _dizinler.Add(birikim);
                }

                Dosyalar[yol] = Encoding.ASCII.GetBytes("eski yedek");
            }
        }

        private async Task KabulDongusuAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                TcpClient istemci;
                try
                {
                    istemci = await _dinleyici.AcceptTcpClientAsync(token);
                }
                catch (Exception)
                {
                    return;
                }

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await OturumAsync(istemci, token);
                    }
                    catch (Exception)
                    {
                        // Istemci baglantiyi koparsa oturum sessizce biter (gercek sunucu gibi).
                    }
                    finally
                    {
                        istemci.Dispose();
                    }
                }, token);
            }
        }

        private async Task OturumAsync(TcpClient istemci, CancellationToken token)
        {
            await using var akis = istemci.GetStream();
            using var okuyucu = new StreamReader(akis, Encoding.ASCII);
            await using var yazici = new StreamWriter(akis, Encoding.ASCII) { AutoFlush = true, NewLine = "\r\n" };

            await yazici.WriteLineAsync("220 AdaPanel test FTP");

            var calisma = "/";
            TcpListener? veriDinleyici = null;

            while (!token.IsCancellationRequested)
            {
                var satir = await okuyucu.ReadLineAsync(token);
                if (satir is null)
                {
                    break;
                }

                var bosluk = satir.IndexOf(' ');
                var komut = (bosluk < 0 ? satir : satir[..bosluk]).ToUpperInvariant();
                var arg = bosluk < 0 ? string.Empty : satir[(bosluk + 1)..].Trim();

                switch (komut)
                {
                    case "USER":
                        await yazici.WriteLineAsync("331 Parola gerekli");
                        break;
                    case "PASS":
                        await yazici.WriteLineAsync("230 Giris tamam");
                        break;
                    case "OPTS":
                    case "TYPE":
                    case "NOOP":
                        await yazici.WriteLineAsync("200 Tamam");
                        break;
                    case "PWD":
                        await yazici.WriteLineAsync($"257 \"{calisma}\" is current directory");
                        break;
                    case "CWD":
                    {
                        var hedef = Coz(calisma, arg);
                        if (Var(hedef))
                        {
                            calisma = hedef;
                            await yazici.WriteLineAsync("250 Dizin degisti");
                        }
                        else
                        {
                            await yazici.WriteLineAsync($"550 {hedef}: No such file or directory");
                        }

                        break;
                    }
                    case "MKD":
                    {
                        var hedef = Coz(calisma, arg);
                        string yanit;
                        lock (_kilit)
                        {
                            MkdYollari.Add(hedef);
                            if (!MkdIzinli)
                            {
                                // GERCEK SUNUCU DAVRANISI: izin yok da 550 doner -- "zaten var" ile
                                // AYNI kod. Kodu okuyup tahmin etmek bu yuzden imkansizdir.
                                yanit = "550 Permission denied";
                            }
                            else if (!_dizinler.Add(hedef))
                            {
                                yanit = $"550 {hedef}: Directory already exists";
                            }
                            else
                            {
                                yanit = $"257 \"{hedef}\" created";
                            }

                            MkdSonuclari.Add(yanit);
                        }

                        await yazici.WriteLineAsync(yanit);
                        break;
                    }
                    case "DELE":
                    {
                        var hedef = Coz(calisma, arg);
                        bool vardi;
                        lock (_kilit)
                        {
                            vardi = Dosyalar.Remove(hedef);
                            SilinenYollar.Add(hedef);
                        }

                        await yazici.WriteLineAsync(vardi ? "250 Silindi" : $"550 {hedef}: No such file");
                        break;
                    }
                    case "PASV":
                    {
                        veriDinleyici?.Stop();
                        veriDinleyici = new TcpListener(IPAddress.Loopback, 0);
                        veriDinleyici.Start();
                        var veriPort = ((IPEndPoint)veriDinleyici.LocalEndpoint).Port;
                        await yazici.WriteLineAsync(
                            $"227 Entering Passive Mode (127,0,0,1,{veriPort / 256},{veriPort % 256})");
                        break;
                    }
                    case "STOR":
                    {
                        var hedef = Coz(calisma, arg);
                        var dizin = hedef[..hedef.LastIndexOf('/')];
                        if (!Var(dizin.Length == 0 ? "/" : dizin))
                        {
                            // ARA DIZIN YOKSA STOR BASARISIZ: FTP kendiliginden klasor acmaz.
                            await yazici.WriteLineAsync($"550 {dizin}: No such file or directory");
                            break;
                        }

                        if (veriDinleyici is null)
                        {
                            await yazici.WriteLineAsync("425 PASV gerekli");
                            break;
                        }

                        await yazici.WriteLineAsync("150 Veri baglantisi aciliyor");
                        using (var veri = await veriDinleyici.AcceptTcpClientAsync(token))
                        await using (var veriAkis = veri.GetStream())
                        {
                            using var bellek = new MemoryStream();
                            await veriAkis.CopyToAsync(bellek, token);
                            lock (_kilit)
                            {
                                Dosyalar[hedef] = bellek.ToArray();
                            }
                        }

                        veriDinleyici.Stop();
                        veriDinleyici = null;
                        await yazici.WriteLineAsync("226 Aktarim tamam");
                        break;
                    }
                    case "QUIT":
                        await yazici.WriteLineAsync("221 Hoscakal");
                        return;
                    default:
                        await yazici.WriteLineAsync($"502 {komut} desteklenmiyor");
                        break;
                }
            }

            veriDinleyici?.Stop();
        }

        private bool Var(string yol)
        {
            lock (_kilit)
            {
                return _dizinler.Contains(yol);
            }
        }

        /// <summary>
        /// FtpWebRequest komutlari kimi zaman MUTLAK, kimi zaman CWD sonrasi GORECELI yolla
        /// gonderir; sunucu ikisini de cozmeli (aksi hâlde test istemcinin ic detayina bagimli olur).
        /// </summary>
        private static string Coz(string calisma, string arg)
        {
            var ham = Uri.UnescapeDataString(arg).Trim('"');
            var tam = ham.StartsWith('/') ? ham : $"{calisma.TrimEnd('/')}/{ham}";
            var parcalar = new List<string>();
            foreach (var parca in tam.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                if (parca == ".")
                {
                    continue;
                }

                if (parca == "..")
                {
                    if (parcalar.Count > 0)
                    {
                        parcalar.RemoveAt(parcalar.Count - 1);
                    }

                    continue;
                }

                parcalar.Add(parca);
            }

            return parcalar.Count == 0 ? "/" : "/" + string.Join('/', parcalar);
        }

        public async ValueTask DisposeAsync()
        {
            _iptal.Cancel();
            _dinleyici.Stop();
            try
            {
                await _dongu;
            }
            catch (Exception)
            {
            }

            _iptal.Dispose();
        }
    }
}
