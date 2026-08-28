using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// PAKETSİZ RELEASE YAYINLANAMAZ.
///
/// <para>
/// <b>Neden var — yaşanmış hata (0.7.5):</b> <c>publish-github-release.ps1</c>
/// <c>-InstallerPath</c> verilmeden çalıştırıldı. Betik release'i oluşturdu, notları yazdı,
/// gövdeyi doğruladı, "Tamamlandi" dedi ve <b>sıfır</b> hata koduyla döndü — ama yayında
/// indirilecek hiçbir şey yoktu.
/// </para>
/// <para>
/// Bunun bedeli sessiz değildir: panelin kendi güncelleme ekranı yeni sürümü GÖRÜR, indirmeye
/// gider ve varlık bulamaz. Yani başarılı görünen bir yayın, kurulu bütün panellerde
/// güncellemeyi kırar.
/// </para>
/// <para>
/// Ders <c>HtaccessModulePackagingTests</c> ile aynı: bir şeyin ÜRETİLDİĞİNİ ölçmek yetmez,
/// TESLİM EDİLDİĞİ de ölçülmelidir. Orada eksik olan modülün pakete girmesiydi, burada paketin
/// yayına girmesi.
/// </para>
/// </summary>
public class ReleaseAssetGuardTests
{
    [Fact]
    public void Yayin_betigi_paketsiz_yayini_ENGELLER()
    {
        var betik = Betik();

        Assert.Contains("Paketsiz yayin ENGELLENDI", betik);
        // Engel, notlar GÖNDERİLMEDEN önce olmalı: sonra olsaydı release yine oluşurdu ve
        // hata yalnızca varlık adımında patlardı — yani tam olarak yaşanan durum.
        Assert.True(
            betik.IndexOf("Paketsiz yayin ENGELLENDI", StringComparison.Ordinal)
                < betik.IndexOf("$release = Publish-AdaGitHubRelease", StringComparison.Ordinal),
            "paketsiz yayin denetimi, release olusturulduktan SONRA yapiliyor -- o noktada is islenmis olur");
    }

    /// <summary>
    /// Yalnız notu düzeltmek MEŞRU bir iştir ve yasaklanmıyor; AÇIKÇA söylenmesi isteniyor.
    /// Kapıyı tamamen kapatmak, yazım hatası düzeltmek için her seferinde 86 MiB paket
    /// yüklemeye zorlardı — kural o kadar pahalı olunca etrafından dolaşılır.
    /// </summary>
    [Fact]
    public void Yalniz_not_guncellemenin_ACIK_bir_yolu_var()
    {
        var betik = Betik();

        Assert.Contains("-SkipAssetUpload", betik);
        Assert.Contains("-not $SkipAssetUpload", betik);
    }

    /// <summary>
    /// Paketi ZATEN OLAN bir yayının notu, paket tekrar yüklenmeden düzeltilebilmeli.
    /// Denetim "parametre verildi mi"ye değil, yayında paket OLUP OLMADIĞINA bakar.
    /// </summary>
    [Fact]
    public void Denetim_YAYINDAKI_pakete_bakar()
    {
        var betik = Betik();

        Assert.Contains("$mevcut.assets", betik);
    }

    private static string Betik()
        => File.ReadAllText(Path.Combine(KaynakKoku(), "scripts", "publish-github-release.ps1"));

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
