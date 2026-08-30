using System.Reflection;
using AdaPanel.Core.Dtos.Php;
using AdaPanel.Infrastructure.Php;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// PHP KIRACI IZOLASYONU. Paylasimli barindirmanin tanimi: bir kiracinin sitesi otekinin
/// dosyalarini OKUYAMAZ.
/// <para>
/// BU MAKINEDE OLCULDU (php-cgi 8.3.33, panelin kurdugu gercek php.exe): varsayilan profille
/// ("auto") acilan bir sitede <c>open_basedir</c> BOSTU; betik komsu sitenin dosyasini ve
/// <c>C:\Windows\win.ini</c>'yi okudu. Izolasyon yalnizca "php" profilinde ve yalnizca site
/// kokundeki <c>.user.ini</c> ile yaziliyordu -- yani (1) varsayilan yolda hic calismiyordu,
/// (2) calistigi yerde de kiracinin KENDI silebilecegi bir dosyaya yaziliyordu.
/// </para>
/// <para>
/// Cozum: izolasyon PAYLASILAN php.ini'nin <c>[PATH=...]</c> bolumune yazilir. Ayni olcumde
/// kiracinin <c>.user.ini</c> ile <c>open_basedir="C:/"</c> demesi HICBIR SEY degistirmedi
/// (PHP, PATH bolumlerinin kullanici ini'leriyle ezilmesine izin vermez), bolumu olmayan site
/// ise komsusunu okumaya devam etti.
/// </para>
/// </summary>
public sealed class PhpTenantIsolationTests
{
    private const string Root = @"C:\inetpub\siteler\musteri-a";
    private const string Temp = @"C:\inetpub\siteler\musteri-a\tmp";

    private static IReadOnlyList<string> BosIni() =>
    [
        "[PHP]",
        "memory_limit = 256M",
        "disable_functions = exec,shell_exec"
    ];

    // ---------------------------------------------------------------------------------------
    // 1) VARSAYILAN BLOK
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void Blok_siteyi_kendi_kokune_hapseder()
    {
        var lines = PhpIniSiteIsolation.Upsert(BosIni(), "musteri-a.com", Root);

        Assert.Contains($"[PATH={Root}]", lines);
        Assert.Contains($"open_basedir = \"{Root}\"", lines);
    }

    // Yukleme/oturum/temp UCU DE site icine baglanir. Aksi halde open_basedir'e ORTAK bir OS temp
    // klasoru eklemek zorunda kalinir ve o klasor butun kiracilarin izin listesinde olur: bir
    // kiraci otekinin YUKLEDIGI dosyayi ve OTURUM dosyasini okuyabilir.
    [Theory]
    [InlineData("upload_tmp_dir")]
    [InlineData("sys_temp_dir")]
    [InlineData("session.save_path")]
    public void Yukleme_oturum_ve_temp_site_icine_baglanir(string yonerge)
    {
        var lines = PhpIniSiteIsolation.Upsert(BosIni(), "musteri-a.com", Root);

        Assert.Contains($"{yonerge} = \"{Temp}\"", lines);
    }

    [Fact]
    public void Ortak_isletim_sistemi_temp_klasoru_izin_listesinde_YOKTUR()
    {
        var lines = PhpIniSiteIsolation.Upsert(BosIni(), "musteri-a.com", Root);
        var blok = string.Join('\n', lines);

        Assert.DoesNotContain(@"C:\Windows\Temp", blok, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("%SystemRoot%", blok, StringComparison.OrdinalIgnoreCase);
    }

    // Laravel: IIS fiziksel yolu <proje>\public olur, kok ise projenin KENDISI. Panel kokU
    // ApplicationRootPath'ten alir; blok o koku yazmalidir ki vendor/ ve storage/ erisilebilir kalsin.
    [Fact]
    public void Kok_verilen_uygulama_kokudur_public_degil()
    {
        var lines = PhpIniSiteIsolation.Upsert(BosIni(), "laravel.com", @"C:\inetpub\siteler\laravel");

        Assert.Contains(@"open_basedir = ""C:\inetpub\siteler\laravel""", lines);
        Assert.DoesNotContain(lines, line => line.Contains(@"laravel\public", StringComparison.OrdinalIgnoreCase));
    }

    // Kok = belge koku olan duz kurulumlarda (WordPress) tmp klasoru WEB'DEN ERISILEBILIR olurdu:
    // /tmp/sess_... indirilebilir bir oturum dosyasidir. Izolasyonu duzeltirken oturum calmayi
    // mumkun kilmamak icin klasore "hicbir uzantiya izin verme" web.config'i konur.
    [Fact]
    public void Gecici_klasor_web_uzerinden_sunulmaz()
    {
        Assert.Contains("requestFiltering", PhpIniSiteIsolation.TempDirectoryWebConfig, StringComparison.Ordinal);
        Assert.Contains("allowUnlisted=\"false\"", PhpIniSiteIsolation.TempDirectoryWebConfig, StringComparison.Ordinal);
    }

    [Fact]
    public void Gecici_klasor_web_config_i_gecerli_XML()
    {
        var belge = System.Xml.Linq.XDocument.Parse(PhpIniSiteIsolation.TempDirectoryWebConfig);

        Assert.Equal("configuration", belge.Root!.Name.LocalName);
    }

    // ---------------------------------------------------------------------------------------
    // 2) TEKRAR YAZMA / KAPATMA (geri alinabilirlik)
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void Ayni_site_iki_kez_yazilinca_blok_cogalmaz()
    {
        var bir = PhpIniSiteIsolation.Upsert(BosIni(), "musteri-a.com", Root);
        var iki = PhpIniSiteIsolation.Upsert(bir, "musteri-a.com", Root);

        Assert.Equal(1, iki.Count(line => line.Trim().StartsWith(PhpIniSiteIsolation.BlockStartPrefix, StringComparison.Ordinal)));
        Assert.Equal(1, iki.Count(line => line.Trim().StartsWith("[PATH=", StringComparison.OrdinalIgnoreCase)));
    }

    // Belge koku degistiginde (ya da Laravel yukseltmesi olustugunda) ESKI kok kalmamalidir:
    // kalirsa site iki kok birden gorur ve izolasyon eski koku de kapsamaya devam eder.
    [Fact]
    public void Kok_degisince_eski_kok_dusER()
    {
        var eski = PhpIniSiteIsolation.Upsert(BosIni(), "musteri-a.com", Root);
        var yeni = PhpIniSiteIsolation.Upsert(eski, "musteri-a.com", @"C:\inetpub\siteler\musteri-a-yeni");

        Assert.DoesNotContain(yeni, line => line.Contains($"[PATH={Root}]", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(@"[PATH=C:\inetpub\siteler\musteri-a-yeni]", yeni);
    }

    [Fact]
    public void Kapatmak_yalniz_o_sitenin_blogunu_dusurur()
    {
        var lines = PhpIniSiteIsolation.Upsert(BosIni(), "musteri-a.com", Root);
        lines = PhpIniSiteIsolation.Upsert(lines, "musteri-b.com", @"C:\inetpub\siteler\musteri-b");

        var sonuc = PhpIniSiteIsolation.Remove(lines, "musteri-a.com");

        Assert.False(PhpIniSiteIsolation.IsEnabled(sonuc, "musteri-a.com"));
        Assert.True(PhpIniSiteIsolation.IsEnabled(sonuc, "musteri-b.com"));
        // Genel ayarlar korunur: kapatma islemi php.ini'nin geri kalanina DOKUNMAZ.
        Assert.Contains("memory_limit = 256M", sonuc);
        Assert.Contains("disable_functions = exec,shell_exec", sonuc);
    }

    [Fact]
    public void Durum_okumasi_blogun_varligini_soyler()
    {
        Assert.False(PhpIniSiteIsolation.IsEnabled(BosIni(), "musteri-a.com"));

        var lines = PhpIniSiteIsolation.Upsert(BosIni(), "musteri-a.com", Root);

        Assert.True(PhpIniSiteIsolation.IsEnabled(lines, "musteri-a.com"));
        Assert.Equal(Root, PhpIniSiteIsolation.ReadRoot(lines, "musteri-a.com"));
    }

    // ---------------------------------------------------------------------------------------
    // 3) KAPSAM SINIRI -- "genel" ayarin site bolumune dusmesi
    // ---------------------------------------------------------------------------------------

    // php.ini'de bir [PATH=] basligindan SONRAKI her satir o siteye aittir. Genel yazicilar
    // yonergeyi dosya SONUNA eklerse (eski davranis) ayar genel olarak HIC uygulanmaz, yalnizca
    // son sitenin bolumune dusER. Sinir tek kaynaktan gelir: GlobalInsertIndex.
    [Fact]
    public void Genel_yonerge_sinirlari_ilk_PATH_basligidir()
    {
        var lines = PhpIniSiteIsolation.Upsert(BosIni(), "musteri-a.com", Root).ToList();
        var sinir = PhpIniSiteIsolation.GlobalInsertIndex(lines);

        Assert.True(sinir < lines.Count, "Sinir dosya sonu olamaz: dosyada bir [PATH=] blogu var.");
        Assert.StartsWith("[PATH=", lines[sinir].Trim(), StringComparison.OrdinalIgnoreCase);
        Assert.All(lines.Take(sinir), line => Assert.DoesNotContain("[PATH=", line, StringComparison.OrdinalIgnoreCase));
    }

    // [PHP] / [Date] gibi ADI OLAN bolumler PHP tarafindan yok sayilir (kapsam ACMAZLAR); sinir
    // yalniz PATH/HOST olmalidir, yoksa genel ayarlar dosyanin en basina hapsedilirdi.
    [Fact]
    public void Adi_olan_siradan_bolumler_sinir_sayilmaz()
    {
        string[] lines = ["[PHP]", "memory_limit = 128M", "[Date]", "date.timezone = UTC"];

        Assert.Equal(lines.Length, PhpIniSiteIsolation.GlobalInsertIndex(lines));
    }

    [Fact]
    public void Site_bolumundeki_deger_GENEL_deger_sanilmaz()
    {
        var lines = PhpIniSiteIsolation.Upsert(BosIni(), "musteri-a.com", Root).ToList();
        lines.Add("memory_limit = 4096M"); // site blogunun ICINE dusen bir satir

        var genel = PhpIniSiteIsolation.GlobalLines(lines);

        Assert.Contains("memory_limit = 256M", genel);
        Assert.DoesNotContain("memory_limit = 4096M", genel);
    }

    // ---------------------------------------------------------------------------------------
    // 4) ENJEKSIYON -- paylasilan php.ini butun sitelerin dosyasidir
    // ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData("site]\r\n[PATH=C:\\]")]
    [InlineData("site\nopen_basedir = \"C:/\"")]
    [InlineData("site[PATH=C:\\]")]
    [InlineData("  ")]
    public void Uydurulmus_site_adi_php_ini_ye_bolum_enjekte_edemez(string siteAdi)
    {
        Assert.ThrowsAny<ArgumentException>(() => PhpIniSiteIsolation.Upsert(BosIni(), siteAdi, Root));
    }

    [Theory]
    [InlineData(@"gecersiz\yol")]           // mutlak degil
    [InlineData("C:\\kok\"\r\nopen_basedir = \"C:/")]
    public void Uydurulmus_kok_yolu_reddedilir(string kok)
    {
        Assert.ThrowsAny<ArgumentException>(() => PhpIniSiteIsolation.Upsert(BosIni(), "musteri-a.com", kok));
    }

    // ---------------------------------------------------------------------------------------
    // 5) SOZLESME -- "soylenmeyen alan degistirmez"
    // ---------------------------------------------------------------------------------------

    // Alan eskiden `bool` idi: JSON'da hic gonderilmediginde `false` olur ve yalniz memory_limit
    // guncelleyen bir istemci sitenin izolasyonunu SESSIZCE KAPATIRDI.
    [Fact]
    public void Izolasyon_alani_soylenmediginde_degismez_olmalidir()
    {
        var property = typeof(UpdateSitePhpSettingsRequest)
            .GetProperty(nameof(UpdateSitePhpSettingsRequest.OpenBasedirEnabled), BindingFlags.Public | BindingFlags.Instance);

        Assert.NotNull(property);
        Assert.Equal(typeof(bool?), property!.PropertyType);
        Assert.Null(new UpdateSitePhpSettingsRequest().OpenBasedirEnabled);
    }

    // ---------------------------------------------------------------------------------------
    // 6) BAGLANTI NOBETCILERI -- "panel yaziyor ama oteki taraf hic cagirmiyor" sinifi
    //
    // Bu dort denetim KAYNAK METNINE bakar; boyle olmasinin sebebi olculdu: cagri yerleri
    // (SiteProvisioningService.IsolateSiteAsync, PhpController) IIS/ServerManager ve tam bir
    // kimlik baglami olmadan calistirilamiyor. Kapatilan acik ise tam olarak "kod dogru ama
    // VARSAYILAN YOLDAN hic cagrilmiyor"du -- yani korunmasi gereken sey cagrinin KENDISI.
    // ---------------------------------------------------------------------------------------

    private static string Kaynak(params string[] parcalar)
        => File.ReadAllText(Path.Combine(ScriptTestHost.RepoRoot, Path.Combine(parcalar)));

    // Varsayilan profil "auto"dur ve o yol SetSitePhpVersionAsync'i CAGIRMAZ. Izolasyon bu yuzden
    // profilden bagimsiz tek yerde (IsolateSiteAsync) cagrilir; cagri dusERSE varsayilan site
    // yine izolasyonsuz kalir.
    [Fact]
    public void Site_kurulumu_PHP_izolasyonunu_profilden_bagimsiz_cagirir()
    {
        var kaynak = Kaynak("src", "AdaPanel.Infrastructure", "Iis", "SiteProvisioningService.cs");

        Assert.Contains("phpService.EnsureSiteIsolationAsync(site.Name, enabled: true", kaynak, StringComparison.Ordinal);
    }

    // Siteye PHP atandiginda da (mevcut siteye sonradan PHP verilmesi) izolasyon yazilir.
    [Fact]
    public void PHP_atamasi_izolasyonu_yazar()
    {
        var kaynak = Kaynak("src", "AdaPanel.Infrastructure", "Php", "PhpService.cs");

        Assert.Contains("EnsureSiteIsolationAsync(siteName, enabled: true", kaynak, StringComparison.Ordinal);
    }

    // Uc ISTISNA: izolasyonu KAPATMAK sunucu duzeyi karardir. Uc Reseller'a da acik oldugu icin,
    // bayi kendi musterisinin sitesi uzerinden BASKA bayilerin musterilerini okuyabilirdi.
    [Fact]
    public void Izolasyonu_yalniz_yonetici_kapatabilir()
    {
        var kaynak = Kaynak("src", "AdaPanel.Api", "Controllers", "PhpController.cs");

        Assert.Contains("request.OpenBasedirEnabled is false", kaynak, StringComparison.Ordinal);
        Assert.Contains("User.IsInRole(\"Administrator\")", kaynak, StringComparison.Ordinal);
        Assert.Contains("php.isolation_admin_only", kaynak, StringComparison.Ordinal);
    }

    // php.ini'ye GENEL yonerge yazan her yol kapsam sinirini kullanmak ZORUNDA: "lines.Add" ile
    // dosya sonuna eklemek, ayari son sitenin [PATH=] blogunun icine gomerdi (butun sitelerde
    // eklenti/limit sessizce kaybolurdu).
    [Theory]
    [InlineData("src", "AdaPanel.Infrastructure", "Php", "PhpService.cs")]
    [InlineData("src", "AdaPanel.Infrastructure", "Bootstrap", "ComponentBootstrapper.cs")]
    public void php_ini_yazicilari_genel_bolum_sinirini_kullanir(params string[] parcalar)
    {
        var kaynak = Kaynak(parcalar);

        Assert.Contains("PhpIniSiteIsolation.GlobalInsertIndex(lines)", kaynak, StringComparison.Ordinal);
        Assert.DoesNotContain("lines.Add(directive)", kaynak, StringComparison.Ordinal);
        Assert.DoesNotContain("lines.Add(formatted)", kaynak, StringComparison.Ordinal);
        Assert.DoesNotContain("lines.Add($\"extension", kaynak, StringComparison.Ordinal);
    }

    // Izolasyon durumu .user.ini'den OKUNMAZ: o dosya kiracinin kendi klasorundedir ve silinebilir;
    // oradan okumak panelin "izolasyon acik" diye YALAN soylemesine yol acardi.
    [Fact]
    public void Izolasyon_durumu_kiracinin_silebildigi_dosyadan_okunmaz()
    {
        var kaynak = Kaynak("src", "AdaPanel.Infrastructure", "Php", "PhpService.cs");

        Assert.Contains("dto.OpenBasedirEnabled = await IsSiteIsolatedAsync", kaynak, StringComparison.Ordinal);
        Assert.DoesNotContain("values.ContainsKey(\"open_basedir\")", kaynak, StringComparison.Ordinal);
    }

    // Koruma yalnizca metinde kalmasin: izolasyon yazilirken web.config GERCEKTEN olusturulmali.
    [Fact]
    public void Izolasyon_gecici_klasore_koruma_web_config_i_yazar()
    {
        var kaynak = Kaynak("src", "AdaPanel.Infrastructure", "Php", "PhpService.cs");

        Assert.Contains("PhpIniSiteIsolation.TempDirectoryWebConfig", kaynak, StringComparison.Ordinal);
    }

    // Yukseltme calisan bir siteyi KENDILIGINDEN kisitlamaz; izolasyon mevcut siteye ancak acik bir
    // eylemle gelir. Karar kodda yazili olmali ki bir sonraki degisiklik onu bilmeden bozmasin.
    [Fact]
    public void Mevcut_siteler_icin_verilen_karar_kodda_yazili()
    {
        var kaynak = Kaynak("src", "AdaPanel.Infrastructure", "Iis", "SiteProvisioningService.cs");

        Assert.Contains("MEVCUT SİTELER İÇİN VERİLEN KARAR", kaynak, StringComparison.Ordinal);
    }
}
