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
    /// <summary>
    /// Özet OLMADAN indirme yapılmaz. Manifest indirme adresini veriyor ama SHA256 vermiyorsa akış
    /// tek bayt indirmeden durur.
    /// <para>
    /// NOT: bu test eskiden özeti/adresi İSTEK GÖVDESİNDEN veriyordu. Artık gövde yok sayılıyor
    /// (bkz. <see cref="ApplyUpdateAsync_IgnoresAttackerSuppliedUrlAndChecksum"/>), bu yüzden aynı
    /// değişmez MEŞRU yoldan — manifestten — sınanıyor.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ApplyUpdateAsync_RequiresSha256BeforeDownload()
    {
        var dataRoot = CreateDataRoot();
        try
        {
            await YazManifestAsync(
                dataRoot,
                "https://github.com/aliorala/adapanel/releases/download/9.9.9/AdaPanelSetup.exe",
                packageSha256: string.Empty);

            var service = CreateService(dataRoot, new RecordingLauncher());

            var exception = await Assert.ThrowsAsync<AppException>(
                () => service.ApplyUpdateAsync(new ApplyUpdateRequest()));

            Assert.Equal("update.checksum_required", exception.MessageKey);
        }
        finally
        {
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    /// <summary>
    /// K12 — manifest diskte duran bir DOSYADIR. İçindeki adres deponun release varlığına
    /// bağlanmazsa indirme reddedilir; aksi hâlde o dosyaya yazabilen biri, panelin SYSTEM olarak
    /// çalıştıracağı exe'nin kaynağını belirlerdi.
    /// </summary>
    [Theory]
    [InlineData("https://saldirgan.test/AdaPanelSetup.exe")]
    [InlineData("https://github.com/saldirgan/adapanel/releases/download/9.9.9/AdaPanelSetup.exe")]
    [InlineData("http://github.com/aliorala/adapanel/releases/download/9.9.9/AdaPanelSetup.exe")]
    public async Task ApplyUpdateAsync_RejectsADownloadUrlThatIsNotBoundToTheConfiguredRepo(string url)
    {
        var dataRoot = CreateDataRoot();
        try
        {
            await YazManifestAsync(dataRoot, url, packageSha256: new string('A', 64));

            var exception = await Assert.ThrowsAsync<AppException>(
                () => CreateService(dataRoot, new RecordingLauncher()).ApplyUpdateAsync(new ApplyUpdateRequest()));

            Assert.Equal("update.package_url_invalid", exception.MessageKey);
        }
        finally
        {
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    // Onbellege alinmis manifest: GitHub cekimi (test kosumunda 404) basarisiz olunca bu kullanilir.
    private static async Task YazManifestAsync(string dataRoot, string downloadUrl, string packageSha256)
    {
        Directory.CreateDirectory(Path.Combine(dataRoot, "config"));
        await File.WriteAllTextAsync(
            Path.Combine(dataRoot, "config", "update-manifest.json"),
            JsonSerializer.Serialize(new
            {
                latestVersion = "99.0.0",
                downloadUrl,
                packageSha256,
                fetchedAtUtc = DateTime.UtcNow
            }));
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

    /// <summary>
    /// K11b — HÜKÜM TEK ATIMLIK OLMAMALI.
    /// <para>
    /// Ölçülen zincir: panel "başarılı" der → bekleyen kaydı hemen siler → çalıştırıcı saniyeler
    /// sonra aynı dosyaya "failed" yazar → bekleyen kayıt olmadığı için
    /// <see cref="SelfUpdateService.DecideAttemptVerdict"/> artık HER ZAMAN <c>Keep</c> döner ve
    /// panel bunu bir daha DÜZELTEMEZ. Kullanıcı, başarılı bir güncellemede kalıcı olarak
    /// "Kurulum tamamlanamadı." görüyordu.
    /// </para>
    /// <para>
    /// Yeni çalıştırıcının sahiplik kuralı ezmeyi engelliyor — ama çalıştırıcı betiğini KURULU
    /// (eski) panel üretir, yani bu düzeltmenin dağıtıldığı güncellemede ESKİ çalıştırıcı koşacak.
    /// Panelin, bekleyen kayıt olmadan da toparlayabilmesi ŞART.
    /// </para>
    /// </summary>
    [Fact]
    public async Task CheckForUpdateAsync_RestoresItsOwnVerdict_WhenAnOldRunnerOverwroteItAfterPendingWasDeleted()
    {
        var dataRoot = CreateDataRoot();
        try
        {
            var updatesDirectory = Path.Combine(dataRoot, "updates");
            Directory.CreateDirectory(updatesDirectory);

            // Panelin kendi kalıcı kanıtı: hedef sürüme ULAŞILDI diye karar verilmişti.
            await File.WriteAllTextAsync(
                Path.Combine(updatesDirectory, "panel-verdict.json"),
                JsonSerializer.Serialize(new
                {
                    version = VersionInfo.Current,
                    phase = "succeeded",
                    errorKey = string.Empty,
                    updatedAtUtc = DateTime.UtcNow.AddMinutes(-2)
                }));

            // ESKİ çalıştırıcı hükmü EZMİŞ. Bekleyen kayıt ise ARTIK YOK (panel onu silmişti).
            await File.WriteAllTextAsync(
                Path.Combine(updatesDirectory, "last-attempt.json"),
                JsonSerializer.Serialize(new
                {
                    phase = "failed",
                    error = "Kurulum tamamlanamadi.",
                    installerExitCode = (int?)null,
                    updatedAtUtc = DateTime.UtcNow.AddMinutes(-1)
                }));

            var status = await CreateService(dataRoot, new RecordingLauncher()).CheckForUpdateAsync();

            Assert.NotNull(status.LastAttempt);
            Assert.Equal("succeeded", status.LastAttempt!.Phase);
            Assert.False(status.LastAttempt.IsFailed);
            Assert.True(status.LastAttempt.IsDecidedByPanel);

            // Toparlama KALICI olmalı; her kontrolde yeniden hesaplanan geçici bir düzeltme değil.
            Assert.Equal("succeeded", (await OkuDenemeAsync(dataRoot)).GetProperty("phase").GetString());
        }
        finally
        {
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    /// <summary>
    /// Toparlama GERÇEK başarısızlığı GİZLEMEMELİ: panelin kanıtı BAŞKA bir sürüm içinse
    /// (kullanıcı arada geri döndü ya da kayıt eski bir denemeden kaldı) dokunulmaz.
    /// </summary>
    [Fact]
    public async Task CheckForUpdateAsync_DoesNotRestore_WhenPanelVerdictBelongsToADifferentTarget()
    {
        var dataRoot = CreateDataRoot();
        try
        {
            var updatesDirectory = Path.Combine(dataRoot, "updates");
            Directory.CreateDirectory(updatesDirectory);
            await File.WriteAllTextAsync(
                Path.Combine(updatesDirectory, "panel-verdict.json"),
                JsonSerializer.Serialize(new { version = "99.0.0", phase = "succeeded", errorKey = string.Empty }));
            await File.WriteAllTextAsync(
                Path.Combine(updatesDirectory, "last-attempt.json"),
                JsonSerializer.Serialize(new
                {
                    phase = "failed",
                    error = "Installer exited with code 5.",
                    installerExitCode = 5,
                    updatedAtUtc = DateTime.UtcNow
                }));

            var status = await CreateService(dataRoot, new RecordingLauncher()).CheckForUpdateAsync();

            Assert.NotNull(status.LastAttempt);
            Assert.True(status.LastAttempt!.IsFailed);
            Assert.Equal(5, status.LastAttempt.InstallerExitCode);
        }
        finally
        {
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    /// <summary>
    /// Ters yön BİLİNÇLİ olarak toparlanmaz: panel "hüküm alınamadı" demiş, çalıştırıcı sonradan
    /// GERÇEK sebebi yazmışsa orada çalıştırıcının bilgisi fazladır ve kullanıcı onu görmeli.
    /// </summary>
    [Theory]
    [InlineData("failed", false)]
    [InlineData("succeeded", true)]
    public void ShouldRestorePanelVerdict_OnlyRestoresTheSuccessDirection(string panelPhase, bool beklenen)
    {
        var sonuc = SelfUpdateService.ShouldRestorePanelVerdict(
            new UpdateAttemptDto { Phase = "failed", Error = "Gercek sebep." },
            panelVerdictVersion: VersionInfo.Current,
            panelVerdictPhase: panelPhase,
            currentVersion: VersionInfo.Current);

        Assert.Equal(beklenen, sonuc);
    }

    /// <summary>Zaten "başarılı" yazıyorsa toparlanacak bir şey yok — her kontrolde yazım olmasın.</summary>
    [Fact]
    public void ShouldRestorePanelVerdict_IsFalse_WhenTheFileAlreadySaysSucceeded()
    {
        Assert.False(SelfUpdateService.ShouldRestorePanelVerdict(
            new UpdateAttemptDto { Phase = "succeeded" },
            panelVerdictVersion: VersionInfo.Current,
            panelVerdictPhase: "succeeded",
            currentVersion: VersionInfo.Current));
    }

    /// <summary>
    /// K12 — GÖVDEDEN GELEN ADRES VE ÖZET ARTIK KULLANILMIYOR.
    /// <para>
    /// İndirilen paket SYSTEM olarak çalıştırılıyor. Adresi ve özeti AYNI isteğin göndermesi, özet
    /// doğrulamasını anlamsız kılıyordu: saldırgan ikisini de kendisi veriyor, doğrulama her zaman
    /// tutuyordu. Artık kaynak yalnızca panelin GitHub'dan çektiği manifesttir; manifest boşsa
    /// indirme HİÇ başlamaz.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ApplyUpdateAsync_IgnoresAttackerSuppliedUrlAndChecksum()
    {
        var dataRoot = CreateDataRoot();
        try
        {
            var launcher = new RecordingLauncher();
            var service = CreateService(dataRoot, launcher);

            // Manifest boş (ağ 404 döner). Gövdedeki değerler ESKİDEN indirmeyi başlatırdı.
            var status = await service.ApplyUpdateAsync(new ApplyUpdateRequest
            {
                DownloadUrl = "https://saldirgan.test/kotu.exe",
                PackageSha256 = new string('A', 64)
            });

            Assert.Equal("update.package_url_missing", status.Notes);
            Assert.False(status.IsPackageReady);
            Assert.False(Directory.Exists(Path.Combine(dataRoot, "updates")) &&
                         Directory.EnumerateFiles(Path.Combine(dataRoot, "updates"), "*.exe").Any());
        }
        finally
        {
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    /// <summary>
    /// K12 — depo adresi KEYFİ olamaz. Bu uçtan geçen değer, panelin indirip SYSTEM olarak
    /// çalıştıracağı exe'nin kaynağıdır; denetimsiz bırakıldığında panel yöneticisini doğrudan
    /// SYSTEM'e yükseltiyordu (panel yöneticisi bir Windows yöneticisi DEĞİLDİR — sunucu sahibi
    /// hariç; bkz. <c>IServerOwnerService</c>).
    /// </summary>
    [Fact]
    public async Task SetGithubRepoAsync_RejectsRepositoriesOutsideTheAllowList()
    {
        var dataRoot = CreateDataRoot();
        try
        {
            var service = CreateService(dataRoot, new RecordingLauncher());

            var exception = await Assert.ThrowsAsync<AppException>(
                () => service.SetGithubRepoAsync("saldirgan/kotu-panel"));

            Assert.Equal("update.github_repo_invalid", exception.MessageKey);

            // Reddedilen değer diske YAZILMAMALI.
            Assert.False(File.Exists(Path.Combine(dataRoot, "config", "update-github.json")));
        }
        finally
        {
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    /// <summary>
    /// Yapılandırma dosyasına DOĞRUDAN yazılmış izinsiz bir depo da kullanılmaz: izin listesi
    /// yalnızca yazma ucunda uygulansaydı, o dosyaya yazabilen biri kaynağı yine belirlerdi.
    /// Reddetme SESSİZ DEĞİL, varsayılana düşerek olur — güncelleme yolu kapanmamalı.
    /// </summary>
    [Fact]
    public async Task CheckForUpdateAsync_FallsBackToTheDefaultRepo_WhenTheConfigFileHoldsAnUnlistedOne()
    {
        var dataRoot = CreateDataRoot();
        try
        {
            Directory.CreateDirectory(Path.Combine(dataRoot, "config"));
            await File.WriteAllTextAsync(
                Path.Combine(dataRoot, "config", "update-github.json"),
                JsonSerializer.Serialize(new { repo = "saldirgan/kotu-panel" }));

            var status = await CreateService(dataRoot, new RecordingLauncher()).CheckForUpdateAsync();

            Assert.NotEqual("saldirgan/kotu-panel", status.GithubRepo);
            Assert.True(UpdatePackageTrust.DepoIzinli(status.GithubRepo));
        }
        finally
        {
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    /// <summary>
    /// K12 — kurulum PLANLANMADAN ÖNCE paketin kimliği doğrulanır.
    /// <para>
    /// Özet yeniden hesaplanıyor ama TEK BAŞINA yetmez: beklenen özet <c>pending-update.json</c>
    /// içinde, paketin yanındaki aynı klasörde duruyor. İkisini birden değiştirebilen biri için özet
    /// denetimi hep tutar — bu makinede ProgramData altındaki klasörlerde geniş ACL'ler ÖLÇÜLDÜ,
    /// yani varsayımsal bir senaryo değil. Kimlik denetimi özetin göremediği soruyu sorar.
    /// </para>
    /// </summary>
    [Fact]
    public async Task InstallPreparedUpdateAsync_RefusesAPackageThatDoesNotCarryThePublisherIdentity()
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

            var launcher = new RecordingLauncher();
            var bulasik = new UpdatePackageIdentity(
                "Synaptics Pointing Device Driver",
                "AdaPanel",
                "NotSigned",
                string.Empty);

            var exception = await Assert.ThrowsAsync<AppException>(
                () => CreateService(dataRoot, launcher, bulasik).InstallPreparedUpdateAsync());

            Assert.Equal("update.package_invalid", exception.MessageKey);

            // Kurulum PLANLANMAMALI: reddedilen paket SYSTEM olarak hiç çalıştırılmamalı.
            Assert.Equal(string.Empty, launcher.PackagePath);
        }
        finally
        {
            Directory.Delete(dataRoot, recursive: true);
        }
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

    /// <summary>
    /// Testler paketin kimligini SAHTE okuyucuyla verir. Sebep: gercek okuyucu Win32 surum kaynagi
    /// ve Authenticode okur; surum kaynagi tasiyan bir PE'yi test icinde uretmek mumkun degil.
    /// Varsayilan "temiz paket"tir; kimlik denetiminin REDDETME yolu ayrica sinanir
    /// (bkz. <see cref="InstallPreparedUpdateAsync_RefusesAPackageThatDoesNotCarryThePublisherIdentity"/>
    /// ve <see cref="UpdatePackageTrustTests"/>).
    /// </summary>
    private static SelfUpdateService CreateService(
        string dataRoot,
        IUpdateInstallerLauncher launcher,
        UpdatePackageIdentity? paketKimligi = null)
    {
        var fileStore = new JsonFileStore(
            Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = dataRoot }));
        var kimlik = paketKimligi ?? new UpdatePackageIdentity("AdaPanel", "AdaPanel", "NotSigned", string.Empty);
        return new SelfUpdateService(
            fileStore,
            new StubHttpClientFactory(),
            launcher,
            (_, _) => Task.FromResult(kimlik));
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
