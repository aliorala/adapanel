using AdaPanel.Infrastructure.Support;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Uzun PowerShell scriptlerinin çalıştırılabilmesi.
/// <para>
/// Canlı olay: bölge senkron scripti, uzun TXT için eklenen yollar birikince komut satırı
/// sınırını aştı ve <c>powershell.exe</c> HİÇ başlatılamadı — "Dosya adı veya uzantısı çok uzun".
/// Windows'ta komut satırı 32767 karakterle sınırlı ve <c>-EncodedCommand</c> base64 olduğu için
/// metni ~2.7 katına çıkarıyor; yani ~12 KB'lik bir script bile çalıştırılamıyor. Sonuç: yapılan
/// düzeltmeler birbirini boğdu ve TÜM kayıtlar aynı hatayı verdi.
/// </para>
/// </summary>
public sealed class ProcessRunnerLongScriptTests
{
    [Fact]
    public async Task RunPowerShellAsync_ExecutesAScriptFarBeyondTheCommandLineLimit()
    {
        // Komut satırına sığmayacak boyutta: yalnız yorum satırlarıyla şişirilmiş 60 KB.
        var padding = string.Join("\n", Enumerable.Range(0, 3000).Select(index => $"# dolgu satiri {index}"));
        var script = padding + "\nWrite-Output 'ADAPANEL-OK'";

        Assert.True(script.Length > 32767, "test scripti komut satiri sinirinin ustunde olmali");

        var result = await ProcessRunner.RunPowerShellAsync(script);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("ADAPANEL-OK", result.StandardOutput, StringComparison.Ordinal);
    }

    // Kisa scriptler kanitlanmis yolda kalmali: her cagri icin gecici dosya olusturmak gereksiz.
    [Fact]
    public async Task RunPowerShellAsync_StillRunsShortScripts()
    {
        var result = await ProcessRunner.RunPowerShellAsync("Write-Output 'KISA-OK'");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("KISA-OK", result.StandardOutput, StringComparison.Ordinal);
    }
}
