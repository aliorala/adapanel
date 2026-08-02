using AdaPanel.Infrastructure.Scheduler;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// "script" görev tipinin güvenlik ve doğruluk özü: uzantı beyaz listesi, site kökü kapsaması
/// ve boşluklu yollarda doğru komut satırı üretimi.
/// </summary>
public sealed class ScriptTaskPlannerTests
{
    [Theory]
    [InlineData(@"C:\sites\site1\cron.bat")]
    [InlineData(@"C:\sites\site1\cron.CMD")]
    [InlineData(@"C:\sites\site1\cron.Ps1")]
    public void IsAllowedExtension_AcceptsOnlyBatchAndPowerShellScripts(string path)
    {
        Assert.True(ScriptTaskPlanner.IsAllowedExtension(path));
    }

    // Yürütülebilir/keyfi uzantılar reddedilmeli: aksi hâlde "betik" tipi ham komut çalıştırmaya dönerdi.
    [Theory]
    [InlineData(@"C:\sites\site1\cron.exe")]
    [InlineData(@"C:\sites\site1\cron.vbs")]
    [InlineData(@"C:\sites\site1\cron.php")]
    [InlineData(@"C:\sites\site1\cron")]
    [InlineData(@"C:\sites\site1\cron.bat.exe")]
    public void IsAllowedExtension_RejectsEverythingElse(string path)
    {
        Assert.False(ScriptTaskPlanner.IsAllowedExtension(path));
    }

    // Kullanıcı metni ".bat" içerse bile karar Path.GetExtension'a göre verilir: "cron.bat.exe" ve
    // sondaki nokta/boşluk hilesi ("cron.exe.bat " değil, "cron.bat." → ".bat" değil) buradan geçmez.
    [Fact]
    public void IsAllowedExtension_IgnoresBatSubstringsInsideTheFileName()
    {
        Assert.False(ScriptTaskPlanner.IsAllowedExtension(@"C:\sites\site1\bat.exe"));
        Assert.False(ScriptTaskPlanner.IsAllowedExtension(@"C:\sites\site1\backup.bat.cmdx"));
    }

    [Fact]
    public void NormalizePath_RejectsRelativeAndEmptyPaths()
    {
        Assert.Null(ScriptTaskPlanner.NormalizePath(null));
        Assert.Null(ScriptTaskPlanner.NormalizePath("   "));
        Assert.Null(ScriptTaskPlanner.NormalizePath(@"cron.bat"));
        Assert.Null(ScriptTaskPlanner.NormalizePath(@"..\cron.bat"));
    }

    [Fact]
    public void NormalizePath_CollapsesDotSegmentsAndAltSeparators()
    {
        Assert.Equal(@"C:\sites\site1\cron.bat", ScriptTaskPlanner.NormalizePath(@"C:\sites\site2\..\site1\cron.bat"));
        Assert.Equal(@"C:\sites\site1\cron.bat", ScriptTaskPlanner.NormalizePath(@"C:/sites/site1/./cron.bat"));
    }

    [Fact]
    public void IsInsideRoot_AcceptsFilesUnderTheSiteRoot()
    {
        Assert.True(ScriptTaskPlanner.IsInsideRoot(@"C:\sites\site1", @"C:\sites\site1\cron.bat"));
        Assert.True(ScriptTaskPlanner.IsInsideRoot(@"C:\sites\site1\", @"C:\sites\site1\bin\deploy\run.ps1"));
        // Sürücü kökü sınır durumu: "C:\" kırpıldığında "C:" kalır, ayırıcı denetimi yine doğru çalışmalı.
        Assert.True(ScriptTaskPlanner.IsInsideRoot(@"C:\", @"C:\cron.bat"));
        // Büyük/küçük harf Windows'ta anlamsızdır; meşru yolu reddetmemeli.
        Assert.True(ScriptTaskPlanner.IsInsideRoot(@"c:\SITES\Site1", @"C:\sites\site1\cron.bat"));
    }

    // ".." ile site kökünden kaçış: normalleştirme olmadan StartsWith bunu KABUL ederdi.
    [Theory]
    [InlineData(@"C:\sites\site1\..\site2\cron.bat")]
    [InlineData(@"C:\sites\site1\public\..\..\..\Windows\System32\evil.bat")]
    [InlineData(@"C:\sites\site1/../site2/cron.bat")]
    public void IsInsideRoot_RejectsParentDirectoryEscapes(string candidate)
    {
        Assert.False(ScriptTaskPlanner.IsInsideRoot(@"C:\sites\site1", candidate));
    }

    // KÖK-ADI-ÖNEKİ TUZAĞI: "C:\sites\site1-evil", "C:\sites\site1" ile StartsWith olarak eşleşir.
    // Ayırıcı sınırı denetlenmezse bir müşteri komşusunun klasöründeki betiği çalıştırabilirdi.
    [Theory]
    [InlineData(@"C:\sites\site1-evil\cron.bat")]
    [InlineData(@"C:\sites\site1evil\cron.bat")]
    [InlineData(@"C:\sites\site10\cron.bat")]
    [InlineData(@"C:\sites\site1.backup\cron.bat")]
    public void IsInsideRoot_RejectsSiblingsThatSharePrefixWithTheRoot(string candidate)
    {
        Assert.False(ScriptTaskPlanner.IsInsideRoot(@"C:\sites\site1", candidate));
    }

    [Fact]
    public void IsInsideRoot_RejectsTheRootItselfAndMissingInput()
    {
        Assert.False(ScriptTaskPlanner.IsInsideRoot(@"C:\sites\site1", @"C:\sites\site1"));
        Assert.False(ScriptTaskPlanner.IsInsideRoot(@"C:\sites\site1", @"C:\sites\site1\"));
        Assert.False(ScriptTaskPlanner.IsInsideRoot(null, @"C:\sites\site1\cron.bat"));
        Assert.False(ScriptTaskPlanner.IsInsideRoot(@"C:\sites\site1", null));
        Assert.False(ScriptTaskPlanner.IsInsideRoot(@"C:\sites\site1", "cron.bat"));
    }

    [Fact]
    public void BuildAction_WrapsBatchScriptsInCmdWithOuterQuotes()
    {
        var (execute, arguments) = ScriptTaskPlanner.BuildAction(@"C:\sites\site1\cron.bat", null);

        Assert.Equal("cmd.exe", execute);
        // Dış tırnak şart: tırnak sayısı ikiden fazla olunca cmd ilk/son tırnağı SİLMEZ.
        Assert.Equal("/c \"\"C:\\sites\\site1\\cron.bat\"\"", arguments);
    }

    [Fact]
    public void BuildAction_QuotesPathsThatContainSpaces()
    {
        var (execute, arguments) = ScriptTaskPlanner.BuildAction(@"C:\web sites\my site\daily job.cmd", "--full run");

        Assert.Equal("cmd.exe", execute);
        Assert.Equal("/c \"\"C:\\web sites\\my site\\daily job.cmd\" --full run\"", arguments);
    }

    [Fact]
    public void BuildAction_UsesPowerShellFileSwitchForPs1()
    {
        var (execute, arguments) = ScriptTaskPlanner.BuildAction(@"C:\web sites\my site\backup.ps1", null);

        Assert.Equal("powershell.exe", execute);
        Assert.Equal("-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"C:\\web sites\\my site\\backup.ps1\"", arguments);
    }

    [Fact]
    public void BuildAction_AppendsScriptArgumentsAfterTheFileSwitch()
    {
        // -File SON parametredir; ondan sonrası betiğin kendi argümanlarıdır, ters sıra sessizce bozardı.
        var (_, arguments) = ScriptTaskPlanner.BuildAction(@"C:\sites\site1\backup.ps1", "  -Target Full  ");

        Assert.Equal("-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"C:\\sites\\site1\\backup.ps1\" -Target Full", arguments);
    }

    // Sınırlı kimlik, Task Scheduler'ın GERÇEKTEN kabul ettiği bir hesap olmalı. Uygulama havuzu
    // sanal hesabı ("IIS APPPOOL\…") bu makinede denenen her yolda reddedildi ya da — schtasks'ta —
    // "SUCCESS" deyip kimliksiz, hiç çalışmayan bir görev bıraktı. Bu test o hesaba geri dönülmesini
    // engeller.
    [Fact]
    public void LimitedUserId_IsAServiceAccountTheSchedulerAccepts()
    {
        Assert.Equal(@"NT AUTHORITY\LOCAL SERVICE", ScriptTaskPlanner.LimitedUserId);
        Assert.DoesNotContain("APPPOOL", ScriptTaskPlanner.LimitedUserId, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("cmd.exe", "/c \"\"C:\\sites\\s\\cron.bat\"\"", true)]
    [InlineData("C:\\Windows\\System32\\cmd.exe", "/C \"x\"", true)]
    [InlineData("powershell.exe", "-NoProfile -File \"C:\\sites\\s\\b.ps1\"", true)]
    [InlineData("powershell.exe", "-Command Restart-Service W3SVC", false)]
    [InlineData("curl.exe", "-fsSL \"https://ornek.test\"", false)]
    [InlineData("C:\\tools\\backup.exe", "--now", false)]
    public void IsScriptAction_DerivesTheTypeFromTheRegisteredAction(string execute, string arguments, bool expected)
    {
        Assert.Equal(expected, ScriptTaskPlanner.IsScriptAction(execute, arguments));
    }

    // Canlı testte yakalanan hatanın regresyonu: site kökünde duran bir bot.bat için çalışma dizini
    // kökün ta kendisine düşüyordu ve görev "site dışında" diye 403 ile reddediliyordu. Yani site
    // kökündeki HİÇBİR betik zamanlanamıyordu.
    [Theory]
    [InlineData(@"C:\inetpub\wwwroot\test.local", @"C:\inetpub\wwwroot\test.local")]
    [InlineData(@"C:\inetpub\wwwroot\test.local", @"C:\inetpub\wwwroot\test.local\")]
    [InlineData(@"C:\inetpub\wwwroot\test.local\", @"C:\inetpub\wwwroot\test.local")]
    [InlineData(@"C:\inetpub\wwwroot\test.local", @"C:\inetpub\wwwroot\test.local\alt")]
    public void IsRootOrInsideRoot_AcceptsTheRootItselfAsAWorkingDirectory(string root, string candidate)
    {
        Assert.True(ScriptTaskPlanner.IsRootOrInsideRoot(root, candidate));
    }

    // Gevşetme yalnızca "kökün kendisi" ile sınırlı kalmalı: dışarısı ve ön ek benzerliği hâlâ ret.
    [Theory]
    [InlineData(@"C:\inetpub\wwwroot\test.local", @"C:\Windows\System32")]
    [InlineData(@"C:\inetpub\wwwroot\test.local", @"C:\inetpub\wwwroot\test.local-evil")]
    [InlineData(@"C:\inetpub\wwwroot\test.local", @"C:\inetpub\wwwroot\test.local\..\baska")]
    [InlineData(@"C:\inetpub\wwwroot\test.local", null)]
    public void IsRootOrInsideRoot_StillRejectsAnythingOutsideTheRoot(string root, string? candidate)
    {
        Assert.False(ScriptTaskPlanner.IsRootOrInsideRoot(root, candidate));
    }

    // Betik için kural DEĞİŞMEDİ: klasörün kendisi çalıştırılabilir bir hedef değildir.
    [Fact]
    public void IsInsideRoot_StillRejectsTheRootItself_ForScriptPaths()
    {
        Assert.False(ScriptTaskPlanner.IsInsideRoot(@"C:\inetpub\wwwroot\test.local", @"C:\inetpub\wwwroot\test.local"));
    }
}
