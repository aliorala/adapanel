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
    /// MIRAS KIRILMAZ — bu, PANELIN KENDI DOSYASINI erisilemez yapiyordu.
    /// <para>
    /// Ilk surumde "/inheritance:r" kullanildi. Bu dizinde panelin HTTP korumasi icin yazdigi bir
    /// web.config duruyor; miras kesilince panel ona yazamaz oldu ve kullanici
    /// "Access to the path ...\adapanel-tokens\web.config is denied" hatasi aldi -- yani guvenlik
    /// duzeltmesi araci tamamen kirdi. Bu makinede birebir uretildi ve dogrulandi.
    /// </para>
    /// <para>
    /// Gereken sey mirasi kesmek degil, GENIS GRUBU KALDIRMAKTI. Ayrica "/inheritance:e" onceki
    /// surumun kirdigi mirasi geri acar, yani duzeltme kendinden onceki hasari da onarir.
    /// </para>
    /// </summary>
    [Fact]
    public void TheTokenDirectory_NeverBreaksInheritance()
    {
        var kaynak = OkuKaynak();

        // Aciklamalarda gecebilir; aranan sey KOMUTUN KENDISI.
        Assert.DoesNotContain("{directory}\\\" /inheritance:r", kaynak, StringComparison.Ordinal);
        Assert.Contains("{directory}\\\" /inheritance:e", kaynak, StringComparison.Ordinal);
        Assert.Contains("/remove:g", kaynak, StringComparison.Ordinal);
    }

    /// <summary>
    /// Dizin kapatilamadiginda arac KOSULSUZ KIRILMAZ; ayrim riskin buyuklugundedir.
    /// Root parolasi (sunucudaki tum veritabanlarini acar) korumasiz dizine yazilmaz, ama tek bir
    /// veritabaninin kendi kullanicisi yazilabilir -- o parola zaten yalniz o veritabanina erisir.
    /// </summary>
    [Fact]
    public void WhenTheDirectoryCannotBeLocked_OnlyTheRootPasswordIsWithheld()
    {
        var kaynak = OkuKaynak();

        Assert.Contains("if (string.Equals(user, \"root\", StringComparison.OrdinalIgnoreCase))", kaynak, StringComparison.Ordinal);
    }

    /// <summary>
    /// HAVUZ ADI TAHMIN EDILMEZ, IIS'TEN OKUNUR.
    /// <para>
    /// Ilk yazimda ad "AdaPanel.{siteAdi}" formuluyle turetiliyordu. Bu bir UYGULAMA AYRINTISIDIR:
    /// eski surumlerle dagitilmis kurulumlarda ad farkli olabilir, yonetici IIS'ten degistirmis
    /// olabilir. Yanlis hesap adi verilince icacls HICBIR SEY uygulamadan cikiyor (bu makinede
    /// olculdu: hata 1332) -- yani tahmin, dizini korumasiz birakmakla kalmaz, araci da tamamen
    /// acilamaz hale getirir. Kullanicinin canli sunucusunda tam olarak bu oldu.
    /// </para>
    /// </summary>
    [Fact]
    public void TheApplicationPoolName_IsReadFromIisNotGuessed()
    {
        var kaynak = OkuKaynak();

        Assert.Contains("ResolveToolAppPoolNameAsync", kaynak, StringComparison.Ordinal);
        Assert.Contains("site?.ApplicationPoolName", kaynak, StringComparison.Ordinal);

        // Formulle turetme geri gelmemeli.
        Assert.DoesNotContain("$\"AdaPanel.{(string.IsNullOrWhiteSpace(toolSiteName)", kaynak, StringComparison.Ordinal);
    }

    /// <summary>
    /// 0.2.91'in kirdigi miras, o klasorlere YAZAN HER YOLDAN ONCE onarilmali.
    /// <para>
    /// Onarimi yalnizca kilitleme adimina koymak YETMEDI: kilitleme, yazma denemesinden SONRA
    /// calisiyor; yani panel kendi biraktigi hasari hicbir zaman onaramiyor ve kullanici
    /// "Beklenmeyen bir hata olustu" almaya devam ediyordu. Hasarli uc klasor vardi: iki aracin
    /// belirtec klasoru ve phpMyAdmin'in sablon onbellegi (oraya yazamazsa phpMyAdmin hic acilmaz).
    /// </para>
    /// </summary>
    [Fact]
    public void TheInheritanceRepair_RunsBeforeEveryWriteToThoseDirectories()
    {
        var kaynak = OkuKaynak();

        Assert.Contains("private static void RestoreDirectoryInheritance", kaynak, StringComparison.Ordinal);

        // Klasoru olusturan her yer, hemen ardindan onarimi cagirmali.
        var olusturma = System.Text.RegularExpressions.Regex.Matches(kaynak, @"Directory\.CreateDirectory\((tokensDir|tempDir)\);");
        Assert.True(olusturma.Count >= 4, $"Beklenen klasor olusturma sayisi degisti: {olusturma.Count}");

        var onarim = System.Text.RegularExpressions.Regex.Matches(kaynak, @"RestoreDirectoryInheritance\((tokensDir|tempDir)\);");
        Assert.True(
            onarim.Count >= olusturma.Count,
            $"Klasor {olusturma.Count} yerde olusturuluyor ama onarim yalnizca {onarim.Count} yerde cagriliyor.");
    }

    /// <summary>
    /// Havuz adi okunamadiysa TAHMINE DUSULMEZ: bilinmeyen bir hesap adiyla icacls calistirmak
    /// dizini korumasiz birakir ve araci kirar.
    /// </summary>
    [Fact]
    public void WhenTheApplicationPoolNameIsUnknown_NoGuessIsMade()
    {
        var kaynak = OkuKaynak();

        Assert.Contains("if (string.IsNullOrWhiteSpace(appPoolName))", kaynak, StringComparison.Ordinal);
    }

    /// <summary>
    /// phpMyAdmin'in SIR ICERMEYEN sablon onbellegi klasoru, kilitlenemedi diye araci
    /// KIRMAMALI: orada parola yok ve phpMyAdmin o klasore yazamazsa hic acilmaz.
    /// </summary>
    [Fact]
    public void TheTemplateCacheDirectory_NeverBlocksTheTool()
    {
        var kaynak = OkuKaynak();
        var tempBlok = kaynak.IndexOf("var pmaHavuzu = await ResolveToolAppPoolNameAsync", StringComparison.Ordinal);

        Assert.True(tempBlok > 0, "Sablon onbellegi kilidi bulunamadi.");

        // Bu bloktan sonraki 400 karakterde "throw" OLMAMALI.
        var pencere = kaynak.Substring(tempBlok, Math.Min(400, kaynak.Length - tempBlok));
        Assert.DoesNotContain("throw new AppException", pencere, StringComparison.Ordinal);
    }

    /// <summary>
    /// Grup DILE BAGLI ADIYLA DEGIL, sabit SID ile kaldirilir: Turkce Windows'ta yerlesik grup
    /// adlari farklidir ve ada gore yapilan bir kaldirma sessizce hicbir sey yapmazdi.
    /// </summary>
    [Fact]
    public void TheWideGroup_IsReferencedBySidNotByLocalisedName()
    {
        var kaynak = OkuKaynak();

        Assert.Contains($"/remove:g \\\"*{{IisIusrsSid}}\\\"", kaynak, StringComparison.Ordinal);
        Assert.DoesNotContain("/remove:g \"IIS_IUSRS\"", kaynak, StringComparison.Ordinal);
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
        Assert.Contains("S-1-5-32-568", kaynak, StringComparison.Ordinal);
        // Kurallar MIRAS DAHIL okunur: grup ust klasorden geliyorsa da yakalanmali.
        Assert.Contains("GetAccessRules(true, true,", kaynak, StringComparison.Ordinal);
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
