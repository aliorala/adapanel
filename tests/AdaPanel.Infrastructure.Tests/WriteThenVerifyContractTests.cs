using System.Text.RegularExpressions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// "HATA VERMEDI" ILE "OLDU" AYNI SEY DEGILDIR — yazan yol, yazdigini GERI OKUR.
///
/// <para>
/// <b>Neden var — bu depoda tekrar tekrar olculmus bir sinif.</b> Panel dis bir sisteme
/// (IIS, Gorev Zamanlayici, Windows) bir sey yaziyor, cagri basarili donuyor, ama sonuc olusmuyor.
/// Bugune kadar bulunan ornekler:
/// </para>
/// <list type="number">
/// <item><c>Set-WebConfigurationProperty -Location</c> FTP ayarlarinda SESSIZCE hicbir sey
/// yapmiyordu; betik yine basarili donuyordu.</item>
/// <item>PowerShell'in cikis kodu SON ifadenin sonucudur — ortada patlayan bir ifade cikis koduna
/// hic yansimiyordu (<c>RunPowerShellStrictAsync</c> bu yuzden yazildi).</item>
/// <item><c>SchedulerService</c> kayittan sonra geri okuyordu ama BULAMAYINCA uydurma bir DTO
/// donuyordu (<c>State = "Ready"</c>): gorev Gorev Zamanlayici'da yokken panel "Hazir" diyordu.</item>
/// <item><c>WinAcmeSslService</c> HTTPS yonlendirme betigi sekiz yazma cagrisindan sonra
/// KOSULSUZ <c>'OK'</c> basiyordu; yarim kalmis bir kural "yonlendirme acik" diye raporlanirdi.</item>
/// </list>
/// <para>
/// Bu test, kapatilan iki noktanin GERI ACILMASINI engeller. Kaynak metnine bakar, cunku olculen
/// sey bir deger degil bir DAVRANIS SEKLIDIR: "yazdiktan sonra geri oku" ve "kosulsuz basari
/// bildirme".
/// </para>
/// </summary>
public sealed class WriteThenVerifyContractTests
{
    /// <summary>
    /// HTTPS yonlendirme betigi kati kipte olmali ve yazdigini geri okumali.
    /// </summary>
    [Fact]
    public void Https_yonlendirme_betigi_KOSULSUZ_OK_basmaz()
    {
        var kaynak = Oku("src", "AdaPanel.Infrastructure", "Ssl", "WinAcmeSslService.cs");

        /*
         * (a) Kati kip: ortada patlayan bir cagri betigi DUSURSUN.
         *
         * DENETIM BILEREK DAR VE DIZILIME BAGLI. Ilk yazdigim hali
         * `Assert.Contains("$ErrorActionPreference = 'Stop'")` idi ve BOS BIR GUVENCEYDI: bu
         * dosyada o satirdan ZATEN BES TANE vardi (baska betiklerde), yani duzeltmem geri
         * alinsa bile test yesil kalirdi. Mutasyon bunu yakaladi -- korumayi kasten bozdum ve
         * test kirmizi YANMADI. Simdi tam diziliyi ariyor: yonlendirme betiginin ilk uc satiri.
         */
        Assert.Matches(
            @"\$ErrorActionPreference = 'Stop'\s*\r?\n\s*Import-Module WebAdministration -ErrorAction Stop\s*\r?\n\s*if \(-not \(Get-WebGlobalModule",
            kaynak);

        // (b) Geri okuma: hata firlatmadan eksik kalan durum da yakalansin. Kati kip TEK BASINA
        //     yetmez -- bir cagri hata vermeden de eksik birakabilir.
        Assert.Contains("kural YARIM", kaynak, StringComparison.Ordinal);

        // (c) Okuma yolu da kuralin TAM oldugunu sorsun: yalnizca "bu adda kural var mi" demek,
        //     yarim yazilmis bir kurali "acik" diye gostermekti.
        Assert.Matches(@"filter\s+""\$r/action""\s+-name\s+'type'", kaynak);
    }

    /// <summary>
    /// Zamanlanmis gorev kaydi DOGRULANMADAN "Hazir" donmemeli.
    /// </summary>
    [Fact]
    public void Zamanlanmis_gorev_kaydi_uydurma_DTO_ile_maskelenmiyor()
    {
        var kaynak = Oku("src", "AdaPanel.Infrastructure", "Scheduler", "SchedulerService.cs");

        // Kayittan sonraki geri okuma, bulunamayinca ATMALI. Eski hal `?? new ScheduledTaskDto`
        // ile devam edip State="Ready" yaziyordu.
        Assert.Contains("cron.write_unverified", kaynak, StringComparison.Ordinal);

        // Kayit yolunda uydurma DTO kalibi kalmamali. Desen dar: yalnizca "bulamazsan nesne uydur"
        // birlesimi yasak; DTO'nun baska yerlerde olusturulmasi mesrudur.
        Assert.False(
            Regex.IsMatch(kaynak, @"\?\?\s*new\s+ScheduledTaskDto"),
            "SchedulerService icinde 'bulunamazsa uydur' kalibi geri gelmis: "
            + "gorev Gorev Zamanlayici'da yokken panel yine 'Hazir' der.");
    }

    /// <summary>
    /// Yukaridaki desenlerin GERCEKTEN yakaladiginin kaniti — sentetik girdiyle.
    /// </summary>
    /// <remarks>
    /// Taramalar bugun kaynak temiz oldugu icin geciyor; desenler hic eslesmese de gecerdi.
    /// Kapsamin kendisi olculmeli, yoksa koruma sessizce bosa duser.
    /// </remarks>
    [Fact]
    public void Yasak_desen_gercekten_yakaliyor()
    {
        const string Eski = "return tasks.FirstOrDefault(t => t.Name == n)\n    ?? new ScheduledTaskDto { State = \"Ready\" };";
        const string Yeni = "return kaydedilen;";

        Assert.Matches(@"\?\?\s*new\s+ScheduledTaskDto", Eski);
        Assert.DoesNotMatch(@"\?\?\s*new\s+ScheduledTaskDto", Yeni);
    }

    private static string Oku(params string[] parcalar)
    {
        var yol = Path.Combine(KaynakKoku(), Path.Combine(parcalar));
        Assert.True(File.Exists(yol), $"kaynak bulunamadi: {yol}");
        return File.ReadAllText(yol);
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
