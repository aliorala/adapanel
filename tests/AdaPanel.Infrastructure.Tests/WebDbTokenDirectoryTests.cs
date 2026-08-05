using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Veritabani araci (phpMyAdmin/Adminer) belirtec dizininin KIRACI IZOLASYONU sozlesmesi.
/// <para>
/// BULUNAN ACIK: dizine <c>icacls ... /grant "IIS_IUSRS:(OI)(CI)M"</c> uygulaniyordu. IIS_IUSRS
/// sunucudaki BUTUN uygulama havuzlarini kapsayan gruptur; yani her musterinin PHP kodu bu dizini
/// okuyabiliyordu. Dizinde ise veritabani motorunun <b>root parolasi DUZ METIN</b> duruyor.
/// Bir kiracinin sitesine konan uc satirlik bir PHP dosyasi, yonetici araci her actiginda parolayi
/// toplayip sunucudaki TUM musterilerin veritabanlarina erisebilirdi.
/// </para>
/// </summary>
public sealed class WebDbTokenDirectoryTests
{
    /// <summary>Butun havuzlari kapsayan gruba ARTIK izin verilmemeli.</summary>
    [Fact]
    public void TheTokenDirectory_IsNoLongerOpenedToEveryApplicationPool()
    {
        var kaynak = OkuKaynak();

        Assert.DoesNotContain("IIS_IUSRS:(OI)(CI)M", kaynak, StringComparison.Ordinal);
        Assert.DoesNotContain("GrantAppPoolWrite", kaynak, StringComparison.Ordinal);
    }

    /// <summary>
    /// Miras KIRILMALI: ust klasorden gelen genis izinler duruyorsa "yalniz bu havuz" sozu tutulamaz.
    /// </summary>
    [Fact]
    public void TheTokenDirectory_BreaksInheritance()
    {
        Assert.Contains("/inheritance:r", OkuKaynak(), StringComparison.Ordinal);
    }

    /// <summary>
    /// HAVUZ ADI SITE ADI DEGILDIR: site "AdaPanel.WebDb.Adminer" ise havuz
    /// "AdaPanel.AdaPanel.WebDb.Adminer"dir (CreateSiteAsync one ek koyar). Bu makinede olculdu:
    /// yanlis hesap adi verilince icacls 1332 ile HICBIR SEYI uygulamiyor -- yani araci kirardi.
    /// </summary>
    [Fact]
    public void TheApplicationPoolName_IsDerivedFromTheSiteNameWithThePanelPrefix()
    {
        Assert.Contains("$\"AdaPanel.{(string.IsNullOrWhiteSpace(toolSiteName)", OkuKaynak(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Hesap adlari DILE BAGLIDIR: Turkce Windows'ta "SYSTEM"/"Administrators" adlari farklidir,
    /// bu yuzden sabit SID kullanilir.
    /// </summary>
    [Fact]
    public void WellKnownAccounts_AreReferencedBySidNotByLocalisedName()
    {
        var kaynak = OkuKaynak();

        Assert.Contains("*S-1-5-18", kaynak, StringComparison.Ordinal);
        Assert.Contains("*S-1-5-32-544", kaynak, StringComparison.Ordinal);
    }

    /// <summary>
    /// SONUC OLCULUR, CIKIS KODUNA GUVENILMEZ: bu makinede olculdu, icacls bir alt ogeye
    /// erisemedigi halde ("Failed processing 1 files") 0 donuyor.
    /// </summary>
    [Fact]
    public void TheLock_IsVerifiedFromTheDirectoryItselfNotFromTheExitCode()
    {
        var kaynak = OkuKaynak();

        Assert.Contains("VerifyTokenDirectoryLocked", kaynak, StringComparison.Ordinal);
        Assert.Contains("AreAccessRulesProtected", kaynak, StringComparison.Ordinal);
        Assert.Contains("S-1-5-32-568", kaynak, StringComparison.Ordinal);
    }

    /// <summary>
    /// Kilit dogrulanamazsa parola YAZILMAZ. Korumasiz bir dizine parola yazmaktansa araci hic
    /// acmamak dogrudur.
    /// </summary>
    [Fact]
    public void WhenTheDirectoryCannotBeLocked_ThePasswordIsNeverWritten()
    {
        var kaynak = OkuKaynak();

        Assert.Contains("webdb.token_dir_unprotected", kaynak, StringComparison.Ordinal);

        // Kilit kontrolu, parolayi yazan cagridan ONCE gelmeli.
        var kilit = kaynak.IndexOf("webdb.token_dir_unprotected", StringComparison.Ordinal);
        var yazma = kaynak.IndexOf("await File.WriteAllTextAsync(\r\n            Path.Combine(tokensDir, token", StringComparison.Ordinal);
        if (yazma < 0)
        {
            yazma = kaynak.IndexOf("Path.Combine(tokensDir, token", StringComparison.Ordinal);
        }

        Assert.True(kilit > 0 && yazma > kilit, "Parola, dizin kilitlenmeden yaziliyor.");
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
