using System.Text.RegularExpressions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Adminer tek tik girisinin GERCEK Adminer 5.x ile calismasini kilitleyen sozlesmeler.
/// <para>
/// Canlida "sayfa goruntulenemiyor" veriyordu. Gercek adminer-5.4.2.php ile calistirilinca sebep
/// goruldu: <c>Class "Adminer" not found</c>. Adminer 5'te her sey "Adminer" ad alanina tasindi;
/// temel sinifin adi artik <c>\Adminer\Adminer</c>. Sunucuda display_errors kapali oldugu icin
/// kullanici bu satiri hic gormuyor, yalnizca bos sayfa goruyordu.
/// </para>
/// <para>
/// Ikinci hata, ilki duzeltilince ortaya cikti: Adminer giristen sonra KENDI giris noktasina
/// (yani sarmalayiciya) yonlendiriyor. Belirtec tek kullanimlik oldugu icin ikinci istekte
/// bulunamiyor ve sarmalayici 403 "Baglanti suresi dolmus" veriyordu -- yani tek tik girisi ilk
/// yonlendirmede oluyordu.
/// </para>
/// </summary>
public sealed class AdminerSignonContractTests
{
    /// <summary>
    /// Adminer 5.x'te temel sinif ad alanina tasindi; kuresel <c>\Adminer</c> sinifi YOKTUR.
    /// </summary>
    [Fact]
    public void Wrapper_ResolvesTheBaseClassInsteadOfAssumingTheOldGlobalOne()
    {
        var php = OkuSarmalayici();

        Assert.Contains("class_exists('Adminer\\\\Adminer')", php, StringComparison.Ordinal);
        Assert.Contains("extends \\AdaPanelAdminerBase", php, StringComparison.Ordinal);

        // Eski (4.x) varsayimi geri gelmemeli.
        Assert.DoesNotContain("class AdaPanelAdminer extends Adminer\n", php, StringComparison.Ordinal);
    }

    /// <summary>
    /// Surum taninmazsa BOS SAYFA donulmez: teshis edilemeyen hata, cozulemeyen hatadir.
    /// </summary>
    [Fact]
    public void Wrapper_SaysSomethingUsefulWhenTheAdminerVersionIsUnknown()
    {
        var php = OkuSarmalayici();

        Assert.Contains("Adminer surumu taninmadi", php, StringComparison.Ordinal);
    }

    /// <summary>
    /// Eklenti giris noktasi KURESEL ad alaninda kalmali: Adminer onu
    /// <c>function_exists('adminer_object')</c> ile arar ve dizge biciminde verilen ad, bulundugu
    /// ad alanina gore cozulmez.
    /// </summary>
    [Fact]
    public void Wrapper_KeepsTheEntryPointInTheGlobalNamespace()
    {
        var php = OkuSarmalayici();

        Assert.DoesNotContain("namespace Adminer", php, StringComparison.Ordinal);
        Assert.Contains("function adminer_object()", php, StringComparison.Ordinal);
    }

    /// <summary>
    /// Belirtec bulunamadiginda SAYFA OLMEZ. Bu, Adminer icinde gezinmenin NORMAL halidir:
    /// belirtec tek kullanimliktir ve ilk istekte silinir.
    /// </summary>
    [Fact]
    public void Wrapper_DoesNotDieWhenTheOneTimeTokenIsAlreadyUsed()
    {
        var php = OkuSarmalayici();

        // Metin sarmalayicinin ACIKLAMASINDA gecebilir; aranan sey, sayfayi olduren CAGRI.
        Assert.DoesNotContain("exit('Baglanti suresi dolmus", php, StringComparison.Ordinal);
        Assert.DoesNotContain("exit('Gecersiz baglanti.')", php, StringComparison.Ordinal);
    }

    /// <summary>
    /// Otomatik giris alanlari YALNIZCA belirtecli ilk istekte doldurulmali; her istekte
    /// doldurmak Adminer'i her sayfada yeniden giris yapmaya zorlar (yonlendirme dongusu).
    /// </summary>
    [Fact]
    public void Wrapper_FillsTheLoginFieldsOnlyOnTheTokenRequest()
    {
        var php = OkuSarmalayici();

        var belirtecBlogu = php.IndexOf("if ($adaToken !== ''", StringComparison.Ordinal);
        var authAlani = php.IndexOf("$_POST['auth']", StringComparison.Ordinal);

        Assert.True(belirtecBlogu >= 0, "Belirtec blogu bulunamadi.");
        Assert.True(authAlani > belirtecBlogu, "Otomatik giris alanlari belirtec blogunun disinda dolduruluyor.");
    }

    /// <summary>
    /// Panel kimligi yokken (gezinme sirasinda) gecersiz kilmalar Adminer'in kendi davranisina
    /// dusmeli; diziye korlemesine erismek her sayfada olumcul hata verirdi.
    /// </summary>
    [Fact]
    public void Wrapper_FallsBackToAdminerWhenThePanelIdentityIsGone()
    {
        var php = OkuSarmalayici();

        Assert.Contains("return parent::credentials();", php, StringComparison.Ordinal);
        Assert.Contains("return parent::login($login, $password);", php, StringComparison.Ordinal);
    }

    /// <summary>
    /// Veritabani suzgeci ilk sayfadan SONRA da uygulanmali. Yalnizca belirtece bakilirsa, gercek
    /// Adminer ile olculdugu gibi, ust taraftaki "DB" secici sunucudaki butun veritabanlarini
    /// listeliyor -- yani panel tutamadigi bir soz veriyordu.
    /// </summary>
    [Fact]
    public void Wrapper_KeepsTheDatabaseFilterWhileBrowsing()
    {
        var php = OkuSarmalayici();

        Assert.Contains("private function adaOnlyDb()", php, StringComparison.Ordinal);
        Assert.Contains("$_GET['db']", php, StringComparison.Ordinal);
    }

    /// <summary>
    /// ADMINER'IN KENDI BELIRTECI oturumda OLMALI.
    /// <para>
    /// Adminer, oturumda bir <c>token</c> degeri tutar ve sonraki her istekte onu arar:
    /// "cerez var ama oturumda token yok" durumunu OTURUM DUSMUS sayar ve
    /// "Session expired, please login again." diyerek 403 doner (kaynak: <c>auth_error</c>,
    /// kosul <c>($_COOKIE[$bi] || $_GET[$bi]) &amp;&amp; !$_SESSION["token"]</c>).
    /// </para>
    /// <para>
    /// O deger normalde giris FORMU cizilirken olusur. Panelin otomatik girisi formu hic
    /// gostermedigi icin hic olusmuyordu: giris basariyla tamamlaniyor, yonlendirilen ILK sayfa
    /// "oturum dustu" ile oluyordu. Gercek Adminer + gercek vekil yolu ile olculdu.
    /// </para>
    /// </summary>
    [Fact]
    public void TheAdminerCsrfToken_IsSeededInTheSession()
    {
        var php = OkuSarmalayici();

        Assert.Contains("$_SESSION['token']", php, StringComparison.Ordinal);
        Assert.Contains("random_int(1, 1000000)", php, StringComparison.Ordinal);
    }

    /// <summary>
    /// Oturum ADMINER'DAN ONCE baslatilir. Iki isi birden gorur: yukaridaki belirteci yazabilmek
    /// ve cerezi KOK yola baglamak.
    /// <para>
    /// Adminer cerez yolunu kendi gordugu adresten turetiyor (REQUEST_URI); panelin vekilinin
    /// arkasinda o yol tarayicinin gittigi yolla eslesmiyor ve cerez bir daha gonderilmiyordu.
    /// Oturum burada baslatilinca Adminer kendi <c>session_start</c> blogunu atlar.
    /// </para>
    /// </summary>
    [Fact]
    public void TheSessionIsStartedBeforeAdminer_WithARootCookiePath()
    {
        var php = OkuSarmalayici();

        var oturum = php.IndexOf("session_start()", StringComparison.Ordinal);
        var yukleme = php.IndexOf("require __DIR__", StringComparison.Ordinal);

        Assert.True(oturum > 0, "Oturum sarmalayicida hic baslatilmiyor.");
        Assert.True(yukleme > oturum, "Oturum, Adminer yuklendikten SONRA baslatiliyor.");
        Assert.Contains("session_set_cookie_params(0, '/'", php, StringComparison.Ordinal);
    }

    /// <summary>
    /// Yol asimi korumasi: belirtec dosya adina cevrilmeden ONCE temizlenmeli.
    /// </summary>
    [Fact]
    public void Wrapper_SanitizesTheTokenBeforeBuildingAPath()
    {
        var php = OkuSarmalayici();

        var temizleme = php.IndexOf("preg_replace('/[^a-f0-9]/'", StringComparison.Ordinal);
        var yolKurulumu = php.IndexOf("'adapanel-tokens'", StringComparison.Ordinal);

        Assert.True(temizleme >= 0, "Belirtec hic temizlenmiyor.");
        Assert.True(yolKurulumu > temizleme, "Belirtec, yola cevrildikten SONRA temizleniyor.");
    }

    /// <summary>Belirtec TEK KULLANIMLIK kalmali: okunur okunmaz silinir.</summary>
    [Fact]
    public void Wrapper_KeepsTheTokenSingleUse()
    {
        Assert.Contains("@unlink($adaFile);", OkuSarmalayici(), StringComparison.Ordinal);
    }

    /// <summary>
    /// C# kaynagindaki ham dizgeden sarmalayici PHP'sini cikarir: test, uretilen GERCEK metni
    /// denetler, kaynaktaki yorumlari degil.
    /// </summary>
    private static string OkuSarmalayici()
    {
        var klasor = AppContext.BaseDirectory;
        for (var derinlik = 0; derinlik < 8; derinlik++)
        {
            var aday = Path.Combine(klasor, "src", "AdaPanel.Infrastructure", "WebDbClient", "WebDbClientService.cs");
            if (File.Exists(aday))
            {
                var kaynak = File.ReadAllText(aday);
                var eslesme = Regex.Match(kaynak, "const string signon = \"\"\"\r?\n(.*?)\r?\n\"\"\";", RegexOptions.Singleline);
                Assert.True(eslesme.Success, "Adminer sarmalayicisi kaynakta bulunamadi.");
                return eslesme.Groups[1].Value;
            }

            klasor = Path.Combine(klasor, "..");
        }

        throw new FileNotFoundException("WebDbClientService.cs");
    }
}
