using AdaPanel.Infrastructure.Scheduler;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Gorev ciktisinin yakalanmasi.
/// <para>
/// Windows Gorev Zamanlayici komut CIKTISINI saklamaz; yalnizca cikis kodunu tutar. Kullanici
/// "gorev calismiyor" dediginde elimizde tek bir sayi oluyordu. Cikti yakalamanin tek yolu komutu
/// bir kabuga sarip yonlendirmektir -- ama bu sarma iki seyi bozmaya cok musait:
/// </para>
/// <para>
/// 1) cmd.exe'nin tirnak ayristirmasi. Yanlis bicim, bosluklu bir yolu "program adi" saydirir ve
///    gorev hic calismaz. Kullanilan bicim canli olarak dogrulandi: bosluklu exe yolu + bosluklu
///    gunluk yolu ile calisti, cikis kodu 7 korundu, hem stdout hem stderr dosyaya dustu.
/// </para>
/// <para>
/// 2) Panelin gorev tipini KAYITLI EYLEMDEN turetmesi. Sarma sonrasi her gorev "cmd.exe" gorunur;
///    geri cozulmezse tipler bozulur, URL sutunu bosalir ve duzenleme ekrani yanlis dolar. Yani
///    cikti kazanip listeyi kaybederdik.
/// </para>
/// </summary>
public sealed class SchedulerOutputCaptureTests
{
    private const string Gunluk = @"C:\ProgramData\AdaPanel\logs\tasks\bot-1A2B3C4D.log";

    [Fact]
    public void LoggedArguments_QuoteBothTheProgramAndTheLogPath()
    {
        var arguments = SchedulerService.BuildLoggedArguments(@"C:\bosluklu klasor\bot.bat", string.Empty, Gunluk);

        // Dis tirnak cifti + ic tirnakli yollar: cmd /c'nin belgelenmis calisan bicimi.
        Assert.StartsWith("/c \"\"", arguments, StringComparison.Ordinal);
        Assert.EndsWith("2>&1\"", arguments, StringComparison.Ordinal);
        Assert.Contains($"\"{Gunluk}\"", arguments, StringComparison.Ordinal);
    }

    // stderr de yakalanmali: bir betigin duserken yazdigi sey neredeyse HER ZAMAN stderr'dedir.
    [Fact]
    public void LoggedArguments_CaptureStandardErrorTogetherWithStandardOutput()
    {
        Assert.Contains("2>&1", SchedulerService.BuildLoggedArguments("bot.exe", string.Empty, Gunluk), StringComparison.Ordinal);
    }

    // ">" kullanilir, ">>" DEGIL: dosyada yalnizca SON calismanin ciktisi durmali. Dakikada bir
    // calisan bir gorevde ekleme yapmak, kimsenin okumadigi ve sinirsiz buyuyen bir dosya birakirdi.
    [Fact]
    public void LoggedArguments_KeepOnlyTheLastRunInsteadOfGrowingForever()
    {
        var arguments = SchedulerService.BuildLoggedArguments("bot.exe", string.Empty, Gunluk);

        Assert.DoesNotContain(">>", arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void LoggedArguments_KeepTheOriginalArguments()
    {
        var arguments = SchedulerService.BuildLoggedArguments("curl.exe", "-fsSL --max-time 300 \"https://ornek.test/\"", Gunluk);

        Assert.Contains("-fsSL --max-time 300 \"https://ornek.test/\"", arguments, StringComparison.Ordinal);
    }

    /// <summary>
    /// Sarilan eylem GERI COZULEBILMELI, yoksa gorev listesi bozulur.
    /// </summary>
    [Theory]
    [InlineData(@"C:\bosluklu klasor\bot.bat", "")]
    [InlineData("curl.exe", "-fsSL --max-time 300 --retry 2 \"https://ornek.test/cron\"")]
    [InlineData(@"C:\php\php.exe", "\"C:\\site\\cron.php\" --gunluk")]
    public void UnwrapLoggedAction_RecoversExactlyWhatWasWrapped(string execute, string arguments)
    {
        var sarili = SchedulerService.BuildLoggedArguments(execute, arguments, Gunluk);

        Assert.True(SchedulerService.TryUnwrapLoggedAction("cmd.exe", sarili, out var cozulenExe, out var cozulenArgs));
        Assert.Equal(execute, cozulenExe);
        Assert.Equal(arguments, cozulenArgs);
    }

    // Sarilmamis gorevler (bu surumden onceki kayitlar) oldugu gibi birakilmali.
    [Fact]
    public void UnwrapLoggedAction_LeavesAnUnwrappedActionAlone()
    {
        Assert.False(SchedulerService.TryUnwrapLoggedAction("curl.exe", "-fsSL \"https://ornek.test/\"", out var exe, out var args));
        Assert.Equal("curl.exe", exe);
        Assert.Equal("-fsSL \"https://ornek.test/\"", args);
    }

    // cmd.exe olan ama BIZIM uretmedigimiz bir eylem de bozulmamali: kullanici kendi
    // "cmd.exe /c ..." komutunu girmis olabilir.
    [Fact]
    public void UnwrapLoggedAction_DoesNotMisreadAUserWrittenCmdCommand()
    {
        Assert.False(SchedulerService.TryUnwrapLoggedAction("cmd.exe", "/c dir C:\\", out _, out _));
    }

    /// <summary>
    /// Gorev adi kullanicidan gelir: yola dogrudan eklemek panelin veri kokunun DISINA yazmasina
    /// yol acardi.
    /// </summary>
    [Theory]
    [InlineData(@"..\..\Windows\System32\onemli")]
    [InlineData("gunluk/yedek")]
    [InlineData("C:ozel")]
    public void LogFileName_NeverEscapesItsFolder(string taskName)
    {
        var dosya = SchedulerService.ResolveLogFileName(taskName);

        // Olculen sey ".." gecmemesi DEGIL -- ayirici olmadan ".." zaten bir ust klasor anlamina
        // gelmez. Asil guvence: uretilen ad SALT bir dosya adidir ve klasorle birlestirildiginde
        // sonuc o klasorun ALTINDA kalir.
        Assert.Equal(dosya, Path.GetFileName(dosya));

        const string klasor = @"C:\ProgramData\AdaPanel\logs\tasks";
        Assert.StartsWith(klasor + Path.DirectorySeparatorChar, Path.GetFullPath(Path.Combine(klasor, dosya)), StringComparison.Ordinal);
    }

    // Temizleme farkli adlari ayni dosyaya dusurmemeli: "yedek/gece" ile "yedek-gece" ayri
    // gorevlerdir ve ciktilarinin karismasi teshisi bastan yanlislar.
    [Fact]
    public void LogFileName_KeepsDifferentTaskNamesApartAfterSanitising()
    {
        Assert.NotEqual(
            SchedulerService.ResolveLogFileName("yedek/gece"),
            SchedulerService.ResolveLogFileName("yedek-gece"));
    }

    [Fact]
    public void LogFileName_IsStableForTheSameTask()
    {
        Assert.Equal(
            SchedulerService.ResolveLogFileName("gece yedegi"),
            SchedulerService.ResolveLogFileName("gece yedegi"));
    }
}
