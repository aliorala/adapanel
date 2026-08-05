using AdaPanel.Core.Dtos.Scheduler;
using AdaPanel.Infrastructure.Scheduler;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// "Simdi calistir" ve "Suren calismayi durdur" eylemlerinin DURUST olmasi.
/// <para>
/// BULUNAN HATA-1: devre disi bir gorevde "Simdi calistir" ham PowerShell yigini gosteriyordu
/// ("Start-ScheduledTask : Gorev devre disi ... HRESULT 0x80041326"). Kullanicinin yapmasi
/// gereken sey (once Etkinlestir) o metinde hic gecmiyordu.
/// </para>
/// <para>
/// BULUNAN HATA-2: "durdur" yalnizca Stop-ScheduledTask cagiriyordu. Kullanicinin canli
/// sunucusunda Chrome/Selenium acan bir .bat gorevi vardi: Zamanlayici yalnizca kendi baslattigi
/// sureci sonlandiriyor, o surecin dogurdugu alti kademelik zincir yasamaya devam ediyordu. Panel
/// "durduruldu" diyor, is suruyordu. Ayrica gorev yeniden kaydedildiginde Zamanlayici calisan
/// turun izini kaybediyor ve Stop-ScheduledTask o OKSUZ sureci hic tanimiyor.
/// </para>
/// </summary>
public sealed class CronRunStopHonestyTests
{
    private const string Gorev = "gece-yedek";
    private const string Gunluk = @"C:\ProgramData\AdaPanel\logs\tasks\gece-yedek-A1B2C3D4.log";

    /// <summary>
    /// Devre disi gorev TETIKLENMEDEN ONCE olculur; Start-ScheduledTask kosulsuz cagrilmaz.
    /// </summary>
    [Fact]
    public void RunScript_ChecksForDisabledBeforeStarting()
    {
        var betik = SchedulerService.BuildRunScript(Gorev);

        var devreDisiIndex = betik.IndexOf("ADAPANEL-DEVREDISI", StringComparison.Ordinal);
        var baslatIndex = betik.IndexOf("Start-ScheduledTask", StringComparison.Ordinal);

        Assert.True(devreDisiIndex >= 0, "Devre disi durumu hic olculmuyor.");
        Assert.True(baslatIndex > devreDisiIndex, "Gorev, devre disi olup olmadigi olculmeden tetikleniyor.");
    }

    /// <summary>
    /// Basari POZITIF BIR ISARETLE olculur. PowerShell 5.1 sonlandirici olmayan hatalarda 0 doner;
    /// yalnizca cikis koduna bakmak "tetiklendi" demeye yetmez.
    /// </summary>
    [Fact]
    public void RunScript_EmitsAPositiveSignalOnSuccess()
    {
        Assert.Contains("ADAPANEL-BASLATILDI", SchedulerService.BuildRunScript(Gorev), StringComparison.Ordinal);
    }

    /// <summary>
    /// Gorev adi tirnak icerse bile betik bozulmamali: ad tirnakli baglama ACIK sekilde kacilir.
    /// </summary>
    [Fact]
    public void RunScript_EscapesQuotesInTheTaskName()
    {
        var betik = SchedulerService.BuildRunScript("gece\"yedek");

        Assert.Contains("gece`\"yedek", betik, StringComparison.Ordinal);
    }

    /// <summary>
    /// Durdurma SUREC AGACINI oldurur. Bu kaldirilirsa panel yeniden, alt surecleri hayatta olan
    /// bir isi "durduruldu" diye raporlar.
    /// </summary>
    [Fact]
    public void StopScript_KillsTheWholeProcessTree()
    {
        var betik = SchedulerService.BuildStopScript(Gorev, Gunluk);

        Assert.Contains("taskkill.exe /PID $surecId /T /F", betik, StringComparison.Ordinal);
        Assert.Contains("Schedule.Service", betik, StringComparison.Ordinal);
        Assert.Contains("EnginePID", betik, StringComparison.Ordinal);
    }

    /// <summary>
    /// SIRA: PID topla -> agaci oldur -> Stop-ScheduledTask.
    /// <para>
    /// BU MAKINEDE OLCULDU: Stop-ScheduledTask once cagrilirsa ust surec aninda oluyor,
    /// Get-Process onu bulamiyor ve oldurme dongusu hicbir sey yapmiyor (gercek cikti:
    /// OLDURULEN=0). Zamanlayicinin is nesnesi normal cocuklari yine de oldurur, ama is
    /// nesnesinden KOPAN surecler (Chrome/chromedriver) hayatta kalir -- kullanicinin canli
    /// sunucusundaki durum tam olarak buydu. Bu sira testi o regresyonu kilitler.
    /// </para>
    /// </summary>
    [Fact]
    public void StopScript_KillsTheTreeBeforeAskingTheSchedulerToStop()
    {
        var betik = SchedulerService.BuildStopScript(Gorev, Gunluk);

        var toplama = betik.IndexOf("EnginePID", StringComparison.Ordinal);
        var oldurme = betik.IndexOf("taskkill.exe", StringComparison.Ordinal);
        // Betikteki aciklamalarda da gectigi icin KOMUTUN KENDISI aranir, adi degil.
        var durdurma = betik.IndexOf("try { Stop-ScheduledTask", StringComparison.Ordinal);

        Assert.True(toplama >= 0, "PID'ler hic toplanmiyor.");
        Assert.True(oldurme > toplama, "Oldurme, PID toplamadan once geliyor.");
        Assert.True(durdurma > oldurme, "Stop-ScheduledTask agac oldurulmeden once cagriliyor; kopan surecler hayatta kalir.");
    }

    /// <summary>
    /// Zamanlayicinin izini kaybettigi OKSUZ surec, gorevin KENDI gunluk dosyasi uzerinden bulunur;
    /// baska hicbir isaret o sureci o goreve baglamaz.
    /// </summary>
    [Fact]
    public void StopScript_AlsoFindsOrphansThroughTheTasksOwnLogPath()
    {
        var betik = SchedulerService.BuildStopScript(Gorev, Gunluk);

        Assert.Contains("Win32_Process", betik, StringComparison.Ordinal);
        Assert.Contains("$surec.CommandLine.Contains($gunluk)", betik, StringComparison.Ordinal);
        Assert.Contains(Gunluk, betik, StringComparison.Ordinal);
    }

    /// <summary>
    /// YANLIS bir PID ile /T /F sunucuyu dusurur: sistem surecleri her zaman atlanmali.
    /// </summary>
    [Fact]
    public void StopScript_NeverTouchesSystemProcesses()
    {
        var betik = SchedulerService.BuildStopScript(Gorev, Gunluk);

        Assert.Contains("if ($surecId -le 4) { continue }", betik, StringComparison.Ordinal);
        Assert.Contains("'svchost'", betik, StringComparison.Ordinal);
        Assert.Contains("'lsass'", betik, StringComparison.Ordinal);
    }

    /// <summary>
    /// taskhostw PAYLASILAN konaktir ve bu makinede OLCULDU: COM isleyicili iki ayri Windows gorevi
    /// ayni taskhostw surecini EnginePID olarak bildiriyordu. Onu surec agaciyla oldurmek, durdurmak
    /// istenen gorevle hicbir ilgisi olmayan gorevleri de dusururdu.
    /// </summary>
    [Fact]
    public void StopScript_NeverKillsTheSharedTaskHost()
    {
        Assert.Contains("'taskhostw'", SchedulerService.BuildStopScript(Gorev, Gunluk), StringComparison.Ordinal);
    }

    /// <summary>
    /// Sonuc OLCULUR: durdurmadan sonra gorev hala calisiyorsa arayuz bunu soyleyebilmeli.
    /// "Durduruldu" demek, durdugunu gormeden verilebilecek bir soz degildir.
    /// </summary>
    [Fact]
    public void StopScript_ReportsWhatActuallyHappened()
    {
        var betik = SchedulerService.BuildStopScript(Gorev, Gunluk);

        Assert.Contains("ADAPANEL-OLDURULEN=", betik, StringComparison.Ordinal);
        Assert.Contains("ADAPANEL-CALISIYOR=", betik, StringComparison.Ordinal);
    }

    /// <summary>
    /// Tek tirnakli baglamda gorev adindaki tirnak kacilmali; yoksa betik sozdizimi bozulur ve
    /// hedef yol yanlis olusur.
    /// </summary>
    [Fact]
    public void StopScript_EscapesSingleQuotesInTheTaskName()
    {
        var betik = SchedulerService.BuildStopScript("gece'yedek", Gunluk);

        Assert.Contains("$hedefYol = '\\AdaPanel\\gece''yedek'", betik, StringComparison.Ordinal);
    }

    /// <summary>
    /// Bos bir sonuc nesnesi hicbir sey iddia etmemeli.
    /// </summary>
    [Fact]
    public void ANewStopResult_ClaimsNothing()
    {
        var sonuc = new StopScheduledTaskResultDto();

        Assert.Equal(0, sonuc.KilledProcesses);
        Assert.False(sonuc.StillRunning);
    }
}
