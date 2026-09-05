using System.Xml.Linq;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Infrastructure.Iis;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// ADAY 5 (<c>SiteAliasService.YonlendirmeKuraliniYaz</c>) ve ADAY 6
/// (<c>SiteHostingSettingsService</c> icindeki <c>Running</c> iddialari).
/// </summary>
/// <remarks>
/// <para>
/// <b>ADAY 5 -- OLCULEN HATA.</b> Kural yazimi tumuyle LOG-ONLY bir <c>catch</c> icindeydi ve
/// <c>belge.Save</c> son ifadeydi. Kullanici "birincile yonlendir"i isaretliyor, panel takma adi
/// kaydediyor ve YONLENDIRME ACIK gorunuyordu; kural hic yazilmamis olabilirdi.
/// </para>
/// <para>
/// <b>ADAY 6 -- OLCULEN HATA.</b> <c>Running = false</c> (ve kardesi <c>Running = true</c>)
/// hicbir olcume dayanmadan yaziliyordu. Ustelik silme hatasi "gorev zaten yok" varsayilarak
/// YUTULUYORDU: silme GERCEKTEN basarisiz oldugunda da panel "durduruldu" diyordu.
/// </para>
/// <para>
/// <b>OLCULEMEYEN, ACIKCA.</b> Bu makinede Gorev Zamanlayici'da panel gorevi YOK ve surec
/// YUKSELTILMEMIS; gercek bir <c>schtasks</c> silme/olusturma turu OLCULMEDI. Ayrica gercek bir
/// IIS sitesi olusturulmadi. OLCULEN sey, panelin sonucu NASIL OKUDUGU ve NASIL ANLATTIGIDIR --
/// ve kural okuma yolu GERCEK web.config dosyalari uzerinde kosar.
/// </para>
/// </remarks>
public sealed class TakmaAdKuraliVeIsciDurumuTests
{
    // =====================================================================================
    // ADAY 5 -- YONLENDIRME KURALI OLCUMU
    // =====================================================================================

    [Fact]
    public void Kural_BEKLENIYOR_ve_DISKTE_VAR_ise_panel_sade_yapildi_der()
    {
        var olcum = new AliasRedirectRuleMeasurement
        {
            Measured = true,
            RuleExpected = true,
            RulePresent = true,
            RedirectedHostCount = 2
        };

        Assert.True(olcum.Applied);
        Assert.False(olcum.NotApplied);
        Assert.False(olcum.Unverified);

        // FAZLA KAPATMA NOBETCISI.
        Assert.Equal(string.Empty, olcum.NoticeKey);
    }

    /// <summary>ASIL HATA: kural bekleniyor ama diskte YOK -> yonlendirme calismayacak.</summary>
    [Fact]
    public void Kural_BEKLENIYOR_ama_DISKTE_YOK_ise_UYGULANMADI_denir()
    {
        var olcum = new AliasRedirectRuleMeasurement
        {
            Measured = true,
            RuleExpected = true,
            RulePresent = false
        };

        Assert.False(olcum.Applied);
        Assert.True(olcum.NotApplied);
        Assert.Equal("alias.redirect_rule_not_applied", olcum.NoticeKey);
    }

    /// <summary>
    /// Kural KALDIRILMALIYDI ama hala duruyor: bu da sessizce "tamam" sayilamaz -- ziyaretci
    /// artik istenmeyen bir yonlendirmeye takilir.
    /// </summary>
    [Fact]
    public void Kural_KALDIRILMALIYDI_ama_duruyorsa_da_UYGULANMADI_denir()
    {
        var olcum = new AliasRedirectRuleMeasurement
        {
            Measured = true,
            RuleExpected = false,
            RulePresent = true
        };

        Assert.True(olcum.NotApplied);
        Assert.Equal("alias.redirect_rule_not_applied", olcum.NoticeKey);
    }

    /// <summary>Okunamayan sonuc "yazilmadi" DEGIL, BILINMIYOR'dur.</summary>
    [Fact]
    public void Geri_okuma_YAPILAMADIYSA_sonuc_YAZILMADI_degil_OLCULEMEDI_dir()
    {
        var olcum = AliasRedirectRuleMeasurement.Olculemedi("web.config okunamadi");

        Assert.True(olcum.Unverified);
        Assert.False(olcum.Applied);
        Assert.False(olcum.NotApplied);
        Assert.Equal("alias.redirect_rule_unverified", olcum.NoticeKey);
    }

    [Fact]
    public void Takma_ad_calisma_alani_varsayilani_OLCULMEDI_dir()
    {
        var workspace = new SiteAliasWorkspaceDto();

        Assert.True(workspace.RedirectRule.Unverified);
        Assert.Equal("alias.redirect_rule_unverified", workspace.RedirectRuleNoticeKey);
    }

    /// <summary>
    /// Kural okuma yolu GERCEK bir web.config dosyasi uzerinde kosar: panelin KENDI kuralini
    /// (ada gore) tanimasi olculur, kullanicinin kendi kurallari onu YANILTMAZ.
    /// </summary>
    [Fact]
    public void Kural_arama_YALNIZ_panelin_kendi_kuralini_tanir()
    {
        var kok = Path.Combine(Path.GetTempPath(), "ada-alias-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(kok);
        var yol = Path.Combine(kok, "web.config");

        // Kullanicinin KENDI kurali var, panelinki YOK.
        File.WriteAllText(
            yol,
            "<configuration><system.webServer><rules>"
            + "<rule name=\"WordPress\"><match url=\".*\" /></rule>"
            + "</rules></system.webServer></configuration>");

        Assert.False(SiteAliasService.KuralDiskteVarMi(yol));

        // Panelin kurali eklendiginde tanir.
        File.WriteAllText(
            yol,
            "<configuration><system.webServer><rules>"
            + "<rule name=\"WordPress\"><match url=\".*\" /></rule>"
            + "<rule name=\"AdaPanel.AliasRedirect\"><match url=\"(.*)\" /></rule>"
            + "</rules></system.webServer></configuration>");

        Assert.True(SiteAliasService.KuralDiskteVarMi(yol));
    }

    /// <summary>
    /// SORGU HATASI ile BOS SONUC AYNI GORUNTUYE DUSMEMELI: okunamayan bir web.config
    /// "kural yok" DEGILDIR.
    /// </summary>
    [Fact]
    public void Okunamayan_web_config_KURAL_YOK_degil_OLCULEMEDI_dir()
    {
        var kok = Path.Combine(Path.GetTempPath(), "ada-alias-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(kok);

        // BOZUK XML: ayristirilamaz -> OLCULEMEDI (null).
        var bozuk = Path.Combine(kok, "web.config");
        File.WriteAllText(bozuk, "<configuration><system.webServer>");
        Assert.Null(SiteAliasService.KuralDiskteVarMi(bozuk));

        // DOSYA HIC YOK: bu OLCULMUS bir "kural yok"tur (false), olculemedi (null) degil.
        Assert.False(SiteAliasService.KuralDiskteVarMi(Path.Combine(kok, "hic-yok.config")));
    }

    // =====================================================================================
    // ADAY 6 -- ISCI DURUMU
    // =====================================================================================

    /// <summary>
    /// Hicbir olcum yapilmadan uretilen bir durum kendini "olculdu" ILAN ETMEZ. Eski kod tam
    /// olarak bunu yapiyordu: <c>new QueueWorkerStatusDto { Running = false }</c>.
    /// </summary>
    [Fact]
    public void Olculmemis_isci_durumu_kendini_OLCULDU_ilan_etmez()
    {
        var durum = new QueueWorkerStatusDto { Running = false, TaskName = "Laravel-x-Queue" };

        Assert.False(durum.Measured);
        Assert.Equal("sites.queue_worker_state_unverified", durum.NoticeKey);
    }

    [Fact]
    public void Olculmus_durum_sade_gosterilir()
    {
        var durum = new QueueWorkerStatusDto
        {
            Running = false,
            TaskName = "Laravel-x-Queue",
            Measured = true,
            Detail = "Gorev kayitli degil."
        };

        // FAZLA KAPATMA NOBETCISI: olculmus ve yolunda bir durum uyari uretmez.
        Assert.Equal(string.Empty, durum.NoticeKey);
    }

    /// <summary>
    /// "Durduruldu" ile "hala calisiyor" AYRI cumlelerdir; ikisi de OLCULMUS olabilir.
    /// Eski kodda ikinci cumle HIC uretilemiyordu.
    /// </summary>
    [Fact]
    public void Silme_basarisiz_olsa_bile_durum_OLCULEBILIR_ve_HALA_CALISIYOR_denir()
    {
        var haladuruyor = new QueueWorkerStatusDto
        {
            Running = true,
            TaskName = "Laravel-x-Queue",
            Measured = true,
            Detail = "Gorev HALA kayitli: Laravel-x-Queue. Silme hatasi: cron.delete_failed."
        };

        Assert.True(haladuruyor.Measured);
        Assert.True(haladuruyor.Running);
        Assert.Contains("HALA kayitli", haladuruyor.Detail, StringComparison.Ordinal);

        // KISMI BASARI ADIYLA: hangi gorev, hangi hata.
        Assert.Contains("cron.delete_failed", haladuruyor.Detail, StringComparison.Ordinal);
    }
}
