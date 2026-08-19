using AdaPanel.Infrastructure.SiteBuilder;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Görsel yükleme kapısı: yüklenen dosya GERÇEKTEN görsel mi.
/// </summary>
/// <remarks>
/// Yüklenen dosya yayında müşterinin IIS sitesinin altına kopyalanır. Oraya bir <c>.php</c> ya da
/// <c>.exe</c> girmesi doğrudan uzaktan kod çalıştırma demektir. Uzantı ve bildirilen tür
/// istemcinin yazdığı metindir, dolayısıyla karar dosyanın İÇERİĞİNE bakılarak verilmeli.
/// </remarks>
public sealed class SiteBuilderImageValidationTests
{
    [Fact]
    public void Gercek_gorseller_KABUL_edilir()
    {
        Assert.Equal(".png", Dogrula(SiteBuilderImageFixtures.MinimalPng()));
        Assert.Equal(".jpg", Dogrula(SiteBuilderImageFixtures.MinimalJpeg()));
        Assert.Equal(".gif", Dogrula(SiteBuilderImageFixtures.MinimalGif()));
        Assert.Equal(".webp", Dogrula(SiteBuilderImageFixtures.MinimalWebp()));
    }

    [Fact]
    public void CALISTIRILABILIR_dosya_REDDEDILIR()
    {
        var sonuc = SiteBuilderImageValidator.Validate(
            SiteBuilderImageFixtures.WindowsExecutable().LongLength,
            SiteBuilderImageFixtures.WindowsExecutable());

        Assert.False(sonuc.Accepted);
        Assert.Equal("sitebuilder.image_type_rejected", sonuc.ErrorKey);
    }

    [Fact]
    public void PHP_betigi_REDDEDILIR()
    {
        Assert.False(SiteBuilderImageValidator.Validate(
            SiteBuilderImageFixtures.PhpScript().LongLength,
            SiteBuilderImageFixtures.PhpScript()).Accepted);
    }

    [Fact]
    public void SVG_bilerek_REDDEDILIR()
    {
        // SVG bir belge biçimidir: içine <script> konabilir ve tarayıcı çalıştırır.
        // "Görsel yükleme" kapısından script geçirmenin en bilinen yolu budur.
        Assert.False(SiteBuilderImageValidator.Validate(
            SiteBuilderImageFixtures.ScriptedSvg().LongLength,
            SiteBuilderImageFixtures.ScriptedSvg()).Accepted);
    }

    [Fact]
    public void Arsiv_ve_HTML_REDDEDILIR()
    {
        Assert.False(SiteBuilderImageValidator.Validate(
            SiteBuilderImageFixtures.ZipArchive().LongLength,
            SiteBuilderImageFixtures.ZipArchive()).Accepted);

        Assert.False(SiteBuilderImageValidator.Validate(
            SiteBuilderImageFixtures.HtmlDocument().LongLength,
            SiteBuilderImageFixtures.HtmlDocument()).Accepted);
    }

    [Fact]
    public void CIFT_UZANTI_oyunu_ise_yaramaz()
    {
        // "kedi.php.jpg": ad ne olursa olsun karar içerikten verilir ve saklanan ad da
        // ÇÖZÜLEN türden üretilir. Bu yüzden çift uzantı diye bir kavram kalmaz.
        var betik = SiteBuilderImageFixtures.PhpScript();
        Assert.False(SiteBuilderImageValidator.Validate(betik.LongLength, betik).Accepted);

        var png = SiteBuilderImageFixtures.MinimalPng();
        var sonuc = SiteBuilderImageValidator.Validate(png.LongLength, png);
        var ad = SiteBuilderImageValidator.BuildStoredName(sonuc.Extension);
        Assert.EndsWith(".png", ad, StringComparison.Ordinal);
        Assert.True(SiteBuilderImageValidator.IsStoredNameValid(ad));
    }

    [Fact]
    public void Boyut_ustu_REDDEDILIR()
    {
        var sonuc = SiteBuilderImageValidator.Validate(
            SiteBuilderCatalog.MaxImageBytes + 1,
            SiteBuilderImageFixtures.MinimalPng());

        Assert.False(sonuc.Accepted);
        Assert.Equal("sitebuilder.image_too_large", sonuc.ErrorKey);
    }

    [Fact]
    public void Bos_dosya_REDDEDILIR()
    {
        var sonuc = SiteBuilderImageValidator.Validate(0, []);
        Assert.False(sonuc.Accepted);
        Assert.Equal("sitebuilder.image_empty", sonuc.ErrorKey);
    }

    [Fact]
    public void Saklanan_ad_kalibi_YOL_KACISINI_kapatir()
    {
        foreach (var kotu in new[]
                 {
                     "../../web.config",
                     "..\\..\\web.config",
                     "abc.php",
                     "abc.jpg.php",
                     "abc.svg",
                     "ABCDEF.jpg",
                     "kisa.jpg",
                     new string('a', 32) + ".exe",
                     new string('g', 32) + ".jpg",
                     ""
                 })
        {
            Assert.False(SiteBuilderImageValidator.IsStoredNameValid(kotu), $"kabul edilmemeliydi: {kotu}");
        }

        Assert.True(SiteBuilderImageValidator.IsStoredNameValid(new string('a', 32) + ".jpg"));
    }

    [Fact]
    public void Uretilen_adlar_CAKISMAZ()
    {
        var adlar = Enumerable.Range(0, 200)
            .Select(_ => SiteBuilderImageValidator.BuildStoredName(".png"))
            .ToList();

        Assert.Equal(adlar.Count, adlar.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    private static string Dogrula(byte[] veri)
    {
        var sonuc = SiteBuilderImageValidator.Validate(veri.LongLength, veri);
        Assert.True(sonuc.Accepted);
        return sonuc.Extension;
    }
}
