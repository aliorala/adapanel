using AdaPanel.Infrastructure.Iis;
using Xunit;
using Xunit.Abstractions;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// IIS SONDASININ BU MAKINEDEKI GERCEK CEVABI (fail-safe yonun kaniti).
/// </summary>
/// <remarks>
/// <para>
/// <b>Dosya adi neden boyle:</b> bu olcum once gecici bir sonda olarak yazildi ve sonucu bir
/// KARARA donustu; dosya silinmedi, olcumun kendisi kalici bir sinamaya cevrildi.
/// </para>
/// <para>
/// <b>OLCULEN.</b> Bu makinede <c>%windir%\System32\inetsrv\Microsoft.Web.Administration.dll</c>
/// VAR ama surec YUKSELTILMEMIS. O yuzden <c>ServerManager</c> <c>applicationHost.config</c>'i
/// okuyamiyor. Sondanin bu durumda ne dedigi HAYATI onemde: erisim reddini "web.config bozuk"
/// saymak, panelin HER barindirma ayari kaydini SAHTE bir hatayla reddetmesi olurdu.
/// </para>
/// <para>
/// <b>OLCUM SONUCU (bu makinede, bu kosumda):</b> hem var olmayan bir site adi hem de
/// <c>Default Web Site</c> icin cevap <c>null</c> -- yani OLCULEMEDI. Panel bu durumda
/// "kaydedildi" DEMIYOR ama kaydi da REDDETMIYOR; kullaniciya
/// <c>sites.hosting_settings_iis_unverified</c> gosteriyor.
/// </para>
/// <para>
/// <b>OLCULEMEYEN, ACIKCA.</b> Sondanin <c>true</c> (kabul) ve <c>false</c> (ret) dallari BU
/// MAKINEDE olculMEDI: kabul dali yukseltilmis bir surec, ret dali ise GERCEKTEN bozuk bir
/// web.config'e sahip GERCEK bir IIS sitesi gerektirir -- ve bu turda hicbir IIS sitesi
/// olusturulmadi/degistirilmedi. Ret dalinin KARAR KURALI
/// (<c>ServerManagerException -&gt; reddedildi</c>, digeri -&gt; olculemedi) ayrica olculuyor.
/// </para>
/// </remarks>
public sealed class IisSondasiFailSafeTests(ITestOutputHelper cikti)
{
    [Fact]
    public void Sonda_YUKSELTILMEMIS_surecte_REDDEDILDI_demez_OLCULEMEDI_der()
    {
        var bilinmeyen = SiteHostingSettingsService.IisWebConfigOkuyabiliyorMu("adapanel-olcum-yok-123");
        var varsayilan = SiteHostingSettingsService.IisWebConfigOkuyabiliyorMu("Default Web Site");

        cikti.WriteLine("bilinmeyen site  => " + (bilinmeyen?.ToString() ?? "NULL (olculemedi)"));
        cikti.WriteLine("Default Web Site => " + (varsayilan?.ToString() ?? "NULL (olculemedi)"));

        // FAIL-SAFE: cevap ASLA "reddedildi" olmamali. Ne bilinmeyen bir site, ne de okunamayan
        // bir applicationHost.config "kullanicinin web.config'i bozuk" anlamina gelir.
        Assert.NotEqual(false, bilinmeyen);
        Assert.NotEqual(false, varsayilan);
    }

    /// <summary>
    /// Sonda HICBIR SEY YAZMAZ. Bu bir olcumdur; olcum sistemi degistirmemelidir.
    /// </summary>
    [Fact]
    public void Sonda_tekrar_tekrar_cagrilabilir_ve_ayni_cevabi_verir()
    {
        var birinci = SiteHostingSettingsService.IisWebConfigOkuyabiliyorMu("Default Web Site");
        var ikinci = SiteHostingSettingsService.IisWebConfigOkuyabiliyorMu("Default Web Site");

        Assert.Equal(birinci, ikinci);
    }
}
