using System.Text.Json;
using AdaPanel.Infrastructure.SelfUpdate;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// K11 — HÜKÜM SAHİPLİĞİ. Bu testler betiğin bir kopyasını değil, <c>WindowsUpdateInstallerLauncher</c>
/// tarafından ÜRETİLEN gerçek PowerShell'i çalıştırır.
/// </summary>
/// <remarks>
/// <para>
/// ÖLÇÜLEN ARIZA: kurulum, panelin servisini kendi bitişinden ~16 saniye ÖNCE başlatıyor
/// (<c>last-attempt.json</c> 21:49:07Z, <c>apply-update.log</c> "Log closed" 21:49:23Z). O aralıkta
/// yeni panel ayağa kalkıp "çalışan sürüm = hedef sürüm" ölçümüyle <c>succeeded</c> yazıyor;
/// çalıştırıcı saniyeler sonra aynı dosyaya kendi (daha az bilgili) hükmünü YAZIYORDU.
/// Panel bunu bir daha düzeltemiyordu, çünkü bekleyen kayıt o sırada zaten silinmiş oluyor —
/// kullanıcı BAŞARILI bir güncellemede kalıcı olarak "Kurulum tamamlanamadı." görüyordu.
/// </para>
/// <para>
/// Metin araması yeterli değil: kural PowerShell'in kendi <c>ConvertFrom-Json</c>/<c>Test-Path</c>
/// davranışına dayanıyor. Bu yüzden üretilen betiğin fonksiyon tanımları GERÇEKTEN koşturulur.
/// Kurulum akışı (servis durdurma, zamanlanmış görev, Start-Process) bilinçli olarak KESİLİR:
/// yalnızca tanımlara kadar olan kısım çalıştırılır.
/// </para>
/// </remarks>
public sealed class UpdateVerdictOwnershipTests : IDisposable
{
    private readonly string calisma = Path.Combine(Path.GetTempPath(), $"ada-verdict-{Guid.NewGuid():N}");

    public UpdateVerdictOwnershipTests() => Directory.CreateDirectory(calisma);

    public void Dispose()
    {
        try { Directory.Delete(calisma, recursive: true); } catch { /* en iyi caba */ }
    }

    private string StatePath => Path.Combine(calisma, "last-attempt.json");

    private string RunnerLogPath => Path.Combine(calisma, "update-runner.log");

    /// <summary>
    /// Üretilen betiğin YALNIZCA tanım kısmını alır (kurulum akışı başlamadan önce keser) ve
    /// sonuna testin çağrılarını ekler.
    /// </summary>
    private string TanimlariHazirla(string ekCagrilar)
    {
        var betik = WindowsUpdateInstallerLauncher.BuildLauncherScript(
            Path.Combine(calisma, "AdaPanelSetup.exe"),
            new string('A', 64),
            Path.Combine(calisma, "apply-update.log"),
            RunnerLogPath);

        const string kesmeNoktasi = "Write-UpdateState 'starting'";
        var indeks = betik.IndexOf(kesmeNoktasi, StringComparison.Ordinal);
        Assert.True(indeks > 0, "Betiğin yapısı değişmiş: 'starting' durum yazımı bulunamadı.");

        return betik[..indeks] + ekCagrilar;
    }

    private void PanelHukmuYaz(string phase, int? installerExitCode, bool isDecidedByPanel = true)
        => File.WriteAllText(
            StatePath,
            JsonSerializer.Serialize(new
            {
                phase,
                error = string.Empty,
                errorKey = string.Empty,
                installerExitCode,
                isDecidedByPanel,
                updatedAtUtc = DateTime.UtcNow
            }));

    private JsonElement DurumuOku()
        => JsonDocument.Parse(File.ReadAllText(StatePath)).RootElement.Clone();

    /// <summary>Panel hüküm vermemişse çalıştırıcı eskisi gibi serbestçe yazar.</summary>
    [Fact]
    public void Calistirici_hukum_yoksa_serbestce_yazar()
    {
        var sonuc = ScriptTestHost.Run(TanimlariHazirla("Write-UpdateState 'installing' '' $null\r\n"));

        Assert.True(sonuc.ExitCode == 0, sonuc.Tumu);
        Assert.Equal("installing", DurumuOku().GetProperty("phase").GetString());
    }

    /// <summary>
    /// ARIZANIN BİREBİR TEKRARI: panel "başarılı" demiş, çalıştırıcı sonradan "başarısız" yazmak
    /// istiyor. Panelin hükmü DEĞİŞMEMELİ.
    /// </summary>
    [Fact]
    public void Calistirici_panelin_basarili_hukmunu_EZEMEZ()
    {
        PanelHukmuYaz("succeeded", installerExitCode: null);

        var sonuc = ScriptTestHost.Run(TanimlariHazirla(
            "Write-UpdateState 'failed' 'Kurulum tamamlanamadi.' $null\r\n"));

        Assert.True(sonuc.ExitCode == 0, sonuc.Tumu);
        var durum = DurumuOku();
        Assert.Equal("succeeded", durum.GetProperty("phase").GetString());
        Assert.True(durum.GetProperty("isDecidedByPanel").GetBoolean());

        // Reddedilen yazım SESSİZ geçmemeli: teşhis için çalıştırıcının kendi günlüğüne düşmeli.
        Assert.Contains("KORUNDU", File.ReadAllText(RunnerLogPath), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// K11a — TEK İSTİSNA. Panel hükmü verdiği anda kurulum çıkış kodunu HENÜZ kimse bilmiyor
    /// (bu yüzden <c>installerExitCode</c> yapısal olarak hep null kalıyordu). Onu yalnızca
    /// çalıştırıcı bilir: faz/sebep değişmeden, boş olan çıkış kodu doldurulmalı.
    /// </summary>
    [Fact]
    public void Calistirici_eksik_kurulum_cikis_kodunu_tamamlayabilir()
    {
        PanelHukmuYaz("succeeded", installerExitCode: null);

        var sonuc = ScriptTestHost.Run(TanimlariHazirla("Write-UpdateState 'installer-finished' '' 0\r\n"));

        Assert.True(sonuc.ExitCode == 0, sonuc.Tumu);
        var durum = DurumuOku();
        Assert.Equal("succeeded", durum.GetProperty("phase").GetString());
        Assert.Equal(0, durum.GetProperty("installerExitCode").GetInt32());
        Assert.True(durum.GetProperty("isDecidedByPanel").GetBoolean());
    }

    /// <summary>
    /// Tamamlama BİR KEREDİR: çıkış kodu zaten yazılıysa üstüne yazılmaz (ilk ölçüm en yakın olandır).
    /// </summary>
    [Fact]
    public void Yazilmis_cikis_kodu_ezilmez()
    {
        PanelHukmuYaz("succeeded", installerExitCode: 5);

        var sonuc = ScriptTestHost.Run(TanimlariHazirla("Write-UpdateState 'succeeded' '' 0\r\n"));

        Assert.True(sonuc.ExitCode == 0, sonuc.Tumu);
        Assert.Equal(5, DurumuOku().GetProperty("installerExitCode").GetInt32());
    }

    /// <summary>
    /// Sahiplik bayrağı YOKSA (çalıştırıcının kendi önceki yazımı) kural devreye girmez; aksi hâlde
    /// çalıştırıcı kendi ara durumlarını bile güncelleyemezdi.
    /// </summary>
    [Fact]
    public void Calistirici_kendi_yazdigi_kaydi_guncelleyebilir()
    {
        PanelHukmuYaz("installing", installerExitCode: null, isDecidedByPanel: false);

        var sonuc = ScriptTestHost.Run(TanimlariHazirla("Write-UpdateState 'failed' 'Gercek sebep.' 5\r\n"));

        Assert.True(sonuc.ExitCode == 0, sonuc.Tumu);
        var durum = DurumuOku();
        Assert.Equal("failed", durum.GetProperty("phase").GetString());
        Assert.Equal("Gercek sebep.", durum.GetProperty("error").GetString());
        Assert.Equal(5, durum.GetProperty("installerExitCode").GetInt32());
    }

    /// <summary>
    /// Bozuk/yarım bir durum dosyası çalıştırıcıyı KİLİTLEMEMELİ: okunamıyorsa sahiplik iddiası da
    /// yoktur, akış eskisi gibi devam eder.
    /// </summary>
    [Fact]
    public void Bozuk_durum_dosyasi_akisi_durdurmaz()
    {
        File.WriteAllText(StatePath, "{ bu gecerli json degil");

        var sonuc = ScriptTestHost.Run(TanimlariHazirla("Write-UpdateState 'installing' '' $null\r\n"));

        Assert.True(sonuc.ExitCode == 0, sonuc.Tumu);
        Assert.Equal("installing", DurumuOku().GetProperty("phase").GetString());
    }

    /// <summary>
    /// K11c — çalıştırıcının izi Inno'nun sıfırdan açtığı dosyaya DEĞİL, kendi dosyasına gitmeli.
    /// Ölçülerek doğrulanır: betik koştuktan sonra <c>update-runner.log</c> oluşmuş olmalı,
    /// Inno'nun günlüğüne ise çalıştırıcı hiç dokunmamış olmalı.
    /// </summary>
    [Fact]
    public void Calistirici_izi_Inno_gunlugune_yazilmaz()
    {
        var innoLog = Path.Combine(calisma, "apply-update.log");
        var sonuc = ScriptTestHost.Run(TanimlariHazirla("Write-UpdateLog 'deneme satiri'\r\n"));

        Assert.True(sonuc.ExitCode == 0, sonuc.Tumu);
        Assert.True(File.Exists(RunnerLogPath), "Çalıştırıcı kendi günlüğünü oluşturmadı.");
        Assert.Contains("deneme satiri", File.ReadAllText(RunnerLogPath), StringComparison.Ordinal);
        Assert.False(File.Exists(innoLog), "Çalıştırıcı, Inno'nun sıfırdan açtığı dosyaya yazmış.");
    }
}
