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
    private const string InnoLogPath = @"C:\ProgramData\AdaPanel\updates\apply-update.log";
    private const string RunnerLogPath = @"C:\ProgramData\AdaPanel\updates\update-runner.log";

    private static string Script() => WindowsUpdateInstallerLauncher.BuildLauncherScript(
        @"C:\ProgramData\AdaPanel\updates\AdaPanelSetup.exe",
        "ABC123",
        InnoLogPath,
        RunnerLogPath);

    // Servis KURULUMDAN ÖNCE durdurulmalı: dosyaları tutan süreç kalmazsa kurulum yardımcısının
    // uygulama kapatmaya çalışmasına hiç gerek kalmaz.
    [Fact]
    public void LauncherStopsTheServiceBeforeRunningTheInstaller()
    {
        var script = Script();
        var stopIndex = script.IndexOf("$svc.Stop()", StringComparison.Ordinal);
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

    /// <summary>
    /// K11c — KANIT YOK EDILIYORDU. Çalıştırıcının kendi izi ile Inno'nun <c>/LOG=</c> çıktısı AYNI
    /// DOSYAYDI; Inno dosyayı sıfırdan açtığı için çalıştırıcının o ana kadar yazdığı her satır
    /// siliniyordu. Ölçüldü: <c>apply-update.log</c>'un ilk satırı "Log opened." ve dosyada tek bir
    /// <c>[runner]</c> satırı yok. Kök nedenin aylarca bulunamamasının doğrudan sebebi buydu.
    /// </summary>
    [Fact]
    public void RunnerWritesItsOwnLog_NotTheFileInnoTruncates()
    {
        var script = Script();

        // Inno'ya verilen /LOG= yolu DEĞİŞMEMELİ: kullanıcıya gösterilen hata metinleri sunucuda
        // bu dosyaya bakmasını söylüyor ve kurulum arızasının gerçek sebebi orada yazıyor.
        Assert.Contains($"/LOG=`\"$logPath`\"", script, StringComparison.Ordinal);

        // Çalıştırıcının kendi satırları AYRI dosyaya gitmeli.
        Assert.Contains("Add-Content -LiteralPath $runnerLogPath", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Add-Content -LiteralPath $logPath", script, StringComparison.Ordinal);

        // İki yol gerçekten farklı olmalı (aynı dosyayı gösteren iki değişken hiçbir şeyi çözmez).
        Assert.NotEqual(InnoLogPath, RunnerLogPath);
        Assert.Contains(RunnerLogPath, script, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Başarısızlıkta kullanıcıya taşınan kuyruk INNO'nun günlüğünden okunmalı: "Cannot create file
    /// ... being used by another process" gibi gerçek sebep orada yazar, çalıştırıcının kendi
    /// izinde değil.
    /// </summary>
    [Fact]
    public void FailureTailIsReadFromTheInstallerLog()
    {
        Assert.Contains("Get-Content -LiteralPath $logPath -Tail", Script(), StringComparison.Ordinal);
    }

    /// <summary>
    /// K11a/K11b — SAHİPLİK. Kurulum, panelin servisini kendi bitişinden ~16 saniye ÖNCE başlatıyor
    /// (ölçüldü). O aralıkta yeni panel ayağa kalkıp "çalışan sürüm = hedef sürüm" ölçümünü yapıyor
    /// ve <c>succeeded</c> yazıyor; çalıştırıcı saniyeler sonra kendi daha az bilgili hükmünü
    /// ÜZERİNE yazıyordu ve panel bunu bir daha düzeltemiyordu (bekleyen kayıt silinmiş oluyor).
    /// </summary>
    [Fact]
    public void RunnerDoesNotOverwriteAVerdictThePanelAlreadyMade()
    {
        var script = Script();

        // Yazmadan ÖNCE mevcut dosya okunmalı ve panel sahipliği sorulmalı.
        Assert.Contains("$panelHukmu.isDecidedByPanel", script, StringComparison.Ordinal);

        // Sahiplik varsa yazım yapılmadan çıkılmalı.
        Assert.Contains("return", script, StringComparison.Ordinal);

        // TEK İSTİSNA: panelin bilemeyeceği kurulum çıkış kodunu tamamlamak. Panel hükmü verdiği
        // anda çıkış kodunu henüz kimse bilmiyor; bu yüzden yapısal olarak hep null kalıyordu.
        Assert.Contains("$panelHukmu.installerExitCode", script, StringComparison.Ordinal);
    }

    /// <summary>
    /// Hükmü tamamlarken panelin nesnesi OLDUĞU GİBİ yeniden serileştirilmemeli:
    /// <c>ConvertFrom-Json</c> <c>updatedAtUtc</c>'yi DateTime'a çevirir, <c>ConvertTo-Json</c> onu
    /// YEREL saat dilimi ekiyle geri yazar ve panel tarafında UTC olmayan bir damga oluşur
    /// (süre hesapları kayar). Alanlar tek tek kurulmalı, zaman damgası taze UTC olmalı.
    /// </summary>
    [Fact]
    public void EnrichedVerdictRebuildsFieldsInsteadOfReserializingTheParsedObject()
    {
        var script = Script();
        Assert.DoesNotContain("$panelHukmu | ConvertTo-Json", script, StringComparison.Ordinal);
        Assert.Contains("$tamamlanmis | ConvertTo-Json", script, StringComparison.Ordinal);
        Assert.Contains("isDecidedByPanel = $true", script, StringComparison.Ordinal);
    }
}
