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
    //
    // NOT: Bu test önce günlükteki birebir bir cümleyi ("is NOT running after retries") arıyordu.
    // Kırılgandı: günlük metni yeniden yazıldığında test kırıldı ama korunan davranış hiç
    // bozulmamıştı. Artık DAVRANIŞ sınanıyor — servis başlatma denemesi, tekrar döngüsü ve
    // başarısızlığın KULLANICIYA bildirilmesi.
    [Fact]
    public void LauncherAlwaysTriesToStartTheServiceAgain()
    {
        var script = Script();
        Assert.Contains("Start-Service", script, StringComparison.Ordinal);

        // Tek deneme yetmez: servis ilk denemede kalkmayabilir.
        Assert.Contains("while (", script, StringComparison.Ordinal);

        // Servis kalkmadıysa bu SESSİZ GEÇİLMEZ; kullanıcı panelinin kapalı olduğunu bilmeli.
        Assert.Contains("panel servisi baslatilamadi", script, StringComparison.OrdinalIgnoreCase);
    }

    // Kurulumun BAŞARISI, servisin kaç saniyede kalktığına göre yargılanmamalı. Yüklü bir
    // sunucuda panel geç açılır ve bu, kurulumun başarısız olduğu anlamına gelmez: bir zaman
    // aşımı yüzünden başarılı kurulum "tamamlanamadı" diye raporlanıyordu.
    [Fact]
    public void LauncherJudgesSuccessByTheInstalledVersionNotByATimeout()
    {
        var script = Script();
        Assert.Contains("VersionInfo.ProductVersion", script, StringComparison.Ordinal);
        Assert.Contains("$kuruldu", script, StringComparison.Ordinal);
    }

    // Başarısızlık sebebi ve kurulum çıkış kodu KORUNMALI. Önce en sondaki satır ikisini de
    // genel bir metinle eziyordu; panel kullanıcıya yalnız "Kurulum tamamlanamadı" diyebiliyor,
    // gerçek sebebi öğrenmek için sunucuya bağlanmak gerekiyordu.
    [Fact]
    public void LauncherKeepsTheFailureReasonAndExitCode()
    {
        var script = Script();
        Assert.Contains("$failReason", script, StringComparison.Ordinal);
        Assert.Contains("$failExitCode", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Write-UpdateState 'failed' 'Kurulum tamamlanamadi.' $null", script, StringComparison.Ordinal);
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
