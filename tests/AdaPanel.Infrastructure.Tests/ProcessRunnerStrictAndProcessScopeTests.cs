using System.Diagnostics;
using System.IO.Compression;
using AdaPanel.Infrastructure.Support;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Panelin dış araç çağrılarındaki İKİ ORTAK YALANI kapatan primitifler.
/// <para>
/// 1) "Çıkış kodu 0 ise iş oldu" — PowerShell'in çıkış kodu SON ifadenin sonucudur; script'in
/// ortasındaki sonlandırıcı olmayan hata (bozuk zip üzerinde <c>Expand-Archive</c>) 0 bırakır.
/// </para>
/// <para>
/// 2) "Süreç bizim dizinimizden mi çalışıyor" karşılaştırmasının ayırıcı yönüne takılması —
/// nginx tarafında bir taraf <c>/</c>, diğeri <c>\</c> kullanıyordu ve öldürme kodu hiç
/// çalışmıyordu.
/// </para>
/// </summary>
public sealed class ProcessRunnerStrictAndProcessScopeTests
{
    /// <summary>
    /// Denetimin ölçümünün birebir tekrarı: bozuk zip üzerinde Expand-Archive akışı DURDURMAZ ve
    /// powershell.exe 0 ile çıkar. Bu test "eski davranış hâlâ böyle" diye tuzağı belgeler —
    /// düzeltme, çağıranların bu 0'a güvenmeyi bırakmasıdır.
    /// </summary>
    [Fact]
    public async Task RunPowerShellAsync_CorruptArchive_StillExitsZero()
    {
        using var gecici = new GeciciKlasor();
        var bozukZip = gecici.DosyaYaz("bozuk.zip", "<html>404 Not Found</html>");
        var hedef = gecici.KlasorOlustur("cikti");

        var result = await ProcessRunner.RunPowerShellAsync(
            $"Expand-Archive -Path '{bozukZip}' -DestinationPath '{hedef}' -Force; Write-Output 'DEVAM-ETTI'");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("DEVAM-ETTI", result.StandardOutput, StringComparison.Ordinal);
    }

    /// <summary>
    /// KATI sürüm aynı script'te arızayı çıkış koduna taşır VE akışı durdurur (script'in kalanı
    /// çalışmaz). MUTASYON: <c>RunPowerShellStrictAsync</c> gövdesi düz
    /// <c>RunPowerShellAsync</c>'e döndürülürse bu test düşer.
    /// </summary>
    [Fact]
    public async Task RunPowerShellStrictAsync_CorruptArchive_FailsAndStopsTheScript()
    {
        using var gecici = new GeciciKlasor();
        var bozukZip = gecici.DosyaYaz("bozuk.zip", "<html>404 Not Found</html>");
        var hedef = gecici.KlasorOlustur("cikti");

        var result = await ProcessRunner.RunPowerShellStrictAsync(
            $"Expand-Archive -Path '{bozukZip}' -DestinationPath '{hedef}' -Force; Write-Output 'DEVAM-ETTI'");

        Assert.NotEqual(0, result.ExitCode);
        Assert.False(result.Success);
        Assert.DoesNotContain("DEVAM-ETTI", result.StandardOutput, StringComparison.Ordinal);

        // Gerçek sebep kullanıcıya ulaşmalı; boş bir hata kutusu teşhis ettirmez.
        Assert.False(string.IsNullOrWhiteSpace(result.StandardError));
    }

    /// <summary>Sağlam zip KATI sürümde de sorunsuz açılmalı: katılık, çalışan yolu bozmamalı.</summary>
    [Fact]
    public async Task RunPowerShellStrictAsync_ValidArchive_Succeeds()
    {
        using var gecici = new GeciciKlasor();
        var icerik = gecici.KlasorOlustur("kaynak");
        File.WriteAllText(Path.Combine(icerik, "index.html"), "merhaba");
        var zip = Path.Combine(gecici.Yol, "saglam.zip");
        ZipFile.CreateFromDirectory(icerik, zip);
        var hedef = gecici.KlasorOlustur("cikti");

        var result = await ProcessRunner.RunPowerShellStrictAsync(
            $"Expand-Archive -Path '{zip}' -DestinationPath '{hedef}' -Force; Write-Output 'BITTI'");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("BITTI", result.StandardOutput, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(hedef, "index.html")));
    }

    /// <summary>
    /// Yerli komutun hatası SON ifade DEĞİLSE de görülmeli (denetimin ölçtüğü ikinci tuzak).
    /// </summary>
    [Fact]
    public async Task RunPowerShellStrictAsync_NativeFailureInTheMiddle_IsReported()
    {
        var script = "cmd.exe /c exit 7\nWrite-Output 'ARDINDAN'";

        var gevsek = await ProcessRunner.RunPowerShellAsync(script);
        Assert.Equal(0, gevsek.ExitCode);

        var kati = await ProcessRunner.RunPowerShellStrictAsync(script);
        Assert.Equal(7, kati.ExitCode);
    }

    /// <summary>
    /// F3'ün TAM KÖK NEDENİ. Eski karşılaştırma kurulum dizinini ileri eğik çizgiye çeviriyor,
    /// süreç yolunu ise <c>Path.GetFullPath</c> (ters eğik çizgi) ile alıyordu → hiçbir zaman
    /// eşleşmiyordu. MUTASYON: karşılaştırma ham dizge <c>StartsWith</c>'e döndürülürse ilk satır düşer.
    /// </summary>
    [Theory]
    // Ayırıcı yönü FARKLI ama aynı dizin: eşleşmeli (eski kodun kaçırdığı durum).
    [InlineData(@"C:\ProgramData\AdaPanel\runtimes\nginx\nginx.exe", "C:/ProgramData/AdaPanel/runtimes/nginx/", true)]
    [InlineData(@"C:\ProgramData\AdaPanel\runtimes\nginx\sbin\nginx.exe", @"C:\ProgramData\AdaPanel\runtimes\nginx", true)]
    // Sondaki ayırıcı olmadan önek karşılaştırması KOMŞU kurulumu da yakalardı: yakalamamalı.
    [InlineData(@"C:\ProgramData\AdaPanel\runtimes\nginx-yedek\nginx.exe", @"C:\ProgramData\AdaPanel\runtimes\nginx", false)]
    // Tamamen başka bir yerdeki aynı adlı süreç: asla bizim değil.
    [InlineData(@"C:\Program Files\baska\nginx.exe", @"C:\ProgramData\AdaPanel\runtimes\nginx", false)]
    [InlineData("", @"C:\ProgramData\AdaPanel\runtimes\nginx", false)]
    public void IsUnderDirectory_ComparesPathsNotSeparators(string surecYolu, string kurulumDizini, bool beklenen)
    {
        Assert.Equal(beklenen, ProcessRunner.IsUnderDirectory(surecYolu, kurulumDizini));
    }

    /// <summary>
    /// GERÇEK süreçle ölçüm: kurulum dizininden çalışan süreç öldürülür, DIŞARIDAKİ aynı adlı
    /// süreç ÖLDÜRÜLMEZ. "Kilitli dizin temizlendi" iddiasının tek dürüst kanıtı budur.
    /// </summary>
    [Fact]
    public async Task KillProcessesUnder_KillsOnlyProcessesInsideTheGivenDirectory()
    {
        using var gecici = new GeciciKlasor();
        var yonetilen = gecici.KlasorOlustur("yonetilen");
        var komsu = gecici.KlasorOlustur("yonetilen-yedek");

        var kaynak = Path.Combine(Environment.SystemDirectory, "ping.exe");
        var yonetilenExe = Path.Combine(yonetilen, "ping.exe");
        var komsuExe = Path.Combine(komsu, "ping.exe");
        File.Copy(kaynak, yonetilenExe);
        File.Copy(kaynak, komsuExe);

        using var icerideki = Baslat(yonetilenExe);
        using var disaridaki = Baslat(komsuExe);
        try
        {
            // Süreçlerin gerçekten ayağa kalkmasını bekle (aksi halde test hiçbir şey ölçmez).
            Assert.True(await BeklenenSayiyaUlas(yonetilen, 1), "yonetilen surec baslamadi");
            Assert.True(await BeklenenSayiyaUlas(komsu, 1), "komsu surec baslamadi");

            var oldurulen = ProcessRunner.KillProcessesUnder(yonetilen, "ping");

            Assert.Equal(1, oldurulen);
            Assert.True(await BeklenenSayiyaUlas(yonetilen, 0), "yonetilen surec olmedi");
            Assert.Equal(1, ProcessRunner.CountProcessesUnder(komsu, "ping"));
            Assert.False(disaridaki.HasExited, "komsu kurulumun sureci OLDURULMEMELIYDI");
        }
        finally
        {
            foreach (var surec in new[] { icerideki, disaridaki })
            {
                try { if (!surec.HasExited) { surec.Kill(entireProcessTree: true); } } catch { /* en iyi caba */ }
                try { surec.WaitForExit(5000); } catch { /* en iyi caba */ }
            }
        }
    }

    private static Process Baslat(string exeYolu)
        => Process.Start(new ProcessStartInfo
        {
            FileName = exeYolu,
            Arguments = "-n 60 127.0.0.1",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true
        })!;

    private static async Task<bool> BeklenenSayiyaUlas(string dizin, int beklenen)
    {
        for (var deneme = 0; deneme < 40; deneme++)
        {
            if (ProcessRunner.CountProcessesUnder(dizin, "ping") == beklenen)
            {
                return true;
            }

            await Task.Delay(150);
        }

        return false;
    }
}

/// <summary>Test süresince yaşayan geçici klasör; sonunda en iyi çabayla silinir.</summary>
internal sealed class GeciciKlasor : IDisposable
{
    public GeciciKlasor()
    {
        Yol = Path.Combine(Path.GetTempPath(), "ada-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Yol);
    }

    public string Yol { get; }

    public string KlasorOlustur(string ad)
    {
        var tam = Path.Combine(Yol, ad);
        Directory.CreateDirectory(tam);
        return tam;
    }

    public string DosyaYaz(string ad, string icerik)
    {
        var tam = Path.Combine(Yol, ad);
        Directory.CreateDirectory(Path.GetDirectoryName(tam)!);
        File.WriteAllText(tam, icerik);
        return tam;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Yol))
            {
                Directory.Delete(Yol, true);
            }
        }
        catch
        {
            // Kilitli dosya kaldıysa geçici klasör Windows temizliğine kalır.
        }
    }
}
