using System.Text.RegularExpressions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Adminer tek tik girisinin sozlesmesi.
/// <para>
/// TASARIM DEGISTI (0.2.97): sarmalayici artik Adminer'in GIRIS NOKTASI DEGIL. Yalnizca oturumu
/// kurar ve kullaniciyi Adminer'in KENDI adresine yonlendirir; Adminer'i hic yuklemez.
/// </para>
/// <para>
/// Onceki tasarim (sonunda <c>require index.php</c>) Adminer'in HER istegini bu dosyadan
/// geciriyordu ve her gecis ayri bir kirilma noktasi uretti — dordu de canlida yasandi:
/// eklenti sinifi 5.x'te ad alanina tasinmisti; tek kullanimlik belirtec ikinci istekte yoktu;
/// Adminer cerez yolunu kendi gordugu adresten turetiyordu ve vekilin arkasinda eslesmiyordu;
/// Adminer oturumda kendi belirtecini ariyordu ve otomatik giris form akisini atladigi icin o
/// deger hic olusmuyordu ("Session expired").
/// </para>
/// <para>
/// Kullanicinin kendi gozlemi yolu gosterdi: aracin KENDI adresi
/// (<c>/api/v1/dbtools/adminer/?server=...</c>) zaten sorunsuz calisiyordu.
/// </para>
/// </summary>
public sealed class AdminerSignonContractTests
{
    /// <summary>
    /// Sarmalayici Adminer'i YUKLEMEZ. Bu satirin geri gelmesi, yukaridaki dort hatanin da
    /// yuzeyini geri getirir.
    /// </summary>
    [Fact]
    public void TheWrapper_NeverLoadsAdminerItself()
    {
        var php = OkuSarmalayici();

        Assert.DoesNotContain("require __DIR__", php, StringComparison.Ordinal);
        Assert.DoesNotContain("adminer_object", php, StringComparison.Ordinal);
        Assert.DoesNotContain("extends", php, StringComparison.Ordinal);
    }

    /// <summary>Is bitince kullanici Adminer'in KENDI adresine birakilir.</summary>
    [Fact]
    public void TheUser_IsHandedOverToAdminersOwnAddress()
    {
        var php = OkuSarmalayici();

        Assert.Contains("header('Location: ' . $adaHedef", php, StringComparison.Ordinal);
        Assert.Contains("'./?'", php, StringComparison.Ordinal);
    }

    /// <summary>
    /// Oturum, Adminer'in KENDI bicimiyle doldurulur (gercek Adminer 5.4.2 oturumu okunarak
    /// dogrulandi). Uc anahtarin ucu de gerekli: parola, secili veritabani ve Adminer'in
    /// her istekte aradigi belirtec.
    /// </summary>
    [Fact]
    public void TheSession_IsFilledInAdminersOwnShape()
    {
        var php = OkuSarmalayici();

        Assert.Contains("$_SESSION['pwds'][$adaSurucu][$adaSunucu][$adaKullanici] = $adaParola;", php, StringComparison.Ordinal);
        Assert.Contains("$_SESSION['db'][$adaSurucu][$adaSunucu][$adaKullanici][$adaDb] = 1;", php, StringComparison.Ordinal);
        Assert.Contains("$_SESSION['token']", php, StringComparison.Ordinal);
    }

    /// <summary>
    /// Cerez KOK yola baglanir: Adminer kendi cagrisinda yolu gordugu adresten turetiyor ve
    /// panelin vekilinin arkasinda o yol tarayicininkiyle eslesmiyordu.
    /// </summary>
    [Fact]
    public void TheSessionCookie_IsBoundToTheRootPath()
    {
        Assert.Contains("session_set_cookie_params(0, '/'", OkuSarmalayici(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Oturum, yonlendirmeden ONCE diske yazilmali; yoksa hedef sayfa oturumu okuyamaz.
    /// </summary>
    [Fact]
    public void TheSession_IsCommittedBeforeTheRedirect()
    {
        var php = OkuSarmalayici();

        var yazma = php.IndexOf("session_write_close()", StringComparison.Ordinal);
        var yonlendirme = php.IndexOf("header('Location: '", StringComparison.Ordinal);

        Assert.True(yazma > 0, "Oturum hic kapatilmiyor.");
        Assert.True(yonlendirme > yazma, "Yonlendirme, oturum diske yazilmadan yapiliyor.");
    }

    /// <summary>Belirtec TEK KULLANIMLIK kalmali: okunur okunmaz silinir.</summary>
    [Fact]
    public void TheToken_StaysSingleUse()
    {
        var php = OkuSarmalayici();

        Assert.Contains("@unlink($adaFile);", php, StringComparison.Ordinal);

        var okuma = php.IndexOf("file_get_contents($adaFile)", StringComparison.Ordinal);
        var silme = php.IndexOf("@unlink($adaFile)", StringComparison.Ordinal);
        Assert.True(okuma > 0 && silme > okuma, "Belirtec okunmadan siliniyor.");
    }

    /// <summary>Yol asimi korumasi: belirtec dosya adina cevrilmeden ONCE temizlenmeli.</summary>
    [Fact]
    public void TheToken_IsSanitisedBeforeBuildingAPath()
    {
        var php = OkuSarmalayici();

        var temizleme = php.IndexOf("preg_replace('/[^a-f0-9]/'", StringComparison.Ordinal);
        var yolKurulumu = php.IndexOf("'adapanel-tokens'", StringComparison.Ordinal);

        Assert.True(temizleme >= 0, "Belirtec hic temizlenmiyor.");
        Assert.True(yolKurulumu > temizleme, "Belirtec, yola cevrildikten SONRA temizleniyor.");
    }

    /// <summary>
    /// OLUMCUL HATA BOS SAYFA DONMEMELI. Bu sayfada display_errors kapali oldugu icin olumcul bir
    /// PHP hatasi tarayiciya bos yanit olarak gidiyor ve kullanici yalnizca "bu sayfa calismiyor"
    /// goruyordu -- ne kullanici ne de gunluk neyin patladigini soyleyebiliyordu.
    /// </summary>
    [Fact]
    public void AFatalError_ExplainsItselfInsteadOfReturningABlankPage()
    {
        var php = OkuSarmalayici();

        Assert.Contains("register_shutdown_function", php, StringComparison.Ordinal);
        Assert.Contains("error_get_last()", php, StringComparison.Ordinal);
        Assert.Contains("E_PARSE", php, StringComparison.Ordinal);
    }

    /// <summary>Oturum baslatilamazsa sebep SOYLENIR (sessizce devam edilmez).</summary>
    [Fact]
    public void AFailedSessionStart_IsReported()
    {
        Assert.Contains("Oturum baslatilamadi", OkuSarmalayici(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Sarmalayici diske yazilamazsa panel SESSIZ KALMAZ: eski (bozuk olabilecek) dosya diskte
    /// kalir ve panel guncelmis gibi davranmaya devam ederdi.
    /// </summary>
    [Fact]
    public void AFailedWrapperWrite_IsNotSwallowed()
    {
        var kaynak = OkuKaynak();

        Assert.Contains("webdb.signon_write_failed", kaynak, StringComparison.Ordinal);
    }

    /// <summary>
    /// C# kaynagindaki ham dizgeden sarmalayici PHP'sini cikarir: test, uretilen GERCEK metni
    /// denetler, kaynaktaki yorumlari degil.
    /// </summary>
    private static string OkuSarmalayici()
    {
        var eslesme = Regex.Match(OkuKaynak(), "const string signon = \"\"\"\r?\n(.*?)\r?\n\"\"\";", RegexOptions.Singleline);
        Assert.True(eslesme.Success, "Adminer sarmalayicisi kaynakta bulunamadi.");
        return eslesme.Groups[1].Value;
    }

    private static string OkuKaynak()
    {
        var klasor = AppContext.BaseDirectory;
        for (var derinlik = 0; derinlik < 8; derinlik++)
        {
            var aday = Path.Combine(klasor, "src", "AdaPanel.Infrastructure", "WebDbClient", "WebDbClientService.cs");
            if (File.Exists(aday))
            {
                return File.ReadAllText(aday);
            }

            klasor = Path.Combine(klasor, "..");
        }

        throw new FileNotFoundException("WebDbClientService.cs");
    }
}
