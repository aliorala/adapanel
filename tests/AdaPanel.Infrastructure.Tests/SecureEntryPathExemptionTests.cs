using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Gizli giris yolu basliginin TARAYICININ GEZDIGI yuzeylerde istenmemesi.
/// <para>
/// CANLI OLAY: "phpMyAdmin'de ac" dugmesi ham bir 404 JSON'i gosterir oldu
/// (security.invalid_entry_path). Sebep bendim: basligi tum /api altina genisletmistim.
/// </para>
/// <para>
/// /api/v1/dbtools, phpMyAdmin'in TAMAMINI panelin HTTPS'i arkasindan sunan bir ters vekildir --
/// HTML sayfalari, CSS/JS varliklari, AJAX cagrilari ve form gonderimleri hepsi oradan gecer.
/// Bunlar tarayicinin KENDI istekleridir; bir sayfaya gidilirken ya da form gonderilirken ozel
/// baslik eklenemez. Yani kural teknik olarak imkansiz bir sey istiyordu.
/// </para>
/// <para>
/// Koruma kaybolmuyor: denetleyici cerez tabanli [Authorize(Roles = "Administrator")] uygular,
/// giris baglantisi tek kullanimlik yuksek entropili bir belirtec tasir ve IP kisitlamasi bu
/// karardan ONCE uygulanir. Baslik, programatik API erisimine karsi bir tarama onlemidir.
/// </para>
/// <para>
/// Test kaynak metnini okur cunku test projesi Api projesine referans vermiyor; ayni desen
/// depoda zaten var (PostgreSqlDumpArgumentTests).
/// </para>
/// </summary>
public sealed class SecureEntryPathExemptionTests
{
    private static string ReadMiddlewareSource()
    {
        var klasor = AppContext.BaseDirectory;
        for (var derinlik = 0; derinlik < 8; derinlik++)
        {
            var aday = Path.Combine(klasor, "src", "AdaPanel.Api", "Middleware", "SecureEntryPathMiddleware.cs");
            if (File.Exists(aday))
            {
                return File.ReadAllText(aday);
            }

            klasor = Path.Combine(klasor, "..");
        }

        throw new FileNotFoundException("SecureEntryPathMiddleware.cs bulunamadi.");
    }

    /// <summary>
    /// Tarayicinin gezdigi vekil yuzeyi baslik ISTEMEMELI. Bu satir kaldirilirsa phpMyAdmin
    /// yeniden erisilemez hale gelir.
    /// </summary>
    [Fact]
    public void BrowserNavigatedProxySurface_IsExemptFromTheEntryPathHeader()
    {
        var kaynak = ReadMiddlewareSource();

        Assert.Contains("/api/v1/dbtools", kaynak, StringComparison.Ordinal);
        Assert.Matches(
            @"StartsWithSegments\(""/api/v1/dbtools"".*\r?\n\s*\{\r?\n\s*return false;",
            kaynak);
    }

    /// <summary>
    /// Muafiyet DAR olmali: kural hala /api altindaki geri kalan her seyi kapsamali. Genis bir
    /// muafiyet, basligi anlamsizlastirip denetimde bulunan korumasiz uc sorununu geri getirirdi.
    /// </summary>
    [Fact]
    public void TheRuleStillCoversEveryOtherApiPath()
    {
        var kaynak = ReadMiddlewareSource();

        Assert.Contains("return path.StartsWithSegments(\"/api\", out _);", kaynak, StringComparison.Ordinal);
    }

    /// <summary>
    /// Muafiyetin GEREKCESI kaynakta yazili kalmali. Bu satirin neden burada oldugunu bilmeyen
    /// biri, guvenligi artiriyorum diye kaldirip phpMyAdmin'i yeniden bozar.
    /// </summary>
    [Fact]
    public void TheReasonForTheExemptionIsWrittenDownNextToIt()
    {
        var kaynak = ReadMiddlewareSource();

        // ASCII alt dizeler secildi: kaynak dosyada Turkce karakterler var ve kodlama farkina
        // takilan bir test, korudugu seyi degil kendi kirilganligini olcerdi.
        Assert.Contains("phpMyAdmin", kaynak, StringComparison.Ordinal);
        Assert.Contains("/mcp", kaynak, StringComparison.Ordinal);
    }
}
