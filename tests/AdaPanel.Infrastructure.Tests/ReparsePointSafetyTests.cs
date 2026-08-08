using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// KLASOR BAGLANTISI (junction) uzerinde ozyinelemeli silmenin YASAK olmasi.
/// <para>
/// RISK: FTP kullanicilarinin kok klasoru bir junction'dir ve HEDEFI musterinin SITE KOKUDUR.
/// Windows PowerShell 5.1'in bilinen reparse-point hatasinda <c>Remove-Item -Recurse</c>
/// baglantiyi TAKIP EDER ve hedefteki dosyalari siler. Yani:
/// </para>
/// <list type="bullet">
/// <item>bir FTP kullanicisini silmek ya da ana dizinini degistirmek,</item>
/// <item>ya da paneli KALDIRMAK (veri koku ozyinelemeli silinir),</item>
/// </list>
/// <para>
/// musterinin butun site dosyalarini goturebilirdi. Yedekten donmeden geri alinamaz.
/// </para>
/// <para>
/// <c>[IO.Directory]::Delete</c> baglantiyi takip etmez; yalniz baglantinin kendisini kaldirir.
/// Bu makinede gercek bir junction + gercek dosyalarla dogrulandi: baglanti kalkti, hedefteki
/// dosyalar korundu.
/// </para>
/// </summary>
public sealed class ReparsePointSafetyTests
{
    /// <summary>
    /// FTP kullanici kokunu kaldiran parca, once "bu bir baglanti mi" diye sorar.
    /// </summary>
    /// <remarks>
    /// Bu denetim METİN tabanlıdır ve kırılgandır (yazım değişirse yanıltır) — ama BİLEREK
    /// duruyor. Yerine yazılan gerçek davranış testi (<see cref="FtpUserRootDeletionBehaviourTests"/>)
    /// ölçüldü: koruma kaldırılınca bile GEÇİYOR, çünkü bu makinedeki PowerShell 5.1 yapısı
    /// junction'ı zaten takip etmiyor (hata sonraki yapılarda düzeltilmiş). Yani tehlikeli davranış
    /// burada üretilemiyor ve koruyucu dalın VARLIĞINI doğrulayan tek şey bu metin denetimi.
    /// Silinseydi kapsam artmaz, azalırdı.
    /// </remarks>
    [Fact]
    public void TheFtpUserRoot_IsNeverDeletedRecursivelyWhenItIsALink()
    {
        var kaynak = OkuKaynak("src", "AdaPanel.Infrastructure", "Ftp", "FtpService.cs");

        Assert.Contains("[IO.FileAttributes]::ReparsePoint", kaynak, StringComparison.Ordinal);
        Assert.Contains("[IO.Directory]::Delete($userRoot)", kaynak, StringComparison.Ordinal);
    }

    /// <summary>
    /// Ayni mantik TEK YERDE durur ve iki cagri yerinde de kullanilir. Kopyalanmis bir kalip,
    /// birini duzeltip digerini unutmanin kestirme yoludur -- bu depoda tam olarak boyle oldu.
    /// </summary>
    [Fact]
    public void TheSafeRemovalLogic_HasASingleSource()
    {
        var kaynak = OkuKaynak("src", "AdaPanel.Infrastructure", "Ftp", "FtpService.cs");

        // Görünürlük anahtarına BAĞLANMA. Bu satır "private const" arıyordu ve sabit testten
        // erişilebilsin diye "internal" yapılınca test kırmızıya döndü — davranış hiç değişmemişken.
        // Metin tabanlı denetimin kırılganlığının canlı örneği; kapsamı daraltmadan gevşetiliyor.
        Assert.Contains("const string RemoveUserRootSnippet", kaynak, StringComparison.Ordinal);

        // Bir tanim + iki kullanim.
        var kullanim = System.Text.RegularExpressions.Regex.Matches(kaynak, @"\{\{RemoveUserRootSnippet\}\}").Count;
        Assert.True(kullanim >= 2, $"Guvenli kaldirma yalnizca {kullanim} yerde kullaniliyor; kullanici kokunu silen her yol kullanmali.");

        // Korumasiz cagri kalmamali.
        Assert.DoesNotContain("Remove-Item -LiteralPath '{{EscapePowerShellLiteral(userRoot)}}' -Force -Recurse", kaynak, StringComparison.Ordinal);
    }

    /// <summary>
    /// PANELI KALDIRMAK musterinin sitelerini silmemeli: veri koku ozyinelemeli silinmeden once
    /// icindeki butun baglantilar kaldirilir.
    /// </summary>
    [Fact]
    public void TheUninstallScript_UnlinksBeforeItDeletes()
    {
        var betik = OkuKaynak("scripts", "uninstall-cleanup.ps1");

        Assert.Contains("Remove-AdaReparsePoints", betik, StringComparison.Ordinal);

        var temizleme = betik.IndexOf("Remove-AdaReparsePoints -Root $expandedPath", StringComparison.Ordinal);
        var silme = betik.IndexOf("Remove-Item -LiteralPath $expandedPath -Recurse", StringComparison.Ordinal);

        Assert.True(temizleme > 0, "Kaldirma betigi baglantilari hic temizlemiyor.");
        Assert.True(silme > temizleme, "Veri koku, baglantilar kaldirilmadan ONCE siliniyor.");
    }

    /// <summary>
    /// Dolasim baglantilarin ICINE GIRMEMELI: girerse hedefteki klasorleri de gezer ve oradan
    /// silmeye baslar. Reparse point bulununca ALT KLASORLERINE inilmez.
    /// </summary>
    [Fact]
    public void TheTraversal_DoesNotDescendIntoLinks()
    {
        var betik = OkuKaynak("scripts", "uninstall-cleanup.ps1");

        // Reparse ise baglanti kaldirilir; DEGILSE yigina eklenir (yalniz gercek klasorlere inilir).
        var baglantiKaldirma = betik.IndexOf("[System.IO.Directory]::Delete($alt)", StringComparison.Ordinal);
        var yiginaEkleme = betik.IndexOf("$bekleyen.Push($alt)", StringComparison.Ordinal);

        Assert.True(baglantiKaldirma > 0, "Baglanti kaldirma yok.");
        Assert.True(yiginaEkleme > baglantiKaldirma, "Alt klasore inme, baglanti kontrolunden ONCE geliyor.");
    }

    private static string OkuKaynak(params string[] parts)
    {
        var klasor = AppContext.BaseDirectory;
        for (var derinlik = 0; derinlik < 8; derinlik++)
        {
            var aday = Path.Combine(new[] { klasor }.Concat(parts).ToArray());
            if (File.Exists(aday))
            {
                return File.ReadAllText(aday);
            }

            klasor = Path.Combine(klasor, "..");
        }

        throw new FileNotFoundException(string.Join('/', parts));
    }
}
