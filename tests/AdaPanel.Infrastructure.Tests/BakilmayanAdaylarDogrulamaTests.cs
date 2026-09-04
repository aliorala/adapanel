using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Databases;
using AdaPanel.Core.Dtos.Ftp;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Dtos.Scheduler;
using AdaPanel.Core.Enums;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Database;
using AdaPanel.Infrastructure.Ftp;
using AdaPanel.Infrastructure.Iis;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Scheduler;
using AdaPanel.Infrastructure.Ssl;
using AdaPanel.Infrastructure.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Onceki turun "BAKILMADI" diye biraktigi adaylarin DOGRULANMASI ve kapatilmasi.
///
/// <para>
/// Aranan tek hata sinifi: panel dis bir sisteme (IIS FTP, IIS baglama/sertifika deposu, Gorev
/// Zamanlayici, MySQL/MariaDB) bir sey soyluyor, istisna firlatilmadigi icin basarili sayiyor ve
/// kullaniciya/denetim kaydina "yapildi" yaziyor.
/// </para>
///
/// <para>
/// <b>OLCULEMEYENLER, ACIKCA.</b> Bu makinede kurulu IIS FTP sitesi, IIS uygulama havuzu, ayakta
/// MariaDB/MySQL ve Gorev Zamanlayici'da panel gorevleri YOK; ustelik surec YUKSELTILMEMIS. Bu
/// yuzden asagidakiler olculMEDI: IIS'in <c>controlChannelPolicy</c> yazimini gercekte kabul
/// edip etmedigi, <c>AddSslCertificate</c>'in gercek davranisi, <c>taskkill</c>'in gercek bir
/// gorev agacindaki cikis kodu ve <c>mysql.exe</c>'nin bozuk bir dokum karsisindaki cikis kodu.
/// OLCULEN sey, panelin surec sonuclarini NASIL OKUDUGUDUR -- ve bu, elle yazilmis sahte
/// sonuclarla degil, GERCEK <c>powershell.exe</c> surecleriyle uretilmis gercek
/// <see cref="CommandResult"/> nesneleri uzerinden olculur.
/// </para>
/// </summary>
public sealed class BakilmayanAdaylarDogrulamaTests
{
    // =================================================================================
    // ADAY 1 -- FTP: SetRequireSslAsync ISTENEN bayragi donduruyordu
    // =================================================================================

    /// <summary>
    /// Betikteki geri okuma damgasi GERCEK bir powershell.exe surecinden gecirilip ayristirilir.
    /// </summary>
    /// <remarks>
    /// Damganin bicimi (satir sonlari, bosluk, tirnak) elle yazilan bir dizede degil, ancak
    /// gercek bir surecin ciktisinda dogru olculur -- ayristiricinin sessizce bosa dusme riski
    /// tam da orada.
    /// </remarks>
    private static async Task<string> GercekDamgaCiktisiAsync(string control, string data, string cert)
    {
        var sonuc = await ProcessRunner.RunPowerShellAsync(
            $"$c = '{control}'; $d = '{data}'; $s = '{cert}'; "
            + "[Console]::Out.WriteLine('ADAPANEL-FTP-TLS=' + $c + '|' + $d + '|' + $s)");

        Assert.True(sonuc.Success, "Olcum surecinin kendisi dusmemeli.");
        return sonuc.StandardOutput;
    }

    [Fact]
    public async Task FTP_TLS_politikasi_UYGULANDIYSA_yururlukte_sayilir()
    {
        var cikti = await GercekDamgaCiktisiAsync("1", "1", "AABBCC");

        var olcum = FtpTlsMeasurement.Olc(requireSsl: true, cikti);

        Assert.True(olcum.Enforced);
        Assert.False(olcum.Failed);

        // FAZLA KAPATMA NOBETCISI: her sey yolundayken panel HALA sade "yapildi" demeli.
        Assert.True(olcum.Verified);
    }

    [Fact]
    public async Task FTP_TLS_politikasi_ENUM_ADIYLA_okunsa_da_yururlukte_sayilir()
    {
        // Bazi IIS surumleri sayi yerine enum adi dondurur. Bunu "uygulanmadi" saymak, calisan bir
        // FTP'yi yanlis alarmla bloke eden ESKI davranisin ta kendisiydi (betikteki 2026-06-21 notu).
        var cikti = await GercekDamgaCiktisiAsync("SslRequire", "SslRequire", "AABBCC");

        Assert.True(FtpTlsMeasurement.Olc(requireSsl: true, cikti).Enforced);
    }

    [Fact]
    public async Task FTP_TLS_politikasi_UYGULANMADIYSA_panel_zorunlu_DEMEZ()
    {
        // Yazma sessizce dustu: IIS hala SslAllow(0) doner. Eskiden panel ISTENEN bayragi
        // dondurdugu icin kullaniciya "duz FTP baglantilari reddedilecek" diyordu.
        var cikti = await GercekDamgaCiktisiAsync("0", "0", "AABBCC");

        var olcum = FtpTlsMeasurement.Olc(requireSsl: true, cikti);

        Assert.False(olcum.Enforced);
        Assert.True(olcum.Failed);
        Assert.False(olcum.Verified);
    }

    [Fact]
    public async Task FTP_TLS_SERTIFIKASIZ_zorunluluk_UYGULANMADI_sayilir()
    {
        // controlChannelPolicy=1 iken serverCertHash yoksa IIS AUTH TLS'i reddeder: duz baglanti
        // da yasak, TLS de imkansiz -> FTP TUMDEN kapanir. Betikte sertifika baglama `catch { }`
        // icinde oldugu icin bu durum sessizce olusabiliyor.
        var cikti = await GercekDamgaCiktisiAsync("1", "1", "yok");

        var olcum = FtpTlsMeasurement.Olc(requireSsl: true, cikti);

        Assert.True(olcum.Failed);
        Assert.False(olcum.Enforced);
    }

    [Fact]
    public async Task FTP_TLS_OKUNAMAZSA_uygulanmadi_DEMEZ_bilmedigini_soyler()
    {
        // Bos deger = geri okuma yapilamadi. Bunu "uygulanmadi" saymak yanlis alarm uretirdi.
        var cikti = await GercekDamgaCiktisiAsync(string.Empty, string.Empty, string.Empty);

        var olcum = FtpTlsMeasurement.Olc(requireSsl: true, cikti);

        Assert.False(olcum.Failed);
        Assert.False(olcum.Enforced);
        Assert.Null(olcum.Verified);
    }

    [Fact]
    public void FTP_TLS_damgasi_HIC_YOKSA_olculemedi_sayilir()
    {
        // Betik damgaya varmadan dustuyse hicbir sey iddia edilmez.
        var olcum = FtpTlsMeasurement.Olc(requireSsl: true, "Set-ItemProperty : Access is denied.");

        Assert.Null(olcum.Verified);
        Assert.False(olcum.Failed);
    }

    [Fact]
    public void FTP_TLS_TANINMAYAN_deger_yanlis_alarm_uretmez()
    {
        // Get-ItemProperty bazi surumlerde nesnenin kendisini dondurebiliyor. Tanimadigimiz bir
        // metni "uygulanmadi" saymak, calisan bir FTP'yi bozuk gostermek olurdu.
        var olcum = FtpTlsMeasurement.Olc(
            requireSsl: true,
            "ADAPANEL-FTP-TLS=Microsoft.IIs.PowerShell.Framework.ConfigurationAttribute|1|AABB");

        Assert.False(olcum.Failed);
        Assert.Null(olcum.Verified);
    }

    [Fact]
    public void FTP_altyapi_betigi_geri_okuma_damgasini_BASAR()
    {
        // Damga kaldirilirsa olcum sessizce "hep olculemedi"ye duser ve panel gri bir cumleye
        // sabitlenir; bu testin varlik sebebi o sessiz gerilemeyi yakalamaktir.
        var kaynak = File.ReadAllText(FtpServisKaynakYolu());

        Assert.Contains("ADAPANEL-FTP-TLS=", kaynak, StringComparison.Ordinal);
        Assert.Contains("ftpServer.security.ssl.dataChannelPolicy", kaynak, StringComparison.Ordinal);
        Assert.Contains("ftpServer.security.ssl.serverCertHash", kaynak, StringComparison.Ordinal);
    }

    [Fact]
    public void FTP_hizli_yol_TLS_politikasini_UYGULANDI_saymaz()
    {
        // Hizli yolda politika yeniden YAZILMAZ, dolayisiyla olculMEZ de. Orada "uygulandi"
        // demek, hic bakmadan iddia etmek olurdu.
        var olcum = FtpTlsMeasurement.Olculemedi("hizli yol");

        Assert.Null(olcum.Verified);
        Assert.False(olcum.Enforced);
        Assert.False(olcum.Failed);
    }

    private static string FtpServisKaynakYolu()
        => Path.Combine(KaynakKoku(), "src", "AdaPanel.Infrastructure", "Ftp", "FtpService.cs");

    // =================================================================================
    // ADAY 2 -- SSL: baglama olculuyor, SERTIFIKA olculmuyordu
    // =================================================================================

    private static IisHttpsBinding Baglama(
        string site,
        string bindingInfo,
        string thumbprint,
        bool okunabilir = true)
        => new()
        {
            SiteName = site,
            BindingInformation = bindingInfo,
            Thumbprint = thumbprint,
            CertificateReadable = okunabilir
        };

    [Fact]
    public void SSL_sertifika_baglamada_ise_kurulum_YESIL_kalir()
    {
        var (baglamaVar, sertifika, _) = SslBindingVerification.Olc(
            [Baglama("site", "*:443:ornek.com", "aabbcc")],
            "site",
            "ornek.com",
            443,
            "AABBCC");

        // FAZLA KAPATMA NOBETCISI: her sey yolundayken panel HALA sade "SSL kuruldu" demeli.
        Assert.True(baglamaVar);
        Assert.True(sertifika);
    }

    [Fact]
    public void SSL_baglama_var_ama_SERTIFIKA_YOKSA_kuruldu_DENMEZ()
    {
        // Betik once New-WebBinding ile baglamayi kurar, SONRA AddSslCertificate cagirir. Ikincisi
        // duserse geriye SERTIFIKASIZ bir HTTPS baglamasi kalir: tarayici baglantiyi kapatir, site
        // 443'te oludur. Eski olcum yalniz protokole baktigi icin buna "HTTPS baglamasi var"
        // diyordu -- SiteBindingDto'da sertifika alani zaten YOKTU.
        var (baglamaVar, sertifika, ayrinti) = SslBindingVerification.Olc(
            [Baglama("site", "*:443:ornek.com", string.Empty)],
            "site",
            "ornek.com",
            443,
            "AABBCC");

        Assert.True(baglamaVar);
        Assert.False(sertifika);
        Assert.Contains("sertifika YOK", ayrinti, StringComparison.Ordinal);
    }

    [Fact]
    public void SSL_baglamada_BASKA_sertifika_varsa_kuruldu_DENMEZ()
    {
        var (_, sertifika, ayrinti) = SslBindingVerification.Olc(
            [Baglama("site", "*:443:ornek.com", "ddeeff")],
            "site",
            "ornek.com",
            443,
            "AABBCC");

        Assert.False(sertifika);

        // Baglami adiyla soyler: yonetici hangi parmak izinin bulundugunu gormeli.
        Assert.Contains("ddeeff", ayrinti, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SSL_sertifika_alani_OKUNAMADIYSA_bagli_degil_DEMEZ()
    {
        // "Sertifika yok" ile "sertifikaya bakamadim" ayni goruntuye dusmemeli: bu deponun
        // tekrarlanan hata sinifinin sertifika tarafindaki ornegi.
        var (baglamaVar, sertifika, _) = SslBindingVerification.Olc(
            [Baglama("site", "*:443:ornek.com", string.Empty, okunabilir: false)],
            "site",
            "ornek.com",
            443,
            "AABBCC");

        Assert.True(baglamaVar);
        Assert.Null(sertifika);
    }

    [Fact]
    public void SSL_baglama_listesi_OKUNAMADIYSA_hicbir_sey_iddia_edilmez()
    {
        var (baglamaVar, sertifika, _) = SslBindingVerification.Olc(null, "site", "ornek.com", 443, "AABBCC");

        Assert.Null(baglamaVar);
        Assert.Null(sertifika);
    }

    [Fact]
    public void SSL_baglama_HIC_YOKSA_olculdu_ve_yok_denir()
    {
        // BOS LISTE ile OKUNAMAYAN LISTE farkli: bos liste gercek bir olcumdur.
        var (baglamaVar, sertifika, _) = SslBindingVerification.Olc([], "site", "ornek.com", 443, "AABBCC");

        Assert.False(baglamaVar);
        Assert.False(sertifika);
    }

    [Fact]
    public void SSL_okuyucu_betigi_sertifika_OKUNABILIRLIGINI_bildirir()
    {
        // certificateReadable damgasi kalkarsa "sertifika yok" ile "okuyamadim" yeniden ayni
        // goruntuye duser.
        var kaynak = File.ReadAllText(Path.Combine(
            KaynakKoku(), "src", "AdaPanel.Infrastructure", "Ssl", "WinAcmeSslService.cs"));

        Assert.Contains("certificateReadable", kaynak, StringComparison.Ordinal);
    }

    // =================================================================================
    // ADAY 3 -- ZAMANLAYICI: taskkill cikis kodu atiliyor, DENEME sayisi "oldurulen" yaziliyordu
    // =================================================================================

    [Fact]
    public void Durdurma_betigi_taskkill_SONUCUNU_denetler()
    {
        var betik = SchedulerService.BuildStopScript("ornek-gorev", @"C:\log\ornek.log");

        // Iki kanit birlikte aranmali: cikis kodu VE surecin gercekten gitmis olmasi.
        Assert.Contains("$LASTEXITCODE", betik, StringComparison.Ordinal);
        Assert.Contains("ADAPANEL-DENENEN=", betik, StringComparison.Ordinal);
        Assert.Contains("ADAPANEL-HAYATTA=", betik, StringComparison.Ordinal);

    }

    /// <summary>
    /// Sayac YALNIZ iki kanit birlikte saglandiginda artabilir.
    /// </summary>
    /// <remarks>
    /// BU TEST BIR MUTASYON KACAGINI KAPATIYOR. Ilk yazilan sinama yalniz <c>$LASTEXITCODE</c>
    /// gecip gecmedigine bakiyordu; kosulun YANINA kosulsuz bir <c>$oldurulen++</c> eklendiginde
    /// (yani ESKI DAVRANIS geri getirildiginde) yesil kaliyordu. Sayilan sey artik artirma
    /// NOKTASININ TEKLIGI: iki artirma noktasi varsa biri kosulsuzdur.
    /// </remarks>
    [Fact]
    public void Durdurma_betiginde_oldurulen_sayaci_YALNIZ_kanit_varken_artar()
    {
        var betik = SchedulerService.BuildStopScript("ornek-gorev", @"C:\log\ornek.log");
        var artiranlar = betik
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Split('\n')
            // Yorum satirlari sayilmaz: gerekce metni de bu sayacin adini geciyor.
            .Where(satir => !satir.TrimStart().StartsWith('#'))
            .Where(satir => satir.Contains("$oldurulen++", StringComparison.Ordinal))
            .ToArray();

        Assert.Single(artiranlar);
        Assert.Contains("$kod -eq 0", artiranlar[0], StringComparison.Ordinal);
        Assert.Contains("-not $kalan", artiranlar[0], StringComparison.Ordinal);
    }

    /// <summary>
    /// Oldurme karari GERCEK powershell.exe'de kosturularak olculur (dize taramasi degil).
    /// </summary>
    [Theory]
    // Cikis kodu 0 VE surec gitti -> tek gecerli "olduruldu" hali.
    [InlineData("0", "$null", 1)]
    // taskkill erisim reddinde 1 doner ve surec YASAR. ESKI DAVRANIS burada 1 yaziyordu.
    [InlineData("1", "'duruyor'", 0)]
    // Cikis kodu 0 olsa bile surec duruyorsa olduruldu DEGILDIR ("termination signal sent").
    [InlineData("0", "'duruyor'", 0)]
    [InlineData("1", "$null", 0)]
    public async Task Oldurme_karari_GERCEK_powershell_ile_olculur(string kod, string kalan, int beklenen)
    {
        var sonuc = await ProcessRunner.RunPowerShellAsync(
            $"$kod = {kod}; $kalan = {kalan}; $oldurulen = 0; "
            + "if ($kod -eq 0 -and -not $kalan) { $oldurulen++ }; "
            + "[Console]::Out.WriteLine('SAYI=' + $oldurulen)");

        Assert.True(sonuc.Success);
        Assert.Contains($"SAYI={beklenen}", sonuc.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Durdurma_betigi_GERCEK_powershell_ile_kosar_ve_damgalari_basar()
    {
        // Betigin tamami GERCEK powershell.exe'de kosturulur. Panelin kendi gorevi bu makinede
        // OLMADIGI icin hicbir surec oldurulmez ve HICBIR GERCEK GOREV DEGISTIRILMEZ -- olculen
        // sey betigin sozdizimsel olarak gecerli oldugu ve damgalari bastigidir. Bu deger bir
        // yazim hatasinin butun durdurma yolunu "cron.stop_failed"a cevirmesini engeller.
        var betik = SchedulerService.BuildStopScript(
            "adapanel-test-olmayan-gorev",
            Path.Combine(Path.GetTempPath(), "adapanel-olmayan.log"));

        var sonuc = await ProcessRunner.RunPowerShellAsync(betik);

        Assert.Contains("ADAPANEL-OLDURULEN=0", sonuc.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("ADAPANEL-DENENEN=0", sonuc.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("ADAPANEL-CALISIYOR=0", sonuc.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void Durdurma_sonucu_HEPSI_oldurulduyse_sade_kalir()
    {
        // FAZLA KAPATMA NOBETCISI: her sey yolundayken panel HALA sade "durduruldu" demeli.
        var durum = new StopScheduledTaskResultDto
        {
            KilledProcesses = 3,
            AttemptedProcesses = 3,
            SurvivingProcesses = [],
            StillRunning = false
        };

        Assert.True(durum.AllKilled);
    }

    [Fact]
    public void Durdurma_sonucu_HAYATTA_KALAN_varsa_durduruldu_DEMEZ()
    {
        // taskkill /F erisim reddinde 1 doner ve surec yasamaya devam eder; eskiden bu durum da
        // "3 surec agaci sonlandirildi" yesil satirina dusuyordu.
        var durum = new StopScheduledTaskResultDto
        {
            KilledProcesses = 1,
            AttemptedProcesses = 3,
            SurvivingProcesses = ["chrome(4120)", "chromedriver(4188)"],
            StillRunning = false
        };

        Assert.False(durum.AllKilled);

        // ADIYLA: "ikisi oldurulemedi" demek yoneticiye HANGISI oldugunu soylemez.
        Assert.Contains("chrome(4120)", durum.SurvivingProcesses);
    }

    [Fact]
    public void Silme_ve_durum_degistirme_GERI_OKUR()
    {
        // Kardesi CreateTaskAsync bu geri okumayi zaten yapiyordu (cron.write_unverified);
        // silme ve etkinlestirme yollari yapmiyordu. Ayni dosyada bir tarafin kapali obur
        // tarafin acik olmasi, korumanin kural degil tesaduf oldugunu gosterir.
        var kaynak = File.ReadAllText(Path.Combine(
            KaynakKoku(), "src", "AdaPanel.Infrastructure", "Scheduler", "SchedulerService.cs"));

        Assert.Contains("cron.delete_failed", kaynak, StringComparison.Ordinal);
        Assert.Contains("cron.delete_unverified", kaynak, StringComparison.Ordinal);
        Assert.Contains("cron.state_not_applied", kaynak, StringComparison.Ordinal);
        Assert.Contains("cron.state_unverified", kaynak, StringComparison.Ordinal);
    }

    // =================================================================================
    // ADAY 5a -- MySQL: `--execute="source ..."` istemci tarafi, hatalar cikis koduna yansimaz
    // =================================================================================

    [Fact]
    public void MySql_geri_yukleme_argumanlarinda_source_YOK()
    {
        var profil = new DatabaseConnectionProfile(
            DbEngine.MariaDb,
            "mariadb",
            "MariaDB",
            "mariadb-11",
            "MariaDB",
            "127.0.0.1",
            3306,
            "root",
            "gizli",
            true,
            @"C:\MariaDB");

        var arg = DatabaseService.BuildMySqlImportArguments(profil, " \"musteri_db\"");

        // `source` mysql istemcisinin KENDI komutudur: dosyadaki bir ifade patlasa da istemci
        // devam eder ve cikis kodu 0 kalabilir -> yarim geri yuklenmis veritabani "tamam" gorunur.
        Assert.DoesNotContain("source", arg, StringComparison.OrdinalIgnoreCase);

        // --force korumayi tam tersine cevirir: eklenirse bu test kirmizi yanar.
        Assert.DoesNotContain("--force", arg, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("musteri_db", arg, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Kosucu_standart_girdiyi_GERCEKTEN_besler_ve_cikis_kodunu_yansitir()
    {
        // BU MAKINEDE OLCULDU. Kurulu MariaDB/MySQL yok, bu yuzden mysql.exe'nin gercek cikis kodu
        // olculEMEZ; olculen sey, toplu (batch) girdinin gercekten aktigi ve besleyen surecin
        // sifir olmayan cikis kodunun CommandResult'a dogru yansidigidir -- duzeltmenin dayandigi
        // mekanizma tam olarak budur.
        var betikYolu = Path.Combine(Path.GetTempPath(), $"adapanel-stdin-{Guid.NewGuid():N}.ps1");
        var girdiYolu = Path.Combine(Path.GetTempPath(), $"adapanel-stdin-{Guid.NewGuid():N}.sql");

        try
        {
            await File.WriteAllTextAsync(
                betikYolu,
                "$giren = [Console]::In.ReadToEnd()\n"
                + "[Console]::Out.WriteLine('UZUNLUK=' + $giren.Length)\n"
                + "if ($giren -match 'PATLAYAN-IFADE') { exit 9 }\n"
                + "exit 0\n");

            await File.WriteAllTextAsync(girdiYolu, "SELECT 1;\nPATLAYAN-IFADE;\n");

            var sonuc = await ProcessRunner.RunAsync(
                "powershell.exe",
                $"-NoProfile -ExecutionPolicy Bypass -File \"{betikYolu}\"",
                standardInputFilePath: girdiYolu);

            // (1) Girdi gercekten akti mi?
            Assert.Contains("UZUNLUK=", sonuc.StandardOutput, StringComparison.Ordinal);
            Assert.DoesNotContain("UZUNLUK=0", sonuc.StandardOutput, StringComparison.Ordinal);

            // (2) Sifir olmayan cikis kodu sonuca yansidi mi? `Success` bunu okur.
            Assert.Equal(9, sonuc.ExitCode);
            Assert.False(sonuc.Success);
        }
        finally
        {
            // Yalniz BU testin kendi olusturdugu gecici dosyalar; baska hicbir sey silinmez.
            if (File.Exists(betikYolu)) { File.Delete(betikYolu); }
            if (File.Exists(girdiYolu)) { File.Delete(girdiYolu); }
        }
    }

    [Fact]
    public async Task Kosucu_standart_girdi_verilmeyen_cagriyi_BOZMAZ()
    {
        // FAZLA KAPATMA NOBETCISI: yeni parametre istege baglidir; verilmediginde eski davranis
        // aynen surmeli (girdi borusu ACILMAMALI, aksi halde girdi bekleyen araclar asilirdi).
        var sonuc = await ProcessRunner.RunPowerShellAsync("[Console]::Out.WriteLine('TAMAM'); exit 0");

        Assert.True(sonuc.Success);
        Assert.Contains("TAMAM", sonuc.StandardOutput, StringComparison.Ordinal);
    }

    // =================================================================================
    // ADAY 5b -- HardenAdminAccountsAsync: DROP'lar yutulunca kayit "temiz" diyordu
    // =================================================================================

    [Fact]
    public void Sertlestirme_HEPSI_kaldirildiysa_TEMIZ_der()
    {
        // FAZLA KAPATMA NOBETCISI: her sey yolundayken panel HALA sade "sertlestirildi" demeli.
        var rapor = new DatabaseHardeningReport
        {
            Applied = ["root@%", "(anonim)@localhost"],
            Measured = true
        };

        Assert.True(rapor.Clean);
    }

    [Fact]
    public void Sertlestirme_HIC_guvensiz_hesap_yoksa_da_TEMIZ_der()
    {
        var rapor = new DatabaseHardeningReport { Measured = true };

        Assert.True(rapor.Clean);
    }

    [Fact]
    public void Sertlestirme_KALDIRILAMAYAN_varsa_temiz_DEMEZ()
    {
        // Eskiden bu durum BOS `removed` listesine dusuyor ve denetim kaydi "guvensiz yonetici
        // hesabi bulunamadi" yaziyordu -- kodun bir ifade once SELECT ile SAYDIGI hesaplar icin.
        var rapor = new DatabaseHardeningReport
        {
            Failed = ["root@% (Access denied)", "(anonim)@localhost (Access denied)"],
            Measured = true
        };

        Assert.False(rapor.Clean);

        // ADIYLA: hangi hesabin durdugunu soylemeyen bir uyari, yoneticiyi hicbir yere goturmez.
        Assert.Contains(rapor.Failed, satir => satir.StartsWith("root@%", StringComparison.Ordinal));
    }

    [Fact]
    public void Sertlestirme_GERI_OKUNAMADIYSA_temiz_de_bozuk_da_DEMEZ()
    {
        var rapor = new DatabaseHardeningReport
        {
            Applied = ["root@%"],
            Unverified = ["guvensiz hesap listesi geri okunamadi"],
            Measured = false
        };

        Assert.Null(rapor.Clean);
    }

    [Fact]
    public void Sertlestirme_varsayilani_TEMIZ_DEGIL_bilinmiyordur()
    {
        // Fail-safe: raporu dolduran biri `Measured`'i unutursa panel "dogrulandi" dememeli.
        Assert.Null(DatabaseHardeningReport.Bos().Clean);
    }

    [Fact]
    public void Sertlestirme_birlestirmesi_OLCULMEYEN_tarafi_yutmaz()
    {
        // Iki profil (MariaDB + MySQL) ayri ayri kosabiliyor. Biri olculmediyse toplam sonuc
        // hakkinda "temiz" denemez: yarisina hic bakilmadi.
        var birlesik = DatabaseHardeningReport.Birlestir(
            new DatabaseHardeningReport { Measured = true },
            new DatabaseHardeningReport { Measured = false });

        Assert.Null(birlesik.Clean);
    }

    // =================================================================================
    // ADAY 4 -- IIS: takma ad kaydi, baglama kaldirilamasa da siliniyordu
    // =================================================================================

    [Fact]
    public void Takma_ad_silme_baglama_DURUYORSA_kaydi_silmez()
    {
        var kaynak = File.ReadAllText(Path.Combine(
            KaynakKoku(), "src", "AdaPanel.Infrastructure", "Iis", "SiteAliasService.cs"));

        // Kaydi silen cagri, GERI OKUMADAN SONRA gelmeli. Sira tersine donerse takma ad
        // arayuzden kaybolur ve IIS'te yayinda kalir -- kapatilan hatanin ta kendisi.
        var geriOkumaIndex = kaynak.IndexOf("var kalanlar = new[] { host,", StringComparison.Ordinal);
        var kayitSilmeIndex = kaynak.IndexOf("KayitYazAsync(siteName, host, redirect: false, sil: true", StringComparison.Ordinal);

        Assert.True(geriOkumaIndex > 0, "Takma ad silmede geri okuma bulunamadi.");
        Assert.True(kayitSilmeIndex > 0, "Takma ad kaydini silen cagri bulunamadi.");
        Assert.True(
            geriOkumaIndex < kayitSilmeIndex,
            "Kayit, baglamanin gercekten kalktigi OLCULMEDEN siliniyor.");

        Assert.Contains("alias.remove_binding_failed", kaynak, StringComparison.Ordinal);

        // OKUNAMADI ile KALKMADI ayri: kendi olcum hatamizi geri alinamaz bir "kayboldu"ya
        // cevirmeyiz.
        Assert.Contains("alias.remove_unverified", kaynak, StringComparison.Ordinal);
    }

    /// <summary>
    /// Baglama GERCEKTEN kalktiginda islem SADE tamamlanir ve kayit silinir.
    /// </summary>
    /// <remarks>
    /// FAZLA KAPATMA NOBETCISI. Kaynak taramasi tek basina yetmiyordu: "her zaman baglama
    /// duruyor" mutasyonu kaynak taramasindan GECIYORDU. Bu test gercek servisi (sahte IIS ile)
    /// kosturur, yani her sey yolundayken panelin HALA sade "takma ad kaldirildi" dedigini olcer.
    /// </remarks>
    [Fact]
    public async Task Takma_ad_silme_BASARILIYKA_sade_tamamlanir()
    {
        using var gecici = new GeciciKlasor();
        var iis = new SahteIisSiteServisi(baglamalariKaldir: true);
        var servis = YeniAliasServisi(iis, gecici);

        var sonuc = await servis.RemoveAliasAsync("site", "takma.ornek.com");

        Assert.Empty(sonuc.Aliases);
        Assert.True(iis.KaldirmaCagrildi);
    }

    [Fact]
    public async Task Takma_ad_silme_BAGLAMA_DURUYORSA_hata_verir_ve_kayit_KALIR()
    {
        using var gecici = new GeciciKlasor();

        // IIS "kaldirdim" der (istisna atmaz) ama baglama listesinde AYNEN durur -- gevsek bir
        // IIS yazmasinin sessizce etkisiz kalmasi tam olarak boyle gorunur.
        var iis = new SahteIisSiteServisi(baglamalariKaldir: false);
        var servis = YeniAliasServisi(iis, gecici);

        var hata = await Assert.ThrowsAsync<AppException>(
            () => servis.RemoveAliasAsync("site", "takma.ornek.com"));

        Assert.Equal("alias.remove_binding_failed", hata.MessageKey);

        // Baglama ADIYLA yazilir: yoneticinin IIS'te arayacagi satir budur.
        Assert.Contains("takma.ornek.com", hata.Details ?? string.Empty, StringComparison.Ordinal);

        // KAYIT DURUYOR: takma ad panelden kaybolup IIS'te yayinda kalmaz, islem yeniden denenebilir.
        var kalanKayit = await File.ReadAllTextAsync(Path.Combine(gecici.Yol, "config", "site-aliases.json"));
        Assert.Contains("takma.ornek.com", kalanKayit, StringComparison.Ordinal);
    }

    private static SiteAliasService YeniAliasServisi(SahteIisSiteServisi iis, GeciciKlasor gecici)
    {
        var store = new JsonFileStore(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = gecici.Yol }));
        store.WriteAsync(
            "config/site-aliases.json",
            new[] { new { SiteName = "site", HostName = "takma.ornek.com", RedirectToPrimary = false } })
            .GetAwaiter().GetResult();

        return new SiteAliasService(iis, store, NullLogger<SiteAliasService>.Instance);
    }

    /// <summary>
    /// Yalnizca bu testin ihtiyaci kadar IIS: site okur, baglama kaldirma cagrisini KAYDEDER ve
    /// istege gore gercekten kaldirir ya da SESSIZCE hicbir sey yapmaz.
    /// </summary>
    private sealed class SahteIisSiteServisi(bool baglamalariKaldir) : IIisSiteService
    {
        private readonly List<SiteBindingDto> _baglamalar =
        [
            new() { Protocol = "http", HostName = "ornek.com", IpAddress = "*", Port = 80 },
            new() { Protocol = "http", HostName = "takma.ornek.com", IpAddress = "*", Port = 80 }
        ];

        public bool KaldirmaCagrildi { get; private set; }

        public Task<SiteDto> GetSiteAsync(string siteName, CancellationToken cancellationToken = default)
            => Task.FromResult(new SiteDto { Name = "site", Bindings = _baglamalar.ToArray() });

        public Task<SiteDto> RemoveBindingAsync(string siteName, RemoveSiteBindingRequest request, CancellationToken cancellationToken = default)
        {
            KaldirmaCagrildi = true;
            if (baglamalariKaldir)
            {
                _baglamalar.RemoveAll(item =>
                    string.Equals(item.HostName, request.HostName, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(item.Protocol, request.Protocol, StringComparison.OrdinalIgnoreCase));
            }

            // İSTİSNA ATMAZ: "hata vermedi" ile "oldu" ayni sey degil -- olculen tam olarak bu.
            return Task.FromResult(new SiteDto { Name = "site", Bindings = _baglamalar.ToArray() });
        }

        public Task<IReadOnlyCollection<SiteDto>> GetSitesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDto> CreateSiteAsync(CreateSiteRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDto> UpdatePhysicalPathAsync(string siteName, string physicalPath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDto> StartSiteAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDto> StopSiteAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDto> AddBindingAsync(string siteName, SiteBindingRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<SiteDto>> ImportExistingSitesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        // Bu sahte IIS HICBIR SEY SILMEZ: silme ucu bilerek desteklenmiyor.
        public Task DeleteSiteAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    /// <summary>Yalniz BU testin kendi olusturdugu gecici klasor; baska hicbir sey silinmez.</summary>
    private sealed class GeciciKlasor : IDisposable
    {
        public string Yol { get; } = Path.Combine(Path.GetTempPath(), $"adapanel-alias-{Guid.NewGuid():N}");

        public GeciciKlasor() => Directory.CreateDirectory(Yol);

        public void Dispose()
        {
            try { Directory.Delete(Yol, recursive: true); } catch { /* en iyi caba */ }
        }
    }

    // =================================================================================
    // ADAY 6a -- Posta: SetAccountQuotaAsync kardeslerindeki dogrulamadan yoksundu
    // =================================================================================

    [Fact]
    public void Posta_kutusu_kotasi_GERI_OKUNUR()
    {
        var kaynak = File.ReadAllText(Path.Combine(
            KaynakKoku(), "src", "AdaPanel.Infrastructure", "Mail", "MailService.cs"));

        var basla = kaynak.IndexOf("public async Task<MailWorkspaceDto> SetAccountQuotaAsync", StringComparison.Ordinal);
        Assert.True(basla > 0);
        var govde = kaynak[basla..(basla + 3000)];

        // Olcum ZATEN elimizdeydi: MutateAsync taze workspace donduruyor ve oradaki MaxSizeMB
        // sunucudan okunmus degerdir. Kullanilmiyordu.
        Assert.Contains("hesap.MaxSizeMB != istenen", govde, StringComparison.Ordinal);
        Assert.Contains("mail.mailbox_quota_not_applied", govde, StringComparison.Ordinal);

        // OLCULEMEDI ile UYGULANMADI ayri anahtarlar.
        Assert.Contains("mail.mailbox_quota_unverified", govde, StringComparison.Ordinal);
        Assert.Contains("AccountsMeasured", govde, StringComparison.Ordinal);
    }

    // =================================================================================
    // ADAY 6b -- Bilesen kurulumu: roundcube/clamav/vandelay HIC dogrulanmiyordu
    // =================================================================================

    [Theory]
    [InlineData("\"roundcube\" => FindInstalledFile(installDirectory, \"index.php\")")]
    [InlineData("\"clamav\" => FindInstalledFile(installDirectory, \"clamscan.exe\")")]
    [InlineData("\"vandelay\" => FindInstalledFile(installDirectory, \"vandelay.exe\")")]
    public void Dogrulamasiz_bilesenlerin_kurulum_isareti_ARANIR(string beklenenDal)
    {
        var kaynak = File.ReadAllText(Path.Combine(
            KaynakKoku(), "src", "AdaPanel.Infrastructure", "Bootstrap", "ComponentBootstrapper.cs"));

        Assert.Contains(beklenenDal, kaynak, StringComparison.Ordinal);
    }

    [Fact]
    public void Bilesen_isareti_BULUNAMAZSA_kurulum_basarili_sayilmaz()
    {
        // FindInstalledFile bulamayinca BOS doner ve bos dize alttaki File.Exists kapisini
        // ATLATIR: "dosya yok" ile "bu bilesen icin dogrulama tanimli degil" ayni goruntuye
        // dusuyordu. Bu ek kapi olmadan uc dal da hicbir sey dogrulamamis olurdu.
        var kaynak = File.ReadAllText(Path.Combine(
            KaynakKoku(), "src", "AdaPanel.Infrastructure", "Bootstrap", "ComponentBootstrapper.cs"));

        Assert.Contains(
            "componentId is \"roundcube\" or \"clamav\" or \"vandelay\"",
            kaynak,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Bilesen_isaretleri_TUKETICININ_aradigi_dosya_adlariyla_ayni()
    {
        // TEK KAYNAK: "kurulu" diyen kapi ile "bulamadim" diyen kapi ayni dosyayi aramali,
        // yoksa panel kurulu der, tuketici bulamaz.
        var kok = KaynakKoku();

        Assert.Contains(
            "\"vandelay.exe\"",
            File.ReadAllText(Path.Combine(kok, "src", "AdaPanel.Infrastructure", "Mail", "StalwartAccountArchiver.cs")),
            StringComparison.Ordinal);

        Assert.Contains(
            "\"clamscan.exe\"",
            File.ReadAllText(Path.Combine(kok, "src", "AdaPanel.Infrastructure", "Antivirus", "AntivirusService.cs")),
            StringComparison.Ordinal);
    }

    // =================================================================================
    // Ortak
    // =================================================================================

    private static string KaynakKoku()
    {
        var dizin = new DirectoryInfo(AppContext.BaseDirectory);
        while (dizin is not null && !Directory.Exists(Path.Combine(dizin.FullName, "src")))
        {
            dizin = dizin.Parent;
        }

        Assert.NotNull(dizin);
        return dizin!.FullName;
    }
}
