using AdaPanel.Infrastructure.AppStore;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Uygulama mağazasının kurduğu Roundcube, site kökünden AÇILMAZ.
/// </summary>
/// <remarks>
/// <para>
/// <b>Kaynağından doğrulandı:</b> panelin bildiriminde sabitlenen sürümün
/// (<c>roundcubemail-1.7.1-complete</c>) ÜST DÜZEY <c>index.php</c> dosyasının tamamı tek satır:
/// <c>exit('Please, configure your HTTP server to point to the /public_html directory …')</c>.
/// Mağaza siteyi uygulama köküne bakar hâlde bırakıyordu; kurulum "başarılı" diyor, ziyaretçi ise
/// yalnız bu uyarıyı görüyordu. Laravel yolu aynı sorunu <c>UpdatePhysicalPathAsync</c> ile doğru
/// çözüyor; Roundcube'da o satır yoktu.
/// </para>
/// </remarks>
public sealed class AppStoreRoundcubeDocumentRootTests : IDisposable
{
    private readonly string kok = Path.Combine(Path.GetTempPath(), "ada-rc-" + Guid.NewGuid().ToString("N"));

    public AppStoreRoundcubeDocumentRootTests() => Directory.CreateDirectory(kok);

    public void Dispose()
    {
        try
        {
            Directory.Delete(kok, recursive: true);
        }
        catch
        {
            // Geçici klasör silinemedi; ölçümü etkilemez.
        }
    }

    private static string AppStoreKaynagi()
        => File.ReadAllText(Path.Combine(
            ScriptTestHost.RepoRoot, "src", "AdaPanel.Infrastructure", "AppStore", "AppStoreService.cs"));

    /// <summary>Depodaki gerçek kaynaktan bir metodun gövdesini süslü parantez eşleyerek çıkarır.</summary>
    private static string MetotGovdesi(string kaynak, string imza)
    {
        var basla = kaynak.IndexOf(imza, StringComparison.Ordinal);
        Assert.True(basla >= 0, $"Metot bulunamadı: {imza}");

        var acilis = kaynak.IndexOf('{', basla);
        Assert.True(acilis > 0, $"Gövde başlangıcı bulunamadı: {imza}");

        var derinlik = 0;
        for (var i = acilis; i < kaynak.Length; i++)
        {
            if (kaynak[i] == '{')
            {
                derinlik++;
            }
            else if (kaynak[i] == '}')
            {
                derinlik--;
                if (derinlik == 0)
                {
                    return kaynak[acilis..(i + 1)];
                }
            }
        }

        throw new InvalidOperationException($"Gövde kapanışı bulunamadı: {imza}");
    }

    [Fact]
    public void PublicHtml_Varsa_BelgeKokuOdur()
    {
        var docRoot = Path.Combine(kok, "public_html");
        Directory.CreateDirectory(docRoot);
        File.WriteAllText(Path.Combine(docRoot, "index.php"), "<?php // gercek giris");
        File.WriteAllText(Path.Combine(kok, "index.php"), "<?php exit('… point to the /public_html directory …');");

        Assert.True(AppStoreService.PathsEqual(docRoot, AppStoreService.ResolveRoundcubeDocumentRoot(kok)));
    }

    /// <summary>
    /// Klasör VARSAYILMAZ: <c>public_html/index.php</c> yoksa uygulama kökü döner. Var olmayan bir
    /// klasörü belge kökü yapmak siteyi büsbütün kırardı.
    /// </summary>
    [Fact]
    public void PublicHtml_Yoksa_UygulamaKokuKalir()
    {
        File.WriteAllText(Path.Combine(kok, "index.php"), "<?php // tek katmanli paket");

        Assert.True(AppStoreService.PathsEqual(kok, AppStoreService.ResolveRoundcubeDocumentRoot(kok)));
    }

    [Fact]
    public void BosPublicHtmlKlasoru_BelgeKokuSayilmaz()
    {
        Directory.CreateDirectory(Path.Combine(kok, "public_html"));

        Assert.True(AppStoreService.PathsEqual(kok, AppStoreService.ResolveRoundcubeDocumentRoot(kok)));
    }

    [Fact]
    public void YolKarsilastirmasi_BuyukKucukVeSondakiAyraciYokSayar()
    {
        Assert.True(AppStoreService.PathsEqual(@"C:\web\Site\", @"C:\WEB\site"));
        Assert.False(AppStoreService.PathsEqual(@"C:\web\site", @"C:\web\site\public_html"));
    }

    /// <summary>
    /// Kurulum akışı siteyi belge köküne YÖNLENDİRMELİ ve sonucu GERİ OKUMALI.
    /// </summary>
    /// <remarks>
    /// Saf yardımcıların testi tek başına yetmez: yardımcı doğru çalışsa bile kurulum akışı onu
    /// çağırmazsa site yine açılmaz. Bu yüzden akışın kendisi bekçileniyor — iki satırdan biri
    /// kaldırılırsa test kırmızı yanar.
    /// </remarks>
    [Fact]
    public void RoundcubeKurulumu_SiteyiBelgeKokuneYonlendirirVeDogrular()
    {
        var govde = MetotGovdesi(AppStoreKaynagi(), "private async Task InstallRoundcubeAsync(");

        Assert.Contains("ResolveRoundcubeDocumentRoot(applicationRoot)", govde, StringComparison.Ordinal);
        Assert.Contains("UpdatePhysicalPathAsync", govde, StringComparison.Ordinal);
        Assert.Contains("VerifyRoundcubeIsServedAsync", govde, StringComparison.Ordinal);
    }

    /// <summary>
    /// Laravel yolu bunu ZATEN doğru yapıyordu; düzeltme onu bozmamalı.
    /// </summary>
    [Fact]
    public void LaravelKurulumu_HalaPublicKlasorunuYayinlar()
    {
        var govde = MetotGovdesi(AppStoreKaynagi(), "private async Task InstallLaravelAsync(");


        Assert.Contains("UpdatePhysicalPathAsync", govde, StringComparison.Ordinal);
    }
}
