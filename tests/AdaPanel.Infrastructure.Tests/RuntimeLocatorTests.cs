using AdaPanel.Infrastructure.Scheduler;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Sunucuda kurulu Python'un bulunmasi ve makine PATH'ine eklenmesi.
/// <para>
/// CANLI OLAY: kullanicinin gorevi "Python bulunamadi" ciktisiyla dustu, oysa sunucuda Python 3.11
/// kuruluydu. Sebep, Python kurulumunun VARSAYILAN secenegi olan "yalnizca benim icin" kurulum:
/// yorumlayici kullanicinin kendi klasorune iner ve YALNIZCA onun PATH'ine eklenir. Zamanlanmis
/// gorevler SYSTEM olarak calistigi icin ne o klasoru gorur ne o ortami. Kullanici kendi
/// terminalinde denedigi icin fark neredeyse hic anlasilmaz.
/// </para>
/// </summary>
public sealed class RuntimeLocatorTests
{
    /// <summary>
    /// Surum karsilastirmasi METIN olarak yapilmamali.
    /// <para>
    /// "3.9" metinsel olarak "3.11"den BUYUKTUR; sirf bu yuzden panel eski yorumlayiciyi secip
    /// PATH'in basina koyabilir ve kullanicinin yeni kurdugu surumu golgeleyerek ONCEKINDEN
    /// KOTU bir durum yaratabilirdi.
    /// </para>
    /// </summary>
    [Fact]
    public void Discovery_PutsTheNewestVersionFirstUsingNumericOrder()
    {
        var (yorumlayicilar, _) = RuntimeLocator.ParseDiscovery(
            """
            ADAPY|3.9.13|C:\Python39\python.exe
            ADAPY|3.11.9|C:\Program Files\Python311\python.exe
            ADAPY|3.10.11|C:\Python310\python.exe
            ADAPATH|
            """);

        Assert.Equal(@"C:\Program Files\Python311\python.exe", yorumlayicilar[0]);
        Assert.Equal(3, yorumlayicilar.Count);
    }

    // Ayni yorumlayici birden cok kaynakta gorunur (hem kayit defteri hem klasor taramasi);
    // listede iki kez cikmamali.
    [Fact]
    public void Discovery_ReportsEachInterpreterOnlyOnce()
    {
        var (yorumlayicilar, _) = RuntimeLocator.ParseDiscovery(
            """
            ADAPY|3.11.9|C:\Program Files\Python311\python.exe
            ADAPY|3.11.9|C:\PROGRAM FILES\PYTHON311\PYTHON.EXE
            ADAPATH|
            """);

        Assert.Single(yorumlayicilar);
    }

    [Fact]
    public void Discovery_ReadsWhetherSystemAlreadyResolvesPython()
    {
        var (_, pathte) = RuntimeLocator.ParseDiscovery(
            "ADAPY|3.11.9|C:\\Python311\\python.exe\nADAPATH|C:\\Python311\\python.exe");

        Assert.Equal(@"C:\Python311\python.exe", pathte);
    }

    /// <summary>
    /// Olcum yapilamadiysa "Python yok" DENMEZ. Bos cikti, bos sonuc verir; panel de var olmayan
    /// bir sorunu onarmayi onermez. Bu oturumun tekrar eden hatasi tam tersiydi: olcememeyi
    /// "sorun yok" saymak.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("beklenmeyen cikti")]
    public void Discovery_ClaimsNothingWhenItCouldNotMeasure(string? cikti)
    {
        var (yorumlayicilar, pathte) = RuntimeLocator.ParseDiscovery(cikti);

        Assert.Empty(yorumlayicilar);
        Assert.Empty(pathte);
    }

    /// <summary>
    /// PATH REG_EXPAND_SZ olarak yazilmali.
    /// <para>
    /// .NET'in <c>SetEnvironmentVariable(..., 'Machine')</c> cagrisi degeri REG_SZ'ye cevirir ve
    /// PATH icindeki %SystemRoot% gibi degiskenler bir daha genisletilmez -- yani sunucunun PATH'i
    /// SESSIZCE bozulur. Bir kolaylik eklerken sunucuyu bozmak, ozelligin kendisinden cok daha
    /// pahaliya gelirdi.
    /// </para>
    /// </summary>
    [Fact]
    public void PathScript_WritesTheValueAsAnExpandableString()
    {
        var script = RuntimeLocator.BuildAddToMachinePathScript(@"C:\Python311\python.exe");

        Assert.Contains("-Type ExpandString", script, StringComparison.Ordinal);
        Assert.DoesNotContain("SetEnvironmentVariable", script, StringComparison.Ordinal);
    }

    // Yorumlayicinin KLASORU eklenir, exe'nin kendisi degil; ayrica pip araclarinin indigi
    // Scripts klasoru de eklenir.
    [Fact]
    public void PathScript_AddsTheFolderAndTheScriptsFolder()
    {
        var script = RuntimeLocator.BuildAddToMachinePathScript(@"C:\Python311\python.exe");

        Assert.Contains(@"'C:\Python311'", script, StringComparison.Ordinal);
        Assert.Contains(@"'C:\Python311\Scripts'", script, StringComparison.Ordinal);
        Assert.DoesNotContain(@"'C:\Python311\python.exe'", script, StringComparison.Ordinal);
    }

    // Zaten ekliyse tekrar eklenmemeli: her cagrida PATH'i buyutmek, sinira dayanip sunucuyu
    // bozabilecek bir birikim olurdu.
    [Fact]
    public void PathScript_IsIdempotent()
    {
        var script = RuntimeLocator.BuildAddToMachinePathScript(@"C:\Python311\python.exe");

        Assert.Contains("ADAPANEL-DEGISIKLIK=0", script, StringComparison.Ordinal);
        Assert.Contains("-ieq", script, StringComparison.Ordinal);
    }

    // Kesif dort kaynaga birden bakmali: yalnizca makine kaydina bakmak, tam da bu kullanicinin
    // "yalnizca benim icin" kurulumunu kacirirdi.
    [Theory]
    [InlineData("HKLM:\\SOFTWARE\\Python\\PythonCore")]
    [InlineData("HKU:")]
    [InlineData("AppData\\Local\\Programs\\Python")]
    [InlineData("Program Files\\Python*")]
    public void DiscoveryScript_LooksAtEveryPlaceAPythonInstallCanLand(string beklenen)
    {
        Assert.Contains(beklenen, RuntimeLocator.DiscoveryScript, StringComparison.Ordinal);
    }
}

/// <summary>
/// PHP yorumlayicisinin sunucunun TAMAMINDA aranmasi.
/// <para>
/// CANLI OLAY: kullanici kendi sitesindeki bir .php dosyasini zamanlamak istedi ve panel izin
/// vermedi. Sebep: PHP yalnizca panelin kendi klasorunde (%ProgramData%\AdaPanel\runtimes\php)
/// araniyordu. Sunucuda PHP zaten kuruluysa -- IIS ile gelen, elle kurulan ya da baska bir
/// panelden kalan -- gorev "PHP kurulu degil" diye reddediliyordu; kullanici ise sitesinin PHP ile
/// calistigini gozuyle goruyordu.
/// </para>
/// <para>
/// Python'daki hatanin AYNISI: panel kendi bildigi tek yeri gercegin tamami saniyordu.
/// </para>
/// </summary>
public sealed class PhpLocatorTests
{
    /// <summary>
    /// IIS yapilandirmasi bir Windows web sunucusunda EN GUVENILIR kaynaktir: PHP hemen her zaman
    /// FastCGI olarak kayitlidir ve orada tam yolu yazar.
    /// </summary>
    [Fact]
    public void IisConfig_YieldsThePhpFolderFromTheFastCgiRegistration()
    {
        var gecici = Path.Combine(Path.GetTempPath(), $"ada-iis-{Guid.NewGuid():N}.config");
        try
        {
            File.WriteAllText(gecici, """
                <configuration>
                  <system.webServer>
                    <fastCgi>
                      <application fullPath="C:\Program Files\PHP\v8.3\php-cgi.exe" maxInstances="4" />
                      <application fullPath="C:\Program Files\PHP\v7.4\php-cgi.exe" maxInstances="4" />
                    </fastCgi>
                  </system.webServer>
                </configuration>
                """);

            var klasorler = RuntimeLocator.ReadPhpFoldersFromIisConfig(gecici);

            // php-cgi.exe kayitlidir ama komut satiri surumu php.exe AYNI klasordedir.
            Assert.Contains(@"C:\Program Files\PHP\v8.3", klasorler);
            Assert.Contains(@"C:\Program Files\PHP\v7.4", klasorler);
        }
        finally
        {
            File.Delete(gecici);
        }
    }

    // Dosya yoksa ya da okunamiyorsa cokmez: PHP'yi baska kaynaklardan aramaya devam edilmeli.
    [Fact]
    public void IisConfig_ReturnsNothingInsteadOfThrowingWhenItCannotBeRead()
    {
        Assert.Empty(RuntimeLocator.ReadPhpFoldersFromIisConfig(@"C:\boyle-bir-dosya-yok-12345.config"));
    }

    // Surum secimi SAYISAL olmali: metinsel siralamada "8.9" > "8.10" cikar ve panel eski
    // yorumlayiciyi secerdi.
    [Fact]
    public void PhpVersions_AreComparedNumericallyNotAsText()
    {
        Assert.True(
            RuntimeLocator.ParseVersionFromPath(@"C:\PHP\8.10.2\php.exe") >
            RuntimeLocator.ParseVersionFromPath(@"C:\PHP\8.9.1\php.exe"));
    }
}
