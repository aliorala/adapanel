using AdaPanel.Infrastructure.SelfUpdate;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Kendi kendini güncelleme betiğinin sözleşmesi.
///
/// Canlı sunucuda doğrulanmış olayın regresyonu (apply-update.log): kurulum yardımcısı
/// <c>/CLOSEAPPLICATIONS</c> ile çalıştırılıyordu. Panel bir WINDOWS SERVİSİ olduğu için
/// RestartManager onu pencere kapatarak durduramadı, Abort/Retry/Ignore sordu ve
/// <c>/SUPPRESSMSGBOXES</c> altında bu soru ABORT'a düştü: kurulum iptal edildi, değişiklikler geri
/// alındı ve çıkış kodu 5 döndü. Yani panel kendi kendini HİÇ güncelleyemiyordu.
/// </summary>
public sealed class UpdateInstallerLauncherTests
{
    private static string Script() => WindowsUpdateInstallerLauncher.BuildLauncherScript(
        @"C:\ProgramData\AdaPanel\updates\AdaPanelSetup.exe",
        "ABC123",
        @"C:\ProgramData\AdaPanel\updates\apply-update.log");

    // Servis KURULUMDAN ÖNCE durdurulmalı: dosyaları tutan süreç kalmazsa kurulum yardımcısının
    // uygulama kapatmaya çalışmasına hiç gerek kalmaz.
    [Fact]
    public void LauncherStopsTheServiceBeforeRunningTheInstaller()
    {
        var script = Script();
        var stopIndex = script.IndexOf("Stop-Service", StringComparison.Ordinal);
        var installIndex = script.IndexOf("Start-Process", StringComparison.Ordinal);

        Assert.True(stopIndex >= 0, "Betik servisi durdurmuyor.");
        Assert.True(installIndex >= 0, "Betik kurulumu başlatmıyor.");
        Assert.True(stopIndex < installIndex, "Servis, kurulumdan SONRA durduruluyor; sıra yanlış.");
    }

    [Fact]
    public void LauncherDoesNotAskTheInstallerToCloseApplications()
    {
        Assert.DoesNotContain("/CLOSEAPPLICATIONS", Script(), StringComparison.OrdinalIgnoreCase);
    }

    // Kurulum ne olursa olsun panel ayakta bırakılmalı; aksi hâlde başarısız bir güncelleme
    // sunucuyu erişilemez bırakır.
    [Fact]
    public void LauncherAlwaysTriesToStartTheServiceAgain()
    {
        var script = Script();
        Assert.Contains("Start-Service", script, StringComparison.Ordinal);
        Assert.Contains("is NOT running after retries", script, StringComparison.Ordinal);
    }

    // Paket, kurulumdan hemen önce yeniden doğrulanmalı: indirme ile kurulum arasında dosya
    // değişmiş olabilir.
    [Fact]
    public void LauncherVerifiesThePackageHashBeforeInstalling()
    {
        var script = Script();
        Assert.Contains("Get-FileHash", script, StringComparison.Ordinal);
        Assert.Contains("checksum changed", script, StringComparison.OrdinalIgnoreCase);
    }
}
