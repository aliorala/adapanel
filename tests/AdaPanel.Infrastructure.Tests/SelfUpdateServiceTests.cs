using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Update;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.SelfUpdate;
using AdaPanel.Infrastructure.Support;
using Microsoft.Extensions.Options;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class SelfUpdateServiceTests
{
    [Fact]
    public async Task ApplyUpdateAsync_RequiresSha256BeforeDownload()
    {
        var dataRoot = CreateDataRoot();
        try
        {
            var service = CreateService(dataRoot, new RecordingLauncher());

            var exception = await Assert.ThrowsAsync<AppException>(() =>
                service.ApplyUpdateAsync(new ApplyUpdateRequest
                {
                    DownloadUrl = "https://example.test/AdaPanelSetup.exe"
                }));

            Assert.Equal("update.checksum_required", exception.MessageKey);
        }
        finally
        {
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    [Fact]
    public async Task InstallPreparedUpdateAsync_RevalidatesAndSchedulesPackage()
    {
        var dataRoot = CreateDataRoot();
        try
        {
            var updatesDirectory = Path.Combine(dataRoot, "updates");
            Directory.CreateDirectory(updatesDirectory);
            var packagePath = Path.Combine(updatesDirectory, "AdaPanelSetup.exe");
            await File.WriteAllBytesAsync(packagePath, [(byte)'M', (byte)'Z', 1, 2, 3, 4]);
            string hash;
            await using (var packageStream = File.OpenRead(packagePath))
            {
                hash = Convert.ToHexString(await SHA256.HashDataAsync(packageStream));
            }
            await File.WriteAllTextAsync(
                Path.Combine(updatesDirectory, "pending-update.json"),
                JsonSerializer.Serialize(new
                {
                    version = "0.2.1",
                    downloadUrl = "https://example.test/AdaPanelSetup.exe",
                    packagePath,
                    packageSha256 = hash,
                    stagedAtUtc = DateTime.UtcNow
                }));

            var launcher = new RecordingLauncher();
            var service = CreateService(dataRoot, launcher);
            var status = await service.InstallPreparedUpdateAsync();

            Assert.Equal(packagePath, launcher.PackagePath);
            Assert.Equal(hash, launcher.PackageSha256);
            Assert.True(status.IsInstallScheduled);
            Assert.NotNull(status.InstallScheduledAtUtc);
            Assert.Equal("update.install_scheduled", status.Notes);
        }
        finally
        {
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    [Theory]
    [InlineData("0.2.6", "0.2.7", true)]   // daha yeni release -> guncelleme var
    [InlineData("0.2.7", "0.2.7", false)]  // esit -> yok
    [InlineData("0.2.7", "0.2.6", false)]  // release ESKI (downgrade) -> uyari YOK
    [InlineData("0.2.2-preview", "0.2.4", true)]   // "-suffix" sayisal kiyastan once atilir
    [InlineData("0.2.4+abc123", "0.2.4", false)]   // "+hash" eki de atilir -> esit
    [InlineData("0.2.6", "", false)]               // bos tag -> yok
    [InlineData("abc", "def", true)]               // cozulemeyen -> eski esitsizlik davranisi
    public void IsNewerVersion_ComparesSemanticVersions(string current, string latest, bool expected)
    {
        Assert.Equal(expected, SelfUpdateService.IsNewerVersion(current, latest));
    }

    /// <summary>
    /// CANLI ARIZANIN BIREBIR TEKRARI: kurulum gunlugu "Installation process succeeded" derken
    /// durum dosyasi 'installing'de donmustu, cunku kurulum panelin servisini durdurunca hukmu
    /// yazacak calistirici da olduruluyor. Panel o donmus kayda bakip BASARILI guncellemeyi
    /// "tamamlanamadi" diye raporluyordu. Hukum artik calistiriciya birakilmaz: CALISAN surum
    /// hedef surume esitse kurulum OLMUSTUR.
    /// </summary>
    [Fact]
    public async Task CheckForUpdateAsync_DeclaresSuccess_WhenRunnerDiedButRunningVersionIsTheTarget()
    {
        var dataRoot = CreateDataRoot();
        try
        {
            // Hedef = panelin SU AN calisan surumu (kurulum gerceklesmis demektir).
            await YazBekleyenAsync(dataRoot, VersionInfo.Current, DateTime.UtcNow.AddMinutes(-2));
            await YazDenemeAsync(dataRoot, "installing", DateTime.UtcNow.AddMinutes(-2));

            var status = await CreateService(dataRoot, new RecordingLauncher()).CheckForUpdateAsync();

            Assert.NotNull(status.LastAttempt);
            Assert.Equal("succeeded", status.LastAttempt!.Phase);
            Assert.False(status.LastAttempt.IsFailed);
            Assert.True(status.LastAttempt.IsFinished);
            Assert.True(status.LastAttempt.IsDecidedByPanel);

            // Hukum KALICI olmali: bekleyen kayit temizlendigi icin ayni karar bir daha verilemez.
            var kalici = await OkuDenemeAsync(dataRoot);
            Assert.Equal("succeeded", kalici.GetProperty("phase").GetString());
        }
        finally
        {
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    /// <summary>
    /// Bu duzeltme GERCEK basarisizligi gizlememeli: surum degismediyse ve calistirici sebebini
    /// yazabildiyse, kullaniciya ESKISI GIBI o sebep gosterilir.
    /// </summary>
    [Fact]
    public async Task CheckForUpdateAsync_KeepsRunnerVerdict_WhenInstallReallyFailed()
    {
        var dataRoot = CreateDataRoot();
        try
        {
            await YazBekleyenAsync(dataRoot, "99.0.0", DateTime.UtcNow.AddMinutes(-2));
            await File.WriteAllTextAsync(
                Path.Combine(dataRoot, "updates", "last-attempt.json"),
                JsonSerializer.Serialize(new
                {
                    phase = "failed",
                    error = "Installer exited with code 5.",
                    installerExitCode = 5,
                    updatedAtUtc = DateTime.UtcNow.AddMinutes(-1)
                }));

            var status = await CreateService(dataRoot, new RecordingLauncher()).CheckForUpdateAsync();

            Assert.NotNull(status.LastAttempt);
            Assert.True(status.LastAttempt!.IsFailed);
            Assert.Equal("Installer exited with code 5.", status.LastAttempt.Error);
            Assert.Equal(5, status.LastAttempt.InstallerExitCode);
            Assert.False(status.LastAttempt.IsDecidedByPanel);
        }
        finally
        {
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    /// <summary>
    /// "Kuruluyor"da donmus bir kayit SONSUZA KADAR oyle kalamaz: sure dolunca deneme kapatilir,
    /// ama uydurma bir sebep yazilmaz -- kullaniciya "hukum alinamadi" denir.
    /// </summary>
    [Fact]
    public async Task CheckForUpdateAsync_ClosesStuckAttempt_WhenGraceElapsedAndVersionUnchanged()
    {
        var dataRoot = CreateDataRoot();
        try
        {
            var eski = DateTime.UtcNow - SelfUpdateService.AttemptVerdictGrace - TimeSpan.FromMinutes(5);
            await YazBekleyenAsync(dataRoot, "99.0.0", eski);
            await YazDenemeAsync(dataRoot, "installing", eski);

            var status = await CreateService(dataRoot, new RecordingLauncher()).CheckForUpdateAsync();

            Assert.NotNull(status.LastAttempt);
            Assert.True(status.LastAttempt!.IsFailed);
            Assert.Equal("update.attempt_no_verdict", status.LastAttempt.ErrorKey);
            Assert.True(status.LastAttempt.IsDecidedByPanel);
        }
        finally
        {
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    /// <summary>Suresi dolmamis bir kurulum HALA SURUYOR sayilir; erken "basarisiz" denmez.</summary>
    [Fact]
    public async Task CheckForUpdateAsync_KeepsWaiting_WhileInstallIsStillWithinGrace()
    {
        var dataRoot = CreateDataRoot();
        try
        {
            await YazBekleyenAsync(dataRoot, "99.0.0", DateTime.UtcNow.AddMinutes(-1));
            await YazDenemeAsync(dataRoot, "installing", DateTime.UtcNow.AddMinutes(-1));

            var status = await CreateService(dataRoot, new RecordingLauncher()).CheckForUpdateAsync();

            Assert.NotNull(status.LastAttempt);
            Assert.Equal("installing", status.LastAttempt!.Phase);
            Assert.False(status.LastAttempt.IsFinished);
        }
        finally
        {
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    /// <summary>
    /// Yeni kurulum planlanirken ONCEKI denemenin durum dosyasi silinir; yoksa gecmis bir
    /// basarisizlik yeni denemenin sonucu gibi raporlanir.
    /// </summary>
    [Fact]
    public async Task InstallPreparedUpdateAsync_ClearsPreviousAttemptState()
    {
        var dataRoot = CreateDataRoot();
        try
        {
            var updatesDirectory = Path.Combine(dataRoot, "updates");
            Directory.CreateDirectory(updatesDirectory);
            var packagePath = Path.Combine(updatesDirectory, "AdaPanelSetup.exe");
            await File.WriteAllBytesAsync(packagePath, [(byte)'M', (byte)'Z', 1, 2, 3, 4]);
            string hash;
            await using (var packageStream = File.OpenRead(packagePath))
            {
                hash = Convert.ToHexString(await SHA256.HashDataAsync(packageStream));
            }

            await File.WriteAllTextAsync(
                Path.Combine(updatesDirectory, "pending-update.json"),
                JsonSerializer.Serialize(new
                {
                    version = "99.0.0",
                    packagePath,
                    packageSha256 = hash,
                    stagedAtUtc = DateTime.UtcNow
                }));
            await YazDenemeAsync(dataRoot, "failed", DateTime.UtcNow.AddDays(-3));

            var status = await CreateService(dataRoot, new RecordingLauncher()).InstallPreparedUpdateAsync();

            Assert.False(File.Exists(Path.Combine(updatesDirectory, "last-attempt.json")));
            Assert.Null(status.LastAttempt);
        }
        finally
        {
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    [Fact]
    public void DecideAttemptVerdict_KeepsEverything_WhenNoInstallWasScheduled()
    {
        // Kurulum hic planlanmadiysa ortada hukum verilecek deneme yoktur: onceki denemeden kalan
        // sonuc oldugu gibi korunur (silinmis bekleyen kayittan sonraki normal durum).
        var verdict = SelfUpdateService.DecideAttemptVerdict(
            new UpdateAttemptDto { Phase = "installing", UpdatedAtUtc = DateTime.UtcNow.AddDays(-1) },
            pendingVersion: string.Empty,
            installScheduledAtUtc: null,
            currentVersion: "0.5.7",
            nowUtc: DateTime.UtcNow);

        Assert.Equal(SelfUpdateService.AttemptVerdict.Keep, verdict);
    }

    [Fact]
    public void DecideAttemptVerdict_DeclaresSuccess_WhenRunningVersionIsNewerThanTarget()
    {
        // Hedefe ULASILDI: esitlik sart degil, daha yeni bir surum calisiyorsa da kurulum olmustur.
        var verdict = SelfUpdateService.DecideAttemptVerdict(
            new UpdateAttemptDto { Phase = "installing", UpdatedAtUtc = DateTime.UtcNow },
            pendingVersion: "0.5.6",
            installScheduledAtUtc: DateTime.UtcNow.AddMinutes(-1),
            currentVersion: "0.5.7",
            nowUtc: DateTime.UtcNow);

        Assert.Equal(SelfUpdateService.AttemptVerdict.Succeeded, verdict);
    }

    [Fact]
    public void DecideAttemptVerdict_DeclaresSuccess_WhenRunnerNeverWroteAnything()
    {
        // Calistirici hic yazamadan olduyse (durum dosyasi YOK) bile olcum hukum verebilir.
        var verdict = SelfUpdateService.DecideAttemptVerdict(
            attempt: null,
            pendingVersion: "0.5.7",
            installScheduledAtUtc: DateTime.UtcNow.AddMinutes(-1),
            currentVersion: "0.5.7",
            nowUtc: DateTime.UtcNow);

        Assert.Equal(SelfUpdateService.AttemptVerdict.Succeeded, verdict);
    }

    [Fact]
    public void DecideAttemptVerdict_PrefersMeasurementOverRunnerFailure_WhenTargetVersionIsRunning()
    {
        // CELISKI: calistirici "basarisiz" demis ama panel HEDEF SURUMLE cevap veriyor. Olcum kazanir;
        // calistiricinin iddia edebilecegi iki sey (kurulum olmadi / servis kalkmadi) de curumustur.
        var verdict = SelfUpdateService.DecideAttemptVerdict(
            new UpdateAttemptDto
            {
                Phase = "failed",
                Error = "Kurulum tamamlandi ancak panel servisi baslatilamadi (durum: Stopped).",
                UpdatedAtUtc = DateTime.UtcNow.AddMinutes(-3)
            },
            pendingVersion: "0.5.7",
            installScheduledAtUtc: DateTime.UtcNow.AddMinutes(-5),
            currentVersion: "0.5.7",
            nowUtc: DateTime.UtcNow);

        Assert.Equal(SelfUpdateService.AttemptVerdict.Succeeded, verdict);
    }

    private static async Task YazBekleyenAsync(string dataRoot, string version, DateTime installScheduledAtUtc)
    {
        var updatesDirectory = Path.Combine(dataRoot, "updates");
        Directory.CreateDirectory(updatesDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(updatesDirectory, "pending-update.json"),
            JsonSerializer.Serialize(new
            {
                version,
                packagePath = Path.Combine(updatesDirectory, "AdaPanelSetup.exe"),
                packageSha256 = new string('A', 64),
                stagedAtUtc = installScheduledAtUtc.AddMinutes(-1),
                installScheduledAtUtc
            }));
    }

    // Calistiricinin (apply-update.ps1) yazdigi bicim birebir taklit edilir.
    private static async Task YazDenemeAsync(string dataRoot, string phase, DateTime updatedAtUtc)
    {
        var updatesDirectory = Path.Combine(dataRoot, "updates");
        Directory.CreateDirectory(updatesDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(updatesDirectory, "last-attempt.json"),
            JsonSerializer.Serialize(new
            {
                phase,
                error = string.Empty,
                installerExitCode = (int?)null,
                updatedAtUtc
            }));
    }

    private static async Task<JsonElement> OkuDenemeAsync(string dataRoot)
    {
        var json = await File.ReadAllTextAsync(Path.Combine(dataRoot, "updates", "last-attempt.json"));
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    private static SelfUpdateService CreateService(string dataRoot, IUpdateInstallerLauncher launcher)
    {
        var fileStore = new JsonFileStore(
            Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = dataRoot }));
        return new SelfUpdateService(fileStore, new StubHttpClientFactory(), launcher);
    }

    private static string CreateDataRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), $"adapanel-update-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class RecordingLauncher : IUpdateInstallerLauncher
    {
        public string PackagePath { get; private set; } = string.Empty;

        public string PackageSha256 { get; private set; } = string.Empty;

        public Task ScheduleAsync(
            string packagePath,
            string packageSha256,
            CancellationToken cancellationToken = default)
        {
            PackagePath = packagePath;
            PackageSha256 = packageSha256;
            return Task.CompletedTask;
        }
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new NotFoundHandler());
    }

    // Testi hermetik tutar: gerçek ağa (GitHub/örnek URL) ÇIKMAZ, her isteğe 404 döner. Böylece
    // CheckForUpdate uzak sürüm bulamaz → SHA boş kalır → ApplyUpdate indirmeden ÖNCE checksum_required
    // fırlatır. (Aksi halde canlı aliorala/adapanel release'i gerçek SHA döndürüp bu testi kırıyordu.)
    private sealed class NotFoundHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}
