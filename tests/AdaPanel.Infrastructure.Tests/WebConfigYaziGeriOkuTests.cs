using System.Xml.Linq;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Infrastructure.Iis;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// ADAY 2 -- <c>SiteHostingSettingsService.UpdateWebConfig</c>: <c>document.Save</c> metodun SON
/// ifadesiydi; geri okuma, yeniden ayristirma ya da IIS tarafi dogrulama YOKTU.
/// </summary>
/// <remarks>
/// <para>
/// <b>OLCULEN HATA.</b> <c>Validate</c> ISTEGI denetliyordu, URETILEN BELGEYI degil. Bozuk bir
/// web.config sitenin TAMAMINA <c>500.19</c> verir; panel yine "kaydedildi" derdi ve denetim
/// kaydindaki sayilar servisin BELLEKTE kurdugu DTO'dan gelirdi.
/// </para>
/// <para>
/// <b>BU TESTLER GERCEK DOSYA YAZAR.</b> Gecici bir klasorde gercek bir web.config uretilir,
/// gercekten kilitlenir, gercekten geri alinir ve icerigi GERCEKTEN geri okunur. Kaynak
/// taramasi YOK -- bu oturumda kaynak tarayan testler alti kez yanildi.
/// </para>
/// </remarks>
public sealed class WebConfigYaziGeriOkuTests
{
    private static UpdateSiteHostingSettingsRequest Istek() => new();

    private static string GeciciKok()
    {
        var kok = Path.Combine(Path.GetTempPath(), "ada-webconfig-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(kok);
        return kok;
    }

    // =================================================================================
    // 1) MUTLU YOL + FAZLA KAPATMA NOBETCISI
    // =================================================================================

    [Fact]
    public void Yazma_basariliysa_GERI_OKUNUR_ve_panel_sade_kaydedildi_der()
    {
        var kok = GeciciKok();

        var olcum = SiteHostingSettingsService.UpdateWebConfig(kok, Istek(), () => true);

        Assert.True(olcum.Reparsed);
        Assert.True(olcum.Matches);
        Assert.True(olcum.Verified);
        Assert.False(olcum.Failed);
        Assert.False(olcum.RolledBack);
        Assert.True(olcum.IisProbed);
        Assert.True(olcum.IisAccepted);
        Assert.True(olcum.IisVerified);

        // FAZLA KAPATMA NOBETCISI: her sey OLCULDU ve yolundayken panel HALA sade "yapildi" demeli.
        Assert.Equal(string.Empty, olcum.NoticeKey);

        // Ve dosya GERCEKTEN diskte, GERCEKTEN ayristirilabilir.
        var yol = Path.Combine(kok, "web.config");
        Assert.True(File.Exists(yol));
        Assert.Equal("configuration", XDocument.Load(yol).Root!.Name.LocalName);
    }

    /// <summary>
    /// SORGU HATASI ile BOS SONUC ayni goruntuye DUSMEMELI: IIS'e sorulamadiysa cevap
    /// "reddetti" DEGIL, "olculemedi"dir -- ve panel bu durumda sade "kaydedildi" DEMEZ.
    /// </summary>
    [Fact]
    public void IIS_e_SORULAMADIYSA_bu_RET_degil_OLCULEMEDI_dir()
    {
        var kok = GeciciKok();

        var olcum = SiteHostingSettingsService.UpdateWebConfig(kok, Istek(), () => null);

        Assert.True(olcum.Verified);
        Assert.False(olcum.IisProbed);
        Assert.False(olcum.IisAccepted);
        Assert.False(olcum.IisVerified);
        Assert.False(olcum.RolledBack);
        Assert.Equal("sites.hosting_settings_iis_unverified", olcum.NoticeKey);

        // Olculemedi RET DEGILDIR: dosya diskte KALIR, geri alinmaz.
        Assert.True(File.Exists(Path.Combine(kok, "web.config")));
    }

    // =================================================================================
    // 2) IIS BELGEYI REDDEDERSE: SITE 500.19'DA BIRAKILMAZ
    // =================================================================================

    [Fact]
    public void IIS_REDDEDERSE_ONCEKI_icerik_BAYT_BAYT_geri_konur()
    {
        var kok = GeciciKok();
        var yol = Path.Combine(kok, "web.config");
        const string onceki =
            "<configuration><system.webServer><defaultDocument><files>"
            + "<add value=\"kullanicinin-kendi-belgesi.html\" /></files></defaultDocument>"
            + "</system.webServer></configuration>";
        File.WriteAllText(yol, onceki);
        var oncekiBaytlar = File.ReadAllBytes(yol);

        var olcum = SiteHostingSettingsService.UpdateWebConfig(kok, Istek(), () => false);

        Assert.True(olcum.IisProbed);
        Assert.False(olcum.IisAccepted);
        Assert.True(olcum.RolledBack);
        Assert.Equal("sites.hosting_settings_rejected_by_iis", olcum.NoticeKey);

        // ASIL KANIT: dosyanin ICERIGI eski hali. Panelin yazimi siteyi dusurmus halde birakmaz.
        Assert.Equal(oncekiBaytlar, File.ReadAllBytes(yol));
        Assert.Contains("kullanicinin-kendi-belgesi.html", File.ReadAllText(yol), StringComparison.Ordinal);
    }

    /// <summary>
    /// Dosya DAHA ONCE YOKTU. Geri alma SILME degildir: yerine GECERLI, zararsiz bir belge konur.
    /// </summary>
    [Fact]
    public void Dosya_yoktuysa_geri_alma_SILMEZ_gecerli_bos_belge_birakir()
    {
        var kok = GeciciKok();
        var yol = Path.Combine(kok, "web.config");
        Assert.False(File.Exists(yol));

        var olcum = SiteHostingSettingsService.UpdateWebConfig(kok, Istek(), () => false);

        Assert.True(olcum.RolledBack);
        Assert.True(File.Exists(yol));

        var belge = XDocument.Load(yol);
        Assert.Equal("configuration", belge.Root!.Name.LocalName);
        Assert.Empty(belge.Root!.Elements());
    }

    // =================================================================================
    // 3) YAZMA GERCEKTEN DUSERSE
    // =================================================================================

    /// <summary>
    /// Dosya BASKA BIR TUTAMAK tarafindan paylasimsiz aciksa <c>Save</c> gercekten duser.
    /// Eskiden bu istisna metottan disari cikar, ama hicbir OLCUM uretmezdi.
    /// </summary>
    [Fact]
    public void Dosya_KILITLIYSE_yazma_basarisiz_olur_ve_ONCEKI_icerik_bozulmaz()
    {
        var kok = GeciciKok();
        var yol = Path.Combine(kok, "web.config");
        const string onceki = "<configuration><system.webServer /></configuration>";
        File.WriteAllText(yol, onceki);

        WebConfigWriteMeasurement olcum;
        using (var kilit = new FileStream(yol, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.True(kilit.CanWrite);
            olcum = SiteHostingSettingsService.UpdateWebConfig(kok, Istek(), () => true);
        }

        Assert.False(olcum.Verified);
        Assert.False(olcum.Reparsed);
        Assert.True(olcum.WriteFailed);
        Assert.True(olcum.Failed);
        Assert.NotEqual(string.Empty, olcum.Detail);
        Assert.Equal("sites.hosting_settings_write_failed", olcum.NoticeKey);

        // Kilit yuzunden ONCEKI icerik hic bozulmadi.
        Assert.Equal(onceki, File.ReadAllText(yol));
    }

    /// <summary>
    /// BU TURDA OLCULDU: kilitli dosyada metodun ILK ifadesi (<c>XDocument.Load</c>) de
    /// korumasizdi -- ham bir <c>IOException</c> disari cikiyordu, yani panel icin ANLAMSIZ bir
    /// 500. Artik dokunmadan "yazilamadi" deniyor.
    /// </summary>
    [Fact]
    public void Mevcut_belge_BOZUKSA_uzerine_YAZILMAZ()
    {
        var kok = GeciciKok();
        var yol = Path.Combine(kok, "web.config");
        const string bozuk = "<configuration><system.webServer>"; // kapanmamis
        File.WriteAllText(yol, bozuk);

        var olcum = SiteHostingSettingsService.UpdateWebConfig(kok, Istek(), () => true);

        Assert.True(olcum.WriteFailed);
        Assert.False(olcum.Verified);
        Assert.Equal("sites.hosting_settings_write_failed", olcum.NoticeKey);

        // ASIL KANIT: kullanicinin dosyasi DEGISTIRILMEDI. Panel "duzelteyim" diye ustune yazmaz.
        Assert.Equal(bozuk, File.ReadAllText(yol));
    }

    /// <summary>Kok eleman &lt;configuration&gt; degilse de dokunulmaz (eskiden NRE atardi).</summary>
    [Fact]
    public void Kok_eleman_configuration_degilse_uzerine_YAZILMAZ()
    {
        var kok = GeciciKok();
        var yol = Path.Combine(kok, "web.config");
        const string yabanci = "<baskaBirKok><x /></baskaBirKok>";
        File.WriteAllText(yol, yabanci);

        var olcum = SiteHostingSettingsService.UpdateWebConfig(kok, Istek(), () => true);

        Assert.True(olcum.WriteFailed);
        Assert.Equal(yabanci, File.ReadAllText(yol));
    }

    // =================================================================================
    // 3b) DISKE INEN BELGE, YAZILMAK ISTENEN BELGE DEGILSE
    // =================================================================================

    /// <summary>
    /// ASIL KAPATILAN HATA. Eskiden <c>document.Save</c> metodun SON ifadesiydi: diske ne indigi
    /// HIC sorulmuyordu. Burada geri okuma FARKLI bir belge dondurur (yarim/bozuk yazim,
    /// araya giren baska bir surec) ve panel bunu "kaydedildi" SAYMAMALI.
    /// </summary>
    /// <remarks>
    /// Yazma ve GERI ALMA gercek dosya sisteminde olur; degistirilen tek sey diskin NE
    /// DONDURDUGUDUR -- cunku yarim bir yazimi gercek bir dosya sisteminde kasten uretmek
    /// mumkun degil.
    /// </remarks>
    [Fact]
    public void Diskten_FARKLI_belge_okunursa_yazim_BASARISIZ_sayilir_ve_geri_alinir()
    {
        var kok = GeciciKok();
        var yol = Path.Combine(kok, "web.config");
        const string onceki =
            "<configuration><system.webServer><defaultDocument><files>"
            + "<add value=\"kullanicinin-kendi-belgesi.html\" /></files></defaultDocument>"
            + "</system.webServer></configuration>";
        File.WriteAllText(yol, onceki);
        var oncekiBaytlar = File.ReadAllBytes(yol);

        var olcum = SiteHostingSettingsService.UpdateWebConfig(
            kok,
            Istek(),
            () => true,
            // DISK BASKA BIR SEY DONDURUYOR: yarim inmis bir belge.
            _ => XDocument.Parse("<configuration><system.webServer /></configuration>"));

        Assert.True(olcum.Reparsed);
        Assert.False(olcum.Matches);
        Assert.False(olcum.Verified);
        Assert.True(olcum.Failed);
        Assert.True(olcum.RolledBack);
        Assert.Equal("sites.hosting_settings_write_failed", olcum.NoticeKey);

        // GERI ALMA GERCEK: kullanicinin onceki web.config'i BAYT BAYT yerinde.
        Assert.Equal(oncekiBaytlar, File.ReadAllBytes(yol));
    }

    /// <summary>
    /// FAZLA KAPATMA NOBETCISI: geri okuma AYNI belgeyi dondurdugunde karsilastirma
    /// "farkli" DEMEMELI -- yoksa panel her dogru kaydi da reddederdi.
    /// </summary>
    [Fact]
    public void Diskten_AYNI_belge_okunursa_yazim_DOGRULANIR()
    {
        var kok = GeciciKok();

        var olcum = SiteHostingSettingsService.UpdateWebConfig(
            kok,
            Istek(),
            () => true,
            // Gercek geri okumanin yaptigi sey: dosyayi oldugu gibi ayristir.
            yol => XDocument.Load(yol, LoadOptions.None));

        Assert.True(olcum.Matches);
        Assert.True(olcum.Verified);
        Assert.False(olcum.RolledBack);
        Assert.Equal(string.Empty, olcum.NoticeKey);
    }

    // =================================================================================
    // 4) OLCUM TIPININ KARAR KURALI
    // =================================================================================

    [Fact]
    public void Geri_okunan_belge_FARKLIYSA_bu_BASARISIZ_yazimdir()
    {
        var olcum = new WebConfigWriteMeasurement { Reparsed = true, Matches = false, RolledBack = true };

        Assert.False(olcum.Verified);
        Assert.True(olcum.Failed);
        Assert.Equal("sites.hosting_settings_write_failed", olcum.NoticeKey);
    }

    [Fact]
    public void Hic_olculmemis_bir_yazma_KAYDEDILDI_sayilmaz()
    {
        var olcum = WebConfigWriteMeasurement.Olculemedi();

        Assert.False(olcum.Verified);
        Assert.False(olcum.Failed);
        Assert.False(olcum.IisVerified);
        Assert.Equal("sites.hosting_settings_unverified", olcum.NoticeKey);

        // DTO varsayilani da OLCULMEDI olmali: bir DTO kendiliginden "dogrulandi" dememeli.
        Assert.False(new SiteHostingSettingsDto().WebConfigWrite.Verified);
    }

    // =================================================================================
    // 5) IIS SONDASI -- BU MAKINEDE GERCEKTEN KOSTURULUR
    // =================================================================================

    /// <summary>
    /// <b>BU MAKINEDE OLCULDU.</b> <c>%windir%\System32\inetsrv\Microsoft.Web.Administration.dll</c>
    /// VAR, yani sonda gercek IIS yapilandirma sistemine dokunuyor. Surec YUKSELTILMEMIS olabilir.
    /// </summary>
    /// <remarks>
    /// OLCULEN sey su: sonda VAR OLMAYAN bir site adiyla cagrildiginda FIRLATMAZ ve "reddedildi"
    /// DEMEZ. Bu kritik: erisim reddini ya da bilinmeyen siteyi "web.config bozuk" saymak,
    /// panelin HER kaydi sahte bir hatayla reddetmesi olurdu. Test HICBIR SITE OLUSTURMAZ,
    /// HICBIR SEY YAZMAZ -- yalniz okur.
    /// </remarks>
    [Fact]
    public void IIS_sondasi_BILINMEYEN_site_icin_REDDEDILDI_demez()
    {
        var sonuc = SiteHostingSettingsService.IisWebConfigOkuyabiliyorMu(
            "adapanel-olcum-yok-" + Guid.NewGuid().ToString("N"));

        // Fail-safe yon: cevap ya "olculemedi" (null) ya da "kabul" (true) olabilir; ama
        // ASLA "reddedildi" (false) olmamali -- cunku olculen sey bozuk bir belge DEGIL.
        Assert.NotEqual(false, sonuc);
    }

    [Fact]
    public void IIS_sondasi_FIRLATMAZ()
    {
        // Sonda bir OLCUMDUR; dusmesi kaydi dusurmemeli.
        var kayit = Record.Exception(() =>
            SiteHostingSettingsService.IisWebConfigOkuyabiliyorMu("adapanel-olcum-yok"));

        Assert.Null(kayit);
    }

    // =================================================================================
    // 6) UPDATE ASYNC: BASARISIZ YAZIM ARTIK "KAYDEDILDI" DEMIYOR
    // =================================================================================

    /// <summary>
    /// Uc degerli olcumun HATA anahtarlarinin gercekten <see cref="AppException"/> mesaj
    /// anahtari olarak kullanilabilir oldugunu dogrular (bos anahtar 500 uretmemeli).
    /// </summary>
    [Fact]
    public void Basarisiz_olcumun_anahtari_HER_ZAMAN_doludur()
    {
        var basarisiz = new WebConfigWriteMeasurement { Reparsed = true, Matches = false };
        var olculemedi = WebConfigWriteMeasurement.Olculemedi();
        var reddedildi = new WebConfigWriteMeasurement
        {
            Reparsed = true,
            Matches = true,
            IisProbed = true,
            IisAccepted = false
        };

        Assert.NotEqual(string.Empty, basarisiz.NoticeKey);
        Assert.NotEqual(string.Empty, olculemedi.NoticeKey);
        Assert.NotEqual(string.Empty, reddedildi.NoticeKey);
        Assert.Equal("sites.hosting_settings_rejected_by_iis", reddedildi.NoticeKey);
    }
}
