using System.Text.RegularExpressions;
using AdaPanel.Infrastructure.Support;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// PowerShell, ASCII ' disinda U+2018..U+201B karakterlerini de tek tirnak sayar ve betik
/// -EncodedCommand ile gittigi icin bu karakterler korunur. Yalniz ASCII ' ikileyen kacis,
/// "x’; komut; ’" ile literal'den cikilip LocalSystem olarak kod calistirmaya izin veriyordu.
/// </summary>
public sealed class PowerShellQuotingTests
{
    [Theory]
    [InlineData("'", "''")]
    [InlineData("‘", "‘‘")]
    [InlineData("’", "’’")]
    [InlineData("‚", "‚‚")]
    [InlineData("‛", "‛‛")]
    [InlineData("x’; calc; ’", "x’’; calc; ’’")]
    [InlineData("o'brien", "o''brien")]
    [InlineData("duz metin $x `n {0}", "duz metin $x `n {0}")]
    // Cift tirnak varyantlari tek tirnakli dizede ozel degildir: dokunulmaz.
    [InlineData("\"a\" “b” „c", "\"a\" “b” „c")]
    [InlineData("", "")]
    public void Escape_tek_tirnak_ailesini_ikiler(string input, string expected)
    {
        Assert.Equal(expected, PowerShellQuoting.Escape(input));
        Assert.Equal("'" + expected + "'", PowerShellQuoting.Literal(input));
    }

    [Fact]
    public void Escape_null_bos_dizedir_ve_NUL_reddedilir()
    {
        Assert.Equal(string.Empty, PowerShellQuoting.Escape(null));
        Assert.Equal("''", PowerShellQuoting.Literal(null));
        Assert.Throws<ArgumentException>(() => PowerShellQuoting.Escape("a\0b"));
    }

    /// <summary>Gercek powershell.exe: yuk calismamali, deger birebir geri okunmali.</summary>
    [Theory]
    [InlineData("x’; New-Item -ItemType File -Path '{0}' | Out-Null; ’")]
    [InlineData("x‘; New-Item -ItemType File -Path '{0}' | Out-Null; ‘")]
    [InlineData("x‚; New-Item -ItemType File -Path '{0}' | Out-Null; ‚")]
    [InlineData("x‛; New-Item -ItemType File -Path '{0}' | Out-Null; ‛")]
    [InlineData("x'; New-Item -ItemType File -Path '{0}' | Out-Null; '")]
    public async Task Gercek_powershell_enjekte_yuku_calistirmaz(string payloadTemplate)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var marker = Path.Combine(Path.GetTempPath(), "ada-psq-" + Guid.NewGuid().ToString("N") + ".marker");
        var payload = payloadTemplate.Replace("{0}", marker.Replace("'", "''"), StringComparison.Ordinal);
        try
        {
            var script = "$v = " + PowerShellQuoting.Literal(payload) + "\n[Console]::Out.Write($v)";
            var result = await ProcessRunner.RunPowerShellAsync(script);

            Assert.False(File.Exists(marker), "Enjekte edilen komut calisti.");
            Assert.Equal(0, result.ExitCode);
            Assert.Equal(payload, result.StandardOutput.Trim());
        }
        finally
        {
            if (File.Exists(marker))
            {
                File.Delete(marker);
            }
        }
    }

    /// <summary>
    /// Elle <c>Replace("'", "''")</c> kopyasi geri gelmesin: PowerShell icin tek kacis
    /// <see cref="PowerShellQuoting"/>. SQL / PHP / sh kacislari (ayri diller) listede degil.
    /// </summary>
    [Fact]
    public void Altyapida_elle_yazilmis_PowerShell_tek_tirnak_kacisi_kalmadi()
    {
        var kok = Path.Combine(ScriptTestHost.RepoRoot, "src", "AdaPanel.Infrastructure");
        var sqlDosyalari = new[] { "Database", "MailService.cs" };
        var desen = new Regex(@"Replace\(\s*""'""\s*,\s*""''""");
        var ihlaller = new List<string>();
        foreach (var dosya in Directory.EnumerateFiles(kok, "*.cs", SearchOption.AllDirectories))
        {
            var goreli = Path.GetRelativePath(kok, dosya);
            if (goreli.EndsWith("PowerShellQuoting.cs", StringComparison.Ordinal)
                || goreli.Split(Path.DirectorySeparatorChar).Any(parca => sqlDosyalari.Contains(parca)))
            {
                continue;
            }

            var satirlar = File.ReadAllLines(dosya);
            for (var i = 0; i < satirlar.Length; i++)
            {
                if (desen.IsMatch(satirlar[i]))
                {
                    ihlaller.Add($"{goreli}:{i + 1}");
                }
            }
        }

        Assert.True(ihlaller.Count == 0, "Elle PowerShell kacisi: " + string.Join(", ", ihlaller));
    }

    [Theory]
    [InlineData("Mail/MailEnableMailService.cs", "PsLiteralRaw")]
    [InlineData("Dns/DnsService.cs", "EscapePowerShellLiteral")]
    [InlineData("Dns/DnsService.cs", "EscapeSingleQuoted")]
    [InlineData("Ftp/FtpService.cs", "EscapePowerShellLiteral")]
    [InlineData("Iis/DiskQuotaService.cs", "EscapePowerShellLiteral")]
    [InlineData("Nginx/NginxInstallationSupport.cs", "EscapePowerShell")]
    [InlineData("Scheduler/SchedulerService.cs", "EscapeSingleQuoted")]
    [InlineData("SelfUpdate/WindowsUpdateInstallerLauncher.cs", "EscapePowerShellLiteral")]
    [InlineData("Firewall/FirewallService.cs", "TekTirnak")]
    [InlineData("Security/BruteForceProtectionService.cs", "TekTirnak")]
    [InlineData("Support/LimitedIdentityRunner.cs", "TekTirnak")]
    public void Eski_yardimcilar_ortak_siniftan_gecer(string dosya, string ad)
    {
        var yol = Path.Combine(ScriptTestHost.RepoRoot, "src", "AdaPanel.Infrastructure", dosya.Replace('/', Path.DirectorySeparatorChar));
        var metin = File.ReadAllText(yol);
        var tanim = new Regex(@"static string " + ad + @"\(string value\)\s*(=>|\{\s*return)\s*PowerShellQuoting\.Escape\(");
        Assert.Matches(tanim, metin);
    }

    [Fact]
    public void Cift_tirnakli_kacis_kalmadi()
    {
        var kok = Path.Combine(ScriptTestHost.RepoRoot, "src", "AdaPanel.Infrastructure");
        foreach (var dosya in new[] { "SelfUpdate/WindowsUpdateInstallerLauncher.cs", "Security/BruteForceRecovery.cs" })
        {
            var metin = File.ReadAllText(Path.Combine(kok, dosya.Replace('/', Path.DirectorySeparatorChar)));
            Assert.DoesNotContain("`\\\"", metin);
        }
    }
}
