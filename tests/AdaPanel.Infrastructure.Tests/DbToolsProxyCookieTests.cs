using AdaPanel.Api.Controllers;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Vekil arkasindaki DB araclarinin OTURUM CEREZI dogru yola baglanmali.
/// <para>
/// BULUNAN HATA: Adminer, cerez yolunu KENDI gordugu adresten uretiyor:
/// <c>session_set_cookie_params(0, preg_replace('~\?.*~', '', $_SERVER["REQUEST_URI"]), ...)</c>.
/// Vekilin arkasinda PHP "/adapanel-signon.php" gorur, ama tarayici adrese
/// "/api/v1/dbtools/adminer/adapanel-signon.php" olarak gider. Iki yol eslesmedigi icin tarayici
/// cerezi BIR DAHA GONDERMEZ: Adminer'in oturumu her istekte kaybolur ve giris yaptiktan sonraki
/// ilk sayfa "bu sayfa calismiyor" verir.
/// </para>
/// <para>
/// Bu, yerelde dogrudan PHP sunucusuna yapilan testlerde GORUNMEZ -- orada yol zaten eslesiyor.
/// Yalnizca gercek vekil yolunda ortaya cikar.
/// </para>
/// </summary>
public sealed class DbToolsProxyCookieTests
{
    private const string Onek = "/api/v1/dbtools/adminer/";

    /// <summary>Adminer'in gercekte gonderdigi bicim (bu makinede olculdu).</summary>
    [Fact]
    public void TheAdminerSessionCookie_IsReboundToTheProxyPath()
    {
        var girdi = "adminer_sid=sm0jnsmo68cci9q1lgfu51uvl6; path=/adapanel-signon.php; HttpOnly";

        var sonuc = DbToolsProxyController.RewriteCookiePath(girdi, Onek);

        Assert.Contains($"Path={Onek}", sonuc, StringComparison.Ordinal);
        Assert.DoesNotContain("path=/adapanel-signon.php", sonuc, StringComparison.Ordinal);
        // Cerezin KENDISI ve diger nitelikleri korunmali.
        Assert.StartsWith("adminer_sid=sm0jnsmo68cci9q1lgfu51uvl6;", sonuc, StringComparison.Ordinal);
        Assert.Contains("HttpOnly", sonuc, StringComparison.Ordinal);
    }

    /// <summary>Path hic yoksa EKLENIR: yoksa tarayici yolu istekten turetir ve alt yollarda kaybolabilir.</summary>
    [Fact]
    public void ACookieWithoutAPath_GetsOne()
    {
        var sonuc = DbToolsProxyController.RewriteCookiePath("phpMyAdmin=abc123; HttpOnly", Onek);

        Assert.Contains($"Path={Onek}", sonuc, StringComparison.Ordinal);
        Assert.StartsWith("phpMyAdmin=abc123;", sonuc, StringComparison.Ordinal);
    }

    /// <summary>Buyuk/kucuk harf ve bosluk degisimleri: "Path = /x" da yakalanmali.</summary>
    [Theory]
    [InlineData("a=b; Path=/adapanel-signon.php")]
    [InlineData("a=b; PATH = /adapanel-signon.php ; Secure")]
    [InlineData("a=b; path=/; SameSite=Lax")]
    public void ThePathAttribute_IsMatchedRegardlessOfCasingOrSpacing(string girdi)
    {
        var sonuc = DbToolsProxyController.RewriteCookiePath(girdi, Onek);

        Assert.Contains($"Path={Onek}", sonuc, StringComparison.Ordinal);
        Assert.DoesNotContain("/adapanel-signon.php", sonuc, StringComparison.Ordinal);
    }

    /// <summary>Diger nitelikler (Secure, SameSite, Expires) KAYBOLMAMALI.</summary>
    [Fact]
    public void OtherCookieAttributes_ArePreserved()
    {
        var girdi = "adminer_sid=xyz; path=/adapanel-signon.php; HttpOnly; Secure; SameSite=Lax";

        var sonuc = DbToolsProxyController.RewriteCookiePath(girdi, Onek);

        Assert.Contains("HttpOnly", sonuc, StringComparison.Ordinal);
        Assert.Contains("Secure", sonuc, StringComparison.Ordinal);
        Assert.Contains("SameSite=Lax", sonuc, StringComparison.Ordinal);
    }

    /// <summary>Bos deger cokmemeli.</summary>
    [Fact]
    public void AnEmptyHeader_IsLeftAlone()
    {
        Assert.Equal(string.Empty, DbToolsProxyController.RewriteCookiePath(string.Empty, Onek));
    }
}
