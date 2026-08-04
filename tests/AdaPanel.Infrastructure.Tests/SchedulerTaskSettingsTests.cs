using AdaPanel.Core.Dtos.Scheduler;
using AdaPanel.Infrastructure.Scheduler;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Zamanlanmis gorev AYARLARI (Settings) -- gorev tanimindan ayri, kolayca unutulan kisim.
/// <para>
/// CANLI OLAY: kullanicinin gunluk .bat gorevi "Running" gorunuyor ama sonucu surekli
/// 0x800710E0 (ERROR_OPERATOR_OR_ADMINISTRATOR_HAS_REFUSED_THE_REQUEST) donuyordu. Panel bu kodu
/// ham onaltilik hâliyle "Hata (0x800710E0)" diye gosteriyordu; yani kullaniciya hicbir sey
/// anlatmiyordu.
/// </para>
/// <para>
/// Kok neden: kayit sirasinda -Settings HIC VERILMIYORDU, dolayisiyla Windows'un DIZUSTU
/// BILGISAYAR icin secilmis varsayilanlari uygulaniyordu. Olculdu (New-ScheduledTaskSettingsSet):
/// DisallowStartIfOnBatteries=True, StopIfGoingOnBatteries=True, StartWhenAvailable=False,
/// ExecutionTimeLimit=PT72H. Ilk ikisi gorevin REDDEDILMESINI dogrudan uretir; sonuncusu ise
/// takilan tek bir turun butun zamanlamayi UC GUN boyunca oldurmesine izin verir -- takilan tur
/// surerken gelen her tetikleme yine 0x800710E0 ile reddedilir.
/// </para>
/// </summary>
public sealed class SchedulerTaskSettingsTests
{
    private static string Script(string taskType = "url") => SchedulerService.BuildCreateScript(
        new CreateScheduledTaskRequest
        {
            Name = "olcum",
            TaskType = taskType,
            Url = "https://ornek.test/cron",
            Command = @"C:\site\scripts\bot.bat",
            ScheduleType = "daily",
            TimeOfDay = "03:30",
            Interval = 1
        },
        plan: null);

    [Fact]
    public void CreateScript_AlwaysPassesExplicitSettings()
    {
        Assert.Contains("New-ScheduledTaskSettingsSet", Script(), StringComparison.Ordinal);
        Assert.Contains("-Settings $settings", Script(), StringComparison.Ordinal);
    }

    // Sunucu, guc durumuna gore gorev atlamaz. Sanallastirilmis makineler (Hyper-V, cesitli
    // VPS'ler) sik sik bir batarya/UPS aygiti bildirir ve varsayilan ayarla gorev REDDEDILIR.
    [Theory]
    [InlineData("-AllowStartIfOnBatteries")]
    [InlineData("-DontStopIfGoingOnBatteries")]
    public void CreateScript_DoesNotLetPowerStateBlockTheTask(string beklenen)
    {
        Assert.Contains(beklenen, Script(), StringComparison.Ordinal);
    }

    // Sunucu kapaliyken ya da yeniden baslarken kacan tur TELAFI EDILMELI: gunluk bir yedegin
    // sirf makine o dakikada mesgul diye tamamen atlanmasi sessiz veri kaybidir.
    [Fact]
    public void CreateScript_RunsAMissedOccurrenceAsSoonAsPossible()
    {
        Assert.Contains("-StartWhenAvailable", Script(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Ust uste binmeyi engellemek DOGRU, ama yalnizca bir sure siniriyla birlikte GUVENLI.
    /// <para>
    /// IgnoreNew tek basina, takilan tek bir turun sonraki butun turlari (varsayilanla uc gun
    /// boyunca) reddetmesi demektir -- kullanicinin .bat gorevinde tam olarak bu oldu. Sure
    /// siniri, takilan turu sonlandirarak zamanlamanin kendi kendine toparlanmasini saglar.
    /// </para>
    /// </summary>
    [Fact]
    public void CreateScript_BoundsHowLongASingleRunCanHoldTheSchedule()
    {
        var script = Script();

        Assert.Contains("-MultipleInstances IgnoreNew", script, StringComparison.Ordinal);
        Assert.Contains("-ExecutionTimeLimit", script, StringComparison.Ordinal);
        Assert.DoesNotContain("-ExecutionTimeLimit (New-TimeSpan -Hours 72)", script, StringComparison.Ordinal);
    }

    // Ayarlar gorev tipinden bagimsizdir: .bat calistiran bir komut gorevi de URL gorevi kadar
    // reddedilmeye acikti.
    [Theory]
    [InlineData("url")]
    [InlineData("command")]
    public void CreateScript_AppliesTheSameSettingsToEveryTaskType(string taskType)
    {
        Assert.Contains("-AllowStartIfOnBatteries", Script(taskType), StringComparison.Ordinal);
    }

    /// <summary>
    /// Klasor verilmeyen bir .bat gorevi, DOSYANIN KENDI klasorunde calismali.
    /// <para>
    /// Gorev SYSTEM olarak calisir ve SYSTEM'in calisma klasoru C:\Windows\system32'dir. Bir .bat
    /// neredeyse her zaman kendi klasorunde oldugunu varsayar (yanindaki betigi cagirir, ciktisini
    /// yanina yazar); klasor verilmezse gorev "calisti" gorunur ama hicbir sey yapmaz. "php" tipinde
    /// bu kural zaten vardi, "command" tipinde unutulmustu.
    /// </para>
    /// </summary>
    [Fact]
    public void CreateScript_CommandTask_DefaultsTheWorkingDirectoryToTheFilesOwnFolder()
    {
        // GERCEKTEN VAR OLAN bir klasor kullanilir: turetilen klasor artik diskte dogrulaniyor
        // (bkz. bir alttaki test), dolayisiyla hayali bir yol bu kurali sinayamaz.
        var klasor = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);

        var script = SchedulerService.BuildCreateScript(
            new CreateScheduledTaskRequest
            {
                Name = "bot",
                TaskType = "command",
                Command = Path.Combine(klasor, "bot.bat"),
                ScheduleType = "daily",
                TimeOfDay = "03:30",
                Interval = 1
            },
            plan: null);

        Assert.Contains($"-WorkingDirectory '{klasor}'", script, StringComparison.Ordinal);
    }

    /// <summary>
    /// Turetilen calisma klasoru diskte YOKSA hic yazilmaz.
    /// <para>
    /// CANLI OLAY: bir gorev uc saniyede 0x8007010B ("Dizin adi gecersiz") ile dustu ve hicbir
    /// cikti uretmedi -- cunku Task Scheduler, calisma klasoru gecersizse sureci HIC OLUSTURMAZ.
    /// Komut dogru olsa bile gorev calismaz. Kolaylik olsun diye turettigimiz bir klasor ugruna
    /// gorevi calisamaz hale getirmek, yardimin isi bozmasi olurdu; boyle bir durumda klasoru hic
    /// vermeyip Windows'un varsayilanina birakmak her zaman daha iyidir.
    /// </para>
    /// </summary>
    [Fact]
    public void CreateScript_CommandTask_OmitsAWorkingDirectoryThatDoesNotExist()
    {
        var script = SchedulerService.BuildCreateScript(
            new CreateScheduledTaskRequest
            {
                Name = "bot",
                TaskType = "command",
                Command = @"C:\boyle-bir-klasor-yok-98765\bot.bat",
                ScheduleType = "daily",
                TimeOfDay = "03:30",
                Interval = 1
            },
            plan: null);

        Assert.DoesNotContain("-WorkingDirectory", script, StringComparison.Ordinal);
    }

    // PATH uzerinden cozulen komutlarda klasor TURETILMEZ: "ipconfig" bir dosya yolu degildir.
    [Theory]
    [InlineData("ipconfig", false)]
    [InlineData("python", false)]
    [InlineData(@"C:\betikler\calistir.cmd", true)]
    public void LooksLikeFilePath_OnlyAcceptsCommandsThatCarryAFolder(string komut, bool beklenen)
    {
        Assert.Equal(beklenen, SchedulerService.LooksLikeFilePath(komut));
    }
}

/// <summary>
/// Argumansiz kabuk gorevi (cmd.exe / powershell.exe) reddedilmeli.
/// <para>
/// CANLI OLAY: kullanicinin gorevi "Running" durumunda takildi ve her tetikleme 0x800710E0 ile
/// reddedildi. Sebep zinciri: betik gorevleri Windows'a "cmd.exe /c ...bat" olarak kaydedilir ve
/// panel bir sure bunu geri cozmuyordu (0.2.70'te duzeltildi) -- duzenleme ekrani komut alanina
/// "cmd.exe" yaziyordu. Kullanici hicbir seyi bilerek degistirmeden kaydettiginde gorev, betigi
/// yerine ARGUMANSIZ BIR KABUK calistiran bir goreve donusuyordu.
/// </para>
/// <para>
/// Boyle bir gorev hicbir zaman bitmez: argumansiz cmd.exe etkilesimli kabuk acar ve girdi bekler,
/// gorevin altinda konsol olmadigi icin sonsuza kadar bekler. Sebep ortadan kaldirildi; bu denetim
/// ayni sonucu BASKA bir yoldan uretmeyi de engeller.
/// </para>
/// </summary>
public sealed class BareShellTaskTests
{
    [Theory]
    [InlineData("cmd.exe")]
    [InlineData("CMD.EXE")]
    [InlineData("powershell.exe")]
    [InlineData(@"C:\Windows\System32\cmd.exe")]
    [InlineData("\"cmd.exe\"")]
    public void BareShell_IsRecognisedWhateverItLooksLike(string komut)
    {
        Assert.True(SchedulerService.IsBareInteractiveShell(komut, string.Empty));
    }

    // Argumanli kabuk MESRUDUR: "cmd.exe /c ..." tam olarak betik gorevlerinin calisma bicimidir.
    [Theory]
    [InlineData("cmd.exe", @"/c ""C:\site\bot.bat""")]
    [InlineData("powershell.exe", @"-File ""C:\betik\temizlik.ps1""")]
    public void ShellWithArguments_StaysAllowed(string komut, string argumanlar)
    {
        Assert.False(SchedulerService.IsBareInteractiveShell(komut, argumanlar));
    }

    [Theory]
    [InlineData("curl.exe")]
    [InlineData(@"C:\site\bot.bat")]
    [InlineData("ipconfig")]
    public void OrdinaryCommands_AreNotMistakenForAShell(string komut)
    {
        Assert.False(SchedulerService.IsBareInteractiveShell(komut, string.Empty));
    }
}
