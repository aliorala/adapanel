using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Firewall;
using AdaPanel.Core.Dtos.Mail;
using AdaPanel.Infrastructure.Bootstrap;
using AdaPanel.Infrastructure.Firewall;
using AdaPanel.Infrastructure.Support;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// "Panel yapamadigi seye BASARDIM diyor" turunun dordu: dis sisteme bir sey soylendi, hata
/// firlamadi, panel basarili saydi.
/// </summary>
/// <remarks>
/// <para>
/// Bu dosyadaki testlerin ortak sorusu tektir: <b>bos sonuc ile olculemeyen sonuc ayni sayiliyor
/// mu?</b> Dordu de ayni bicimde kapatildi -- yazilan geri okunur, okunamiyorsa panel "yapildi"
/// demez, "olculemedi" der (uc degerli durustluk).
/// </para>
/// <para>
/// <b>Bu makinede OLCULEMEYENLER:</b> ayakta bir Stalwart, IIS uygulama havuzu, MariaDB ve
/// Windows DNS YOK. Guvenlik duvari yolu icin surec YUKSELTILMEMIS oldugundan
/// <c>Get-NetFirewallRule</c> "Erisim engellendi" veriyor -- bu, okunabilirlik kapisinin
/// GERCEKTEN olculebildigi anlamina gelir (asagida), ama kural OKUNDU dali bu makinede gercek bir
/// kuralla olculemedi.
/// </para>
/// </remarks>
public sealed class OlculemedigiSoylenmeyenIddialarTests
{
    // =============================================================================
    // ADAY 4 -- GUVENLIK DUVARI: `Enabled = true` UYDURMASI
    // =============================================================================

    /// <summary>
    /// KOK OLCUM: cok ifadeli bir PowerShell script'inde <c>New-NetFirewallRule</c> duserse
    /// script akmaya devam eder ve <c>powershell.exe</c> <b>0</b> ile cikar.
    /// </summary>
    /// <remarks>
    /// Bu, panelin "kural eklendi" diyebilmesinin sebebiydi: <c>UpsertRuleAsync</c> yalniz
    /// <c>result.Success</c>'e bakiyor, sonra <c>Enabled = true</c> ISTEYEREK donuyordu. Test
    /// gercek <c>powershell.exe</c>'yi kosturur; hicbir kural OLUSTURMAZ (parametre donusumu
    /// cmdlet calismadan once duser) ve hicbir sey SILMEZ.
    /// </remarks>
    [Fact]
    public async Task Guvenlik_duvari_yazmasi_DUSSE_BILE_kati_olmayan_kosucu_SIFIR_doner()
    {
        var script =
            "New-NetFirewallRule -DisplayName 'AdaPanel Olcum Kurali Yok' -Direction Inbound "
            + "-Action GECERSIZEYLEM -Protocol TCP -LocalPort 65099 -Profile Any | Out-Null\n"
            + "[Console]::Out.WriteLine('DEVAM-ETTI')";

        var gevsek = await ProcessRunner.RunPowerShellAsync(script);

        // ISTE YALANIN KAYNAGI: komut coktu, cikis kodu 0.
        Assert.Contains("DEVAM-ETTI", gevsek.StandardOutput, StringComparison.Ordinal);
        Assert.True(gevsek.Success);

        // KATI kosucu ayni script'te arizayi cikis koduna yansitir -- UpsertRuleAsync artik bunu
        // kullaniyor.
        var kati = await ProcessRunner.RunPowerShellStrictAsync(script);
        Assert.False(kati.Success);
    }

    /// <summary>
    /// Kural okuma betigi, guvenlik duvari OKUNAMADIGINDA damga BASMAZ.
    /// </summary>
    /// <remarks>
    /// <b>GERCEK OLCUM:</b> bu surec yukseltilmemis oldugu icin <c>Get-NetFirewallRule</c>
    /// gercekten "Erisim engellendi" ile duser. Eski davranista bu, <c>-ErrorAction
    /// SilentlyContinue</c> yuzunden BOS LISTE + cikis kodu 0 demekti; yani "okuyamadim" ile
    /// "hic kural yok" ayirt edilemiyordu. Damga tam da bunu ayirir.
    /// <para>
    /// Yukseltilmis bir makinede kapi gecilir ve damga basilir; test o durumda da anlamlidir
    /// (asagidaki iki dal).
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Kural_okuma_betigi_okuyamadiginda_DAMGA_BASMAZ()
    {
        var sonuc = await ProcessRunner.RunPowerShellAsync(FirewallService.BuildReadRulesScript());

        var okunabildi = sonuc.StandardOutput.Contains("ADAPANEL-DUVAR=OKUNDU", StringComparison.Ordinal);
        var duvarErisimi = await GuvenlikDuvariOkunabilirMi();

        // Betik ile gercek erisim AYNI seyi soylemeli. Damga, erisim yokken basilamaz.
        Assert.Equal(duvarErisimi, okunabildi);
    }

    /// <summary>Damga satiri JSON'dan ayrilir; ayrilmasaydi her okuma ayristirma hatasi verirdi.</summary>
    [Fact]
    public void Damga_satiri_JSONdan_ayrilir()
    {
        var temiz = FirewallService.StripOkunduDamgasi("ADAPANEL-DUVAR=OKUNDU\n[{\"DisplayName\":\"AdaPanel Block TCP 25\"}]");

        Assert.DoesNotContain("ADAPANEL-DUVAR", temiz, StringComparison.Ordinal);
        Assert.Contains("AdaPanel Block TCP 25", temiz, StringComparison.Ordinal);
    }

    /// <summary>
    /// Temizleme raporu: BULUNAN sayi degil, GERCEKTEN GIDEN kurallar yazilir.
    /// </summary>
    [Fact]
    public void Temizleme_raporu_kaldirilamayan_kurali_ADIYLA_yazar()
    {
        var rapor = new FirewallBlockCleanupReport(
            true,
            ["AdaPanel Block TCP 25"],
            ["AdaPanel Block TCP 3389"]);

        Assert.False(rapor.Temiz);
        Assert.Contains("AdaPanel Block TCP 3389", rapor.Describe(), StringComparison.Ordinal);
    }

    /// <summary>
    /// FAZLA KAPATMA NOBETCISI: her sey yolundayken rapor SADE kalir, uyari YAZMAZ.
    /// </summary>
    /// <remarks>
    /// "Her zaman supheli yaz" cozumu oteki testleri de gecerdi ve her basarili kurtarmayi
    /// arizali gosterirdi; surekli uyaran bir kayit okunmaz olur.
    /// </remarks>
    [Fact]
    public void Temizleme_raporu_her_sey_yolundayken_SADE_kalir()
    {
        var rapor = new FirewallBlockCleanupReport(true, ["AdaPanel Block TCP 25"], []);

        Assert.True(rapor.Temiz);
        Assert.Equal("1 panel engelleme kurali kaldirildi", rapor.Describe());
        Assert.DoesNotContain("OLCULEMEDI", rapor.Describe(), StringComparison.Ordinal);
        Assert.DoesNotContain("KALDIRILAMAYAN", rapor.Describe(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Guvenlik duvari OKUNAMADIYSA rapor "kural yoktu" DEMEZ.
    /// </summary>
    /// <remarks>
    /// Eski donus tipi <c>int</c>'ti: okuma basarisiz olunca 0 cikiyor ve erisim kurtarma gunlugu
    /// "0 panel engelleme kurali kaldirildi" yaziyordu -- yani "temizlenecek bir sey yoktu". Panele
    /// ulasamayan yonetici guvenlik duvarini eleyip baska yerde arardi.
    /// </remarks>
    [Fact]
    public void Duvar_okunamadiginda_rapor_KURAL_YOKTU_demez()
    {
        var rapor = FirewallBlockCleanupReport.Olculemedi;

        Assert.False(rapor.Temiz);
        Assert.Contains("OLCULEMEDI", rapor.Describe(), StringComparison.Ordinal);
        Assert.DoesNotContain("yoktu", rapor.Describe(), StringComparison.Ordinal);
    }

    /// <summary>Olculdu ve gercekten kural yoktu: bu bir OLCUMDUR, sade cumleyle soylenir.</summary>
    [Fact]
    public void Olculdu_ve_kural_yoktu_hali_OLCULEMEDIDEN_ayridir()
    {
        Assert.True(FirewallBlockCleanupReport.Bos.Temiz);
        Assert.Equal("kaldirilacak panel engelleme kurali yoktu", FirewallBlockCleanupReport.Bos.Describe());
    }

    /// <summary>
    /// Geri okunamayan kuralda <c>Enabled</c> bir OLCUM DEGILDIR ve kayit bunu soyler.
    /// </summary>
    [Fact]
    public void Dogrulanmamis_kural_denetim_kaydinda_ETKIN_diye_gecmez()
    {
        var dogrulanmamis = new FirewallRuleDto { DisplayName = "AdaPanel Block TCP 25", Verified = false };

        // Uydurma yok: Enabled varsayilan false ve Verified bunun bir olcum olmadigini soyluyor.
        Assert.False(dogrulanmamis.Enabled);
        Assert.False(dogrulanmamis.Verified);
    }

    /// <summary>
    /// FAZLA KAPATMA NOBETCISI: listeleme yolundan gelen satirlar tanimi geregi OKUNMUSTUR.
    /// </summary>
    [Fact]
    public void Listeden_gelen_kural_varsayilan_olarak_DOGRULANMIS_sayilir()
    {
        Assert.True(new FirewallRuleDto { DisplayName = "AdaPanel Allow TCP 443" }.Verified);
    }

    private static async Task<bool> GuvenlikDuvariOkunabilirMi()
    {
        var sonuc = await ProcessRunner.RunPowerShellAsync(
            "try { $null = @(Get-NetFirewallRule -ErrorAction Stop | Select-Object -First 1); "
            + "[Console]::Out.WriteLine('ERISIM=VAR') } catch { [Console]::Out.WriteLine('ERISIM=YOK') }");

        return sonuc.StandardOutput.Contains("ERISIM=VAR", StringComparison.Ordinal);
    }

    // =============================================================================
    // ADAY 2 -- KOTA: "OLCULEMEDI" ILE "KOTA ASILMADI" AYNI SAYILIYORDU
    // =============================================================================

    /// <summary>
    /// Kota VARSA ve sayim yapilamadiysa islem reddedilir -- kota sessizce kalkmaz.
    /// </summary>
    /// <remarks>
    /// Olculen acik: <c>MailController.EnsureMailboxQuotaAsync</c> kutulari
    /// <c>workspace.Accounts?.Count ?? 0</c> ile sayiyordu ve bos liste, Stalwart sorgusu HIC
    /// KOSMADIGINDA da geliyordu. Kotasi dolu kiraci sinirsiz kutu acabilirdi.
    /// </remarks>
    [Fact]
    public void Kota_varken_olculemeyen_sayim_islemi_REDDEDER()
    {
        Assert.False(MailQuotaMeasurement.CanEnforce(limit: 5, measured: false));

        var hata = Assert.Throws<AppException>(() => MailQuotaMeasurement.EnsureMeasured(
            5,
            false,
            MailQuotaMeasurement.MailboxCountKey,
            "ornek.com"));

        Assert.Equal("mail.mailbox_quota_unmeasured", hata.MessageKey);
        Assert.Contains("ornek.com", hata.Details, StringComparison.Ordinal);
    }

    /// <summary>
    /// FAZLA KAPATMA NOBETCISI 1: olcum YAPILDIYSA hicbir sey soylenmez, islem akar.
    /// </summary>
    [Fact]
    public void Olcum_yapildiysa_kota_kapisi_SESSIZ_gecer()
    {
        Assert.True(MailQuotaMeasurement.CanEnforce(limit: 5, measured: true));
        MailQuotaMeasurement.EnsureMeasured(5, true, MailQuotaMeasurement.MailboxCountKey, "ornek.com");
    }

    /// <summary>
    /// FAZLA KAPATMA NOBETCISI 2: kotasi OLMAYAN musteri, olcum yapilamasa da engellenmez.
    /// </summary>
    /// <remarks>
    /// "Olcemedigimde her zaman reddet" cozumu oteki testleri de gecerdi ama limitsiz musteriler
    /// posta sunucusu her aksadiginda kutu acamaz olurdu -- ozelligin kendisini kirardi.
    /// </remarks>
    [Fact]
    public void Kotasiz_musteri_olcum_yapilamasa_da_ENGELLENMEZ()
    {
        Assert.True(MailQuotaMeasurement.CanEnforce(limit: 0, measured: false));
        MailQuotaMeasurement.EnsureMeasured(0, false, MailQuotaMeasurement.MailboxCountKey, "ornek.com");
    }

    /// <summary>Alan adi disk tahsisi kotasi ayni kapidan gecer (TEK KAYNAK).</summary>
    [Fact]
    public void Alan_adi_disk_kotasi_ayni_kapidan_gecer()
    {
        var hata = Assert.Throws<AppException>(() => MailQuotaMeasurement.EnsureMeasured(
            2048,
            false,
            MailQuotaMeasurement.DomainAllocationKey,
            "ornek.com"));

        Assert.Equal("mail.domain_quota_unmeasured", hata.MessageKey);
    }

    /// <summary>
    /// Calisma alani DTO'sunda "olculdu" bayragi VARSAYILAN OLARAK KAPALIDIR.
    /// </summary>
    /// <remarks>
    /// Bu, korumanin dayandigi tek varsayimdir: hesap listesini doldurmayan her yol (posta
    /// kurulu degil, yapilandirilmamis, alan adi secilmemis) kendiliginden "olculmedi" der.
    /// Varsayilan true olsaydi, o yollarin hepsi kotayi sessizce devre disi birakirdi.
    /// </remarks>
    [Fact]
    public void Calisma_alani_varsayilan_olarak_OLCULMEDI_der()
    {
        Assert.False(new MailWorkspaceDto().AccountsMeasured);
        Assert.False(new MailWorkspaceDto { Installed = true, Configured = false }.AccountsMeasured);
    }

    // =============================================================================
    // ADAY 3 -- BILESEN KURULUMU: ISTENEN SURUM KAYDEDILIYORDU
    // =============================================================================

    /// <summary>
    /// GERCEK OLCUM: depodaki gercek <c>dotnet.exe</c> <c>--version</c> ile kosturulur ve
    /// ciktisindan surum ayiklanir.
    /// </summary>
    /// <remarks>
    /// Elle yazilmis bir ornek dizge yerine GERCEK bir aracin GERCEK ciktisi kullanilir; kurulum
    /// yolunun ayiklayicisi da tam olarak budur. Bu makinede kurulu bir Stalwart/Node/Python
    /// olmadigi icin olcum, ayni bicimde surum basan mevcut araca yapilir.
    /// </remarks>
    [Fact]
    public async Task Surum_ciktisi_GERCEK_bir_aractan_okunur()
    {
        var dotnet = Path.Combine(ScriptTestHost.RepoRoot, ".tools", "dotnet", "dotnet.exe");
        Assert.True(File.Exists(dotnet), $"Olcum icin gerekli arac yok: {dotnet}");

        var sonuc = await ProcessRunner.RunAsync(dotnet, "--version");
        var okunan = ComponentVersionProbe.FromCommandOutput(sonuc.StandardOutput);

        Assert.False(string.IsNullOrWhiteSpace(okunan));
        Assert.Contains('.', okunan!);

        // Ayiklanan sey gercekten ciktinin ICINDE: uydurulmus bir sabit degil.
        Assert.Contains(okunan!, sonuc.StandardOutput, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("v22.11.0\r\n", "22.11.0")]
    [InlineData("Python 3.12.4\r\n", "3.12.4")]
    [InlineData("Composer version 2.7.7 2024-06-10 15:03:00\r\n", "2.7.7")]
    [InlineData("stalwart-cli 0.10.6\r\n", "0.10.6")]
    public void Surum_jetonu_araclarin_farkli_sarmalarindan_ayiklanir(string cikti, string beklenen)
    {
        Assert.Equal(beklenen, ComponentVersionProbe.FromCommandOutput(cikti));
    }

    /// <summary>Surum okunamadiysa UYDURULMAZ.</summary>
    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("AdaPanel-PHP-OK")]
    public void Surum_okunamazsa_null_doner(string? cikti)
    {
        Assert.Null(ComponentVersionProbe.FromCommandOutput(cikti));
    }

    /// <summary>
    /// Olculen surum ISTENENDEN farkliysa KAYDEDILEN olculendir ve kayit sapmayi soyler.
    /// </summary>
    /// <remarks>
    /// Bu depoda birkac bilesenin indirme adresi surum numarasi URL'e dizilerek TAHMIN ediliyor;
    /// paket baska bir yama surumu tasirsa panel istenen surumu kaydeder, guncelleme onbellegi de
    /// o yanlis degerden beslenip "guncelsiniz" derdi.
    /// </remarks>
    [Fact]
    public void Olculen_surum_istenenden_farkliysa_OLCULEN_kaydedilir()
    {
        var kayit = ComponentVersionProbe.Decide("8.4.3", "8.4.0");

        Assert.Equal("8.4.0", kayit.Version);
        Assert.True(kayit.Measured);
        Assert.True(kayit.Deviated);
        Assert.Contains("8.4.0", kayit.Describe(), StringComparison.Ordinal);
        Assert.Contains("8.4.3", kayit.Describe(), StringComparison.Ordinal);
    }

    /// <summary>Olculemediyse istenen surum gosterilir ama OLCUM OLMADIGI soylenir.</summary>
    [Fact]
    public void Surum_olculemediyse_kayit_ISTENEN_oldugunu_soyler()
    {
        var kayit = ComponentVersionProbe.Decide("8.4.3", null);

        Assert.Equal("8.4.3", kayit.Version);
        Assert.False(kayit.Measured);
        Assert.False(kayit.Deviated);
        Assert.Contains("REQUESTED", kayit.Describe(), StringComparison.Ordinal);
        Assert.Contains("could not be measured", kayit.Describe(), StringComparison.Ordinal);
    }

    /// <summary>
    /// FAZLA KAPATMA NOBETCISI: olculen surum istenenle AYNIYSA cumle SADE kalir.
    /// </summary>
    /// <remarks>
    /// "Her kurulumda dogrulama notu yaz" cozumu de oteki testleri gecerdi, ama her kaydi
    /// gurultuye bogar ve gercek sapma gozden kacardi.
    /// </remarks>
    [Fact]
    public void Surum_istenenle_ayniysa_kayit_SADE_kalir()
    {
        var kayit = ComponentVersionProbe.Decide("8.4.3", "8.4.3");

        Assert.True(kayit.Measured);
        Assert.False(kayit.Deviated);
        Assert.Equal("version 8.4.3", kayit.Describe());
    }
}
