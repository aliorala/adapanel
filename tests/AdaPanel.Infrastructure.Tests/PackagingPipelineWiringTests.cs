using System.Text.RegularExpressions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Paketleme hatlarinin DENETIMLERE BAGLI olmasi.
/// </summary>
/// <remarks>
/// <para>
/// <c>scripts/audit-component-packages.ps1</c> VARDI ama hicbir yerden CAGRILMIYORDU. Cagrilmayan
/// bir denetim, olmayan bir denetimdir: dosya depoda durdugu icin "denetliyoruz" sanilir.
/// Bu testler hatta bagli olmayi sabitler.
/// </para>
/// <para>
/// Metin tabanlidir ve kirilgandir. Alternatifi hatti gercekten kosturmakti: npm build, tam test
/// kosumu, self-contained publish ve yuzlerce megabaytlik bilesen indirmesi -- bir birim testinde
/// yapilamaz. Kirilganligi kabul edip kapsami koruyoruz.
/// </para>
/// </remarks>
public sealed class PackagingPipelineWiringTests
{
    [Theory]
    [InlineData("build-installer.ps1")]
    [InlineData("package-installer.ps1")]
    public void Paketleme_betiklerinde_sabit_surum_varsayilani_YOK(string betikAdi)
    {
        var betik = BetigiOku(betikAdi);

        // "$Version = '0.2.28'" gibi bir varsayilan tam olarak sonsuz guncelleme dongusunu uretti.
        var sabitVarsayilan = Regex.Match(betik, @"\$Version\s*=\s*[""'](\d+\.\d+[^""']*)[""']");

        Assert.False(
            sabitVarsayilan.Success,
            $"{betikAdi} hala sabit bir surum varsayilani tasiyor: {sabitVarsayilan.Value}. " +
            "Surum Directory.Build.props'tan OKUNMALI.");
    }

    [Theory]
    [InlineData("build-installer.ps1")]
    [InlineData("package-installer.ps1")]
    public void Paketleme_betikleri_surum_kapisini_kullanir(string betikAdi)
    {
        var betik = BetigiOku(betikAdi);

        Assert.Contains("version-guard.ps1", betik);
        Assert.Contains("Resolve-AdaReleaseVersion", betik);
    }

    [Theory]
    [InlineData("build-installer.ps1")]
    [InlineData("package-installer.ps1")]
    public void Paketleme_betikleri_publish_icerigini_dogrular(string betikAdi)
    {
        var betik = BetigiOku(betikAdi);

        Assert.Contains("verify-publish-output.ps1", betik);
    }

    [Theory]
    [InlineData("build-installer.ps1")]
    [InlineData("package-installer.ps1")]
    public void Bilesen_paketi_denetimi_hatta_baglidir(string betikAdi)
    {
        var betik = BetigiOku(betikAdi);

        Assert.Contains("audit-component-packages.ps1", betik);
    }

    /// <summary>
    /// Icerik denetimi, kurulum paketi URETILMEDEN once calismali.
    /// </summary>
    /// <remarks>
    /// Denetim ISCC'den sonra kosarsa bulasik paket zaten uretilmis olur; "uretme" ile "uretip
    /// sonra sikayet et" ayni sey degildir.
    /// </remarks>
    [Theory]
    [InlineData("build-installer.ps1")]
    [InlineData("package-installer.ps1")]
    public void Icerik_denetimi_ISCC_derlemesinden_ONCE_kosar(string betikAdi)
    {
        var betik = BetigiOku(betikAdi);

        var denetim = betik.IndexOf("verify-publish-output.ps1", StringComparison.Ordinal);
        var derleme = betik.IndexOf("$iscc \"/DMyAppVersion", StringComparison.Ordinal);

        Assert.True(denetim > 0, $"{betikAdi}: icerik denetimi cagrisi yok.");
        Assert.True(derleme > 0, $"{betikAdi}: ISCC cagrisi bulunamadi.");
        Assert.True(denetim < derleme, $"{betikAdi}: icerik denetimi ISCC'den SONRA kosuyor.");
    }

    /// <summary>
    /// README paketleme komutu, surumun tek kaynaktan geldigini soylemeli.
    /// </summary>
    /// <remarks>
    /// Zincir README'deki parametresiz komutla basliyordu; belge duzeltilmezse ayni hata elle
    /// tekrar uretilir.
    /// </remarks>
    [Fact]
    public void README_surumun_tek_kaynagini_anlatir()
    {
        var readme = File.ReadAllText(ScriptTestHost.ScriptPath("README.md"));

        Assert.Contains("Directory.Build.props", readme);
        Assert.Contains("version-guard.ps1", readme);
    }

    private static string BetigiOku(string betikAdi)
    {
        var yol = ScriptTestHost.ScriptPath("scripts", betikAdi);
        Assert.True(File.Exists(yol), $"Betik bulunamadi: {yol}");
        return File.ReadAllText(yol);
    }
}
