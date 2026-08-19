using System.Diagnostics;
using System.Text;
using AdaPanel.Infrastructure.Bootstrap;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// K1 — Stalwart süper-admin parolası artık diske DÜZ METİN yazılmaz.
/// </summary>
/// <remarks>
/// <para>
/// ÖLÇÜLEN AÇIK: <c>%ProgramData%\AdaPanel\runtimes\stalwart\run-stalwart.cmd</c> içinde
/// <c>set "STALWART_RECOVERY_ADMIN=admin:&lt;parola&gt;"</c> satırı duruyordu ve dosya
/// YÜKSELTİLMEMİŞ bir kullanıcı olarak okunabiliyordu (270 bayt) — çünkü <c>runtimes</c>
/// <c>BUILTIN\Users : ReadAndExecute</c> mirasını taşır. Barındırılan herhangi bir sitenin PHP'si
/// bu dosyayı okuyup sunucudaki BÜTÜN alan adlarının postasına tam yetkiyle sahip olabilirdi.
/// </para>
/// <para>
/// Bu testler wrapper'ı taklit ETMEZ: üretilen <c>.cmd</c> dosyası GERÇEKTEN çalıştırılır ve
/// <c>STALWART_RECOVERY_ADMIN</c>'in çocuk sürece doğru ulaştığı ölçülür. Yani "parolayı kaldırdım
/// ama posta sunucusu artık kimlik alamıyor" regresyonu testte görünür.
/// </para>
/// </remarks>
public sealed class StalwartRecoverySecretTests : IDisposable
{
    private readonly string calisma = Path.Combine(Path.GetTempPath(), $"ada-stalwart-{Guid.NewGuid():N}");

    public StalwartRecoverySecretTests() => Directory.CreateDirectory(calisma);

    public void Dispose()
    {
        try { Directory.Delete(calisma, recursive: true); } catch { /* en iyi caba */ }
    }

    /// <summary>Wrapper metninde parola HİÇBİR biçimde geçmemeli.</summary>
    [Fact]
    public void Wrapper_icerigi_parolayi_TASIMAZ()
    {
        const string parola = "CokGizliParola123";
        var icerik = ComponentBootstrapper.BuildStalwartWrapperContent(
            Path.Combine(calisma, "stalwart.exe"),
            Path.Combine(calisma, "etc", "config.json"),
            Path.Combine(calisma, "logs", "stalwart.log"),
            Path.Combine(calisma, "etc", ComponentBootstrapper.StalwartRecoverySecretFileName));

        Assert.DoesNotContain(parola, icerik, StringComparison.Ordinal);
        Assert.DoesNotContain("admin:", icerik, StringComparison.Ordinal);
        // Kimlik ortam değişkeni yine kuruluyor olmalı: yalnızca KAYNAĞI değişti.
        Assert.Contains("STALWART_RECOVERY_ADMIN", icerik, StringComparison.Ordinal);
        Assert.Contains(ComponentBootstrapper.StalwartRecoverySecretFileName, icerik, StringComparison.Ordinal);
    }

    /// <summary>Korunan dosya, parolanın kendisini düz metin olarak İÇERMEMELİ.</summary>
    [Fact]
    public void Korunan_kimlik_dosyasi_duz_metin_parola_icermez()
    {
        const string parola = "BaskaBirGizliParola987";
        var korunan = ComponentBootstrapper.ProtectStalwartSecret($"admin:{parola}");

        Assert.DoesNotContain(parola, Encoding.UTF8.GetString(korunan), StringComparison.Ordinal);
        Assert.DoesNotContain(parola, Encoding.ASCII.GetString(korunan), StringComparison.Ordinal);
        // Gerçekten korunmuş: ham UTF-8 kopyasından da uzun/farklı olmalı.
        Assert.NotEqual(Encoding.UTF8.GetBytes($"admin:{parola}"), korunan);
    }

    /// <summary>
    /// GERÇEK YOL: üretilen wrapper çalıştırıldığında posta sunucusu süreci kimliği ALIR.
    /// "stalwart.exe" yerine, aldığı ortam değişkenini log'a yazan sahte bir çalıştırılabilir konur;
    /// böylece wrapper'ın çözme adımı (DPAPI + cmd tırnaklaması) gerçekten ölçülür.
    /// </summary>
    [Fact]
    public void Wrapper_calistirildiginda_kimlik_surece_ULASIR()
    {
        const string parola = "GercekYolTest_2026";
        var etc = Path.Combine(calisma, "etc");
        var logs = Path.Combine(calisma, "logs");
        Directory.CreateDirectory(etc);
        Directory.CreateDirectory(logs);

        var secretPath = Path.Combine(etc, ComponentBootstrapper.StalwartRecoverySecretFileName);
        File.WriteAllBytes(secretPath, ComponentBootstrapper.ProtectStalwartSecret($"admin:{parola}"));

        // Sahte "stalwart.exe": aldığı ortam değişkenini stdout'a basar (wrapper log'a yönlendirir).
        var sahteSunucu = Path.Combine(calisma, "stalwart.cmd");
        File.WriteAllText(sahteSunucu, "@echo off\r\necho GORULEN=%STALWART_RECOVERY_ADMIN%\r\n");

        var logPath = Path.Combine(logs, "stalwart.log");
        var wrapperPath = Path.Combine(calisma, "run-stalwart.cmd");
        File.WriteAllText(
            wrapperPath,
            ComponentBootstrapper.BuildStalwartWrapperContent(
                sahteSunucu,
                Path.Combine(etc, "config.json"),
                logPath,
                secretPath));

        using (var surec = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"\"{wrapperPath}\"\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        })!)
        {
            surec.StandardOutput.ReadToEnd();
            surec.StandardError.ReadToEnd();
            Assert.True(surec.WaitForExit(120_000), "run-stalwart.cmd zaman asimina ugradi.");
        }

        var log = File.ReadAllText(logPath);
        Assert.Contains($"GORULEN=admin:{parola}", log, StringComparison.Ordinal);

        // Ve dosyanın kendisi hâlâ parolasız: kiracı okusa bile eline bir şey geçmez.
        Assert.DoesNotContain(parola, File.ReadAllText(wrapperPath), StringComparison.Ordinal);
    }
}
