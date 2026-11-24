using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Antivirus;
using AdaPanel.Core.Enums;
using AdaPanel.Core.Interfaces;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AdaPanel.Infrastructure.Antivirus;

/// <summary>
/// ClamAV tabanlı dosya antivirüsü. Kurulum yazılım kataloğu ("clamav" bileşeni) üzerinden yapılır;
/// bu servis kurulu dizindeki clamscan/freshclam çalıştırılabilirlerini bulur ve çalıştırır.
/// Daemon (clamd) BİLEREK kullanılmaz: tarama isteğe bağlıdır, kalıcı RAM maliyeti yoktur.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AntivirusService(
    AdaPanelDbContext dbContext,
    IIisSiteService iisSiteService,
    JsonFileStore fileStore,
    ILogger<AntivirusService> logger) : IAntivirusService
{
    private const string ComponentId = "clamav";
    private const string StatePath = "config/antivirus.json";

    /// <summary>Süren taramanın kaydı (bkz. <see cref="AntivirusRunningScanState"/>).</summary>
    private const string RunningStatePath = "config/antivirus-running.json";

    // clamscan bulgu satırı: "<dosya yolu>: <Imza.Adi> FOUND"
    private static readonly Regex FoundLine = new(@"^(?<path>.+?): (?<sig>\S+) FOUND\s*$", RegexOptions.Compiled);
    private static readonly Regex ScannedFilesLine = new(@"^Scanned files:\s*(?<count>\d+)", RegexOptions.Compiled);

    public async Task<AntivirusStatusDto> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var component = await FindComponentAsync(cancellationToken);
        var lastScan = await fileStore.ReadAsync<AntivirusScanResultDto?>(StatePath, null, cancellationToken);

        // SÜREN TARAMA burada okunur. Bu uç, sayfa yenilendikten sonra "tarama sürüyor mu"
        // sorusunun sorulabileceği TEK yerdir; arayüz sekmeyi açar açmaz buraya bakar.
        var (running, reconciledLastScan) = await ResolveRunningScanAsync(lastScan, cancellationToken);
        lastScan = reconciledLastScan;

        if (component is null)
        {
            return new AntivirusStatusDto { IsInstalled = false, LastScan = lastScan, RunningScan = running };
        }

        var databaseDirectory = GetDatabaseDirectory(component.InstallDir);
        var newestDefinition = GetNewestDefinitionUtc(databaseDirectory);

        return new AntivirusStatusDto
        {
            IsInstalled = true,
            Version = component.Version,
            DatabaseReady = newestDefinition is not null,
            DatabaseUpdatedAtUtc = newestDefinition,
            LastScan = lastScan,
            RunningScan = running
        };
    }

    public async Task<AntivirusStatusDto> UpdateDefinitionsAsync(CancellationToken cancellationToken = default)
    {
        var component = await RequireComponentAsync(cancellationToken);
        var freshclam = FindExecutable(component.InstallDir, "freshclam.exe")
            ?? throw new AppException("antivirus.not_installed", 409, 409, "freshclam.exe not found under install dir.");

        var databaseDirectory = GetDatabaseDirectory(component.InstallDir);
        Directory.CreateDirectory(databaseDirectory);
        var configPath = await EnsureFreshclamConfigAsync(component.InstallDir, databaseDirectory, cancellationToken);

        // İlk indirme ~250 MB (main+daily+bytecode) → uzun zaman aşımı. Sonraki çalışmalar artımlıdır (hızlı).
        var result = await ProcessRunner.RunAsync(
            freshclam,
            $"--config-file=\"{configPath}\" --datadir=\"{databaseDirectory}\"",
            workingDirectory: Path.GetDirectoryName(freshclam),
            cancellationToken: cancellationToken,
            timeout: TimeSpan.FromMinutes(30));

        // freshclam çıkışı: 0 = güncellendi/zaten güncel; diğerleri gerçek hata (ağ/mirror/izin).
        if (result.ExitCode != 0)
        {
            throw new AppException(
                "antivirus.update_failed",
                502,
                502,
                Truncate(result.StandardError.Length > 0 ? result.StandardError : result.StandardOutput));
        }

        logger.LogInformation("ClamAV tanımları güncellendi ({Directory}).", databaseDirectory);
        return await GetStatusAsync(cancellationToken);
    }

    public async Task<AntivirusScanResultDto> ScanAsync(AntivirusScanRequest request, CancellationToken cancellationToken = default)
    {
        var component = await RequireComponentAsync(cancellationToken);
        var clamscan = FindExecutable(component.InstallDir, "clamscan.exe")
            ?? throw new AppException("antivirus.not_installed", 409, 409, "clamscan.exe not found under install dir.");

        var databaseDirectory = GetDatabaseDirectory(component.InstallDir);
        if (GetNewestDefinitionUtc(databaseDirectory) is null)
        {
            throw new AppException("antivirus.definitions_missing", 409, 409);
        }

        var (target, path) = await ResolveTargetAsync(request, cancellationToken);

        // AYNI ANDA İKİ TARAMA YOK. Sunucudaki "sürüyor" kaydı tek yuvalıdır; ikinci bir tarama
        // başlatmak birinci taramanın kaydını ezerdi ve bittiğinde ikisinden hangisinin sonucunun
        // yazıldığı belirsiz olurdu. Üstelik aynı ağacı iki clamscan ile taramak sunucuyu boşuna
        // yorar. Kullanıcı 409 ile açıkça uyarılır.
        var startedAt = DateTime.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        await YuvayiAyirtAsync(target, startedAt, cancellationToken);

        try
        {
            // --infected: yalnız bulgular yazılır (dev sitelerde çıktı şişmez); özet (Scanned files) korunur.
            var result = await ProcessRunner.RunAsync(
                clamscan,
                $"--database=\"{databaseDirectory}\" --recursive --infected \"{path}\"",
                workingDirectory: Path.GetDirectoryName(clamscan),
                cancellationToken: cancellationToken,
                timeout: TimeSpan.FromHours(2));
            stopwatch.Stop();
            var scan = ParseScanResult(result, target, startedAt, stopwatch.Elapsed);
            // Persist the result before releasing the slot. Even if persistence is
            // canceled or fails, the finally block releases the completed scan's slot.
            await fileStore.WriteAsync(StatePath, scan, cancellationToken);
            return scan;
        }
        finally
        {
            // Parsing and result persistence can also fail after the process exits.
            // They must not leave this process owning a permanently busy slot.
            await ClearRunningScanAsync(startedAt, CancellationToken.None);
        }
    }

    internal static AntivirusScanResultDto ParseScanResult(
        CommandResult result, string target, DateTime startedAt, TimeSpan duration)
    {
        // clamscan çıkış kodları: 0 = temiz, 1 = virüs bulundu, 2+ = gerçek hata.
        AntivirusScanResultDto scan;
        if (result.ExitCode is 0 or 1)
        {
            var findings = new List<AntivirusFindingDto>();
            var scannedFiles = 0;
            foreach (var line in result.StandardOutput.Split('\n'))
            {
                var trimmed = line.TrimEnd('\r');
                var found = FoundLine.Match(trimmed);
                if (found.Success)
                {
                    findings.Add(new AntivirusFindingDto
                    {
                        Path = found.Groups["path"].Value,
                        Signature = found.Groups["sig"].Value
                    });
                    continue;
                }

                var scanned = ScannedFilesLine.Match(trimmed);
                if (scanned.Success)
                {
                    _ = int.TryParse(scanned.Groups["count"].Value, out scannedFiles);
                }
            }

            scan = new AntivirusScanResultDto
            {
                Target = target,
                StartedAtUtc = startedAt,
                DurationSeconds = Math.Round(duration.TotalSeconds, 1),
                ScannedFiles = scannedFiles,
                InfectedCount = findings.Count,
                Infected = findings,
                Status = findings.Count > 0 ? "infected" : result.ExitCode == 0 ? "clean" : "failed",
                Detail = result.ExitCode == 1 && findings.Count == 0
                    ? "ClamAV reported an infection, but its finding details could not be read. Review the scanner output and repeat the scan."
                    : string.Empty
            };
        }
        else
        {
            scan = new AntivirusScanResultDto
            {
                Target = target,
                StartedAtUtc = startedAt,
                DurationSeconds = Math.Round(duration.TotalSeconds, 1),
                Status = "failed",
                Detail = Truncate(result.StandardError.Length > 0 ? result.StandardError : result.StandardOutput)
            };
        }

        return scan;
    }

    /// <summary>
    /// Diskteki "sürüyor" kaydını GERÇEĞE göre yorumlar ve gerekiyorsa son tarama kaydını onarır.
    /// <para>
    /// Üç durum vardır: (1) kayıt yok → tarama yok. (2) kayıt VAR ve sahibi BU süreç → tarama
    /// gerçekten sürüyor. (3) kayıt var ama sahibi başka bir süreç → panel tarama sürerken yeniden
    /// başlamış; o tarama artık koşmuyor ve sonucu HİÇ YAZILAMAYACAK. Üçüncü durumda kayıt
    /// temizlenir ve son tarama olarak <c>interrupted</c> yazılır: kullanıcı 2 saatlik taramasının
    /// neden bittiğini göremediğini öğrenmeli, ekran sessizce boş kalmamalı.
    /// </para>
    /// </summary>
    private async Task<(AntivirusRunningScanDto? Running, AntivirusScanResultDto? LastScan)> ResolveRunningScanAsync(
        AntivirusScanResultDto? lastScan,
        CancellationToken cancellationToken)
    {
        var state = await fileStore.ReadAsync<AntivirusRunningScanState?>(RunningStatePath, null, cancellationToken);
        if (state is null || state.StartedAtUtc == default)
        {
            return (null, lastScan);
        }

        if (IsOwnedByCurrentProcess(state))
        {
            return (new AntivirusRunningScanDto { Target = state.Target, StartedAtUtc = state.StartedAtUtc }, lastScan);
        }

        logger.LogWarning(
            "Yarıda kalan antivirüs taraması bulundu ({Target}, başlangıç {StartedAt:O}): panel süreci tarama sürerken yeniden başlamış.",
            state.Target,
            state.StartedAtUtc);

        await ClearRunningScanAsync(startedAtUtc: null, cancellationToken);

        // Daha YENİ bir sonuç varsa ona dokunulmaz: yarıda kalan tarama eski bir kayıttır ve
        // sonradan tamamlanmış gerçek bir taramanın üstüne yazmak veri kaybı olurdu.
        if (lastScan is not null && lastScan.StartedAtUtc >= state.StartedAtUtc)
        {
            return (null, lastScan);
        }

        var interrupted = new AntivirusScanResultDto
        {
            Target = state.Target,
            StartedAtUtc = state.StartedAtUtc,
            Status = "interrupted"
        };

        await fileStore.WriteAsync(StatePath, interrupted, cancellationToken);
        return (null, interrupted);
    }

    /// <summary>
    /// "Sürüyor" yuvasını AYIRTIR: yuva doluysa 409, boşsa kaydı yazar.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>KAPATILAN YARIŞ (ölçüldü):</b> kontrol ile yazma AYRI iki çağrıydı ve aralarında
    /// <c>await</c> vardı. Aynı anda gelen N istek hepsi "tarama yok" görüp hepsi kayıt yazıyordu:
    /// N tane <c>clamscan.exe</c> aynı ağaçta, her biri 2 saat zaman aşımlı. Yani 409 koruması
    /// kâğıt üzerinde vardı, gerçekte YOKTU.
    /// </para>
    /// <para>
    /// <b>DESEN İCAT EDİLMEDİ:</b> <see cref="JsonFileStore.UpdateAsync"/> dosya başına kilit
    /// tutar ve okuma-değiştirme-yazmanın tamamı o kilidin altındadır;
    /// <c>RunningJobService.BeginAsync</c> aynı sorunu birebir bu şekilde çözüyor. Kilidi ALMAYAN
    /// <see cref="JsonFileStore.WriteAsync"/> ile yazmak, kontrolü kilidin dışında bırakmak
    /// demekti.
    /// </para>
    /// <para>
    /// Sahipsiz (ölü süreçten kalan) kayıt yuvayı DOLU saymaz: bir kez çöken panel bir daha asla
    /// tarama yapamazdı.
    /// </para>
    /// </remarks>
    /// <remarks>
    /// <c>internal</c>: yarışın kapandığı ancak AYNI ANDA çağrılarak kanıtlanabilir. Gerçek
    /// <c>ScanAsync</c> üzerinden ölçmek, bu makinede olmayan ve saniyelerce koşması gereken bir
    /// <c>clamscan.exe</c> gerektirirdi. Dışarıya yeni bir uç açmaz.
    /// </remarks>
    internal Task YuvayiAyirtAsync(string target, DateTime startedAtUtc, CancellationToken cancellationToken) =>
        fileStore.UpdateAsync<AntivirusRunningScanState?>(
            RunningStatePath,
            null,
            mevcut =>
            {
                if (mevcut is not null && mevcut.StartedAtUtc != default && IsOwnedByCurrentProcess(mevcut))
                {
                    throw new AppException(
                        "antivirus.scan_in_progress",
                        409,
                        409,
                        $"A scan of '{mevcut.Target}' started at {mevcut.StartedAtUtc:O} is still running.");
                }

                return new AntivirusRunningScanState
                {
                    Target = target,
                    StartedAtUtc = startedAtUtc,
                    OwnerProcessId = Environment.ProcessId,
                    OwnerProcessStartedAtTicks = CurrentProcessStartTicks()
                };
            },
            cancellationToken);

    /// <summary>
    /// Kaydı boşaltır. Dosya SİLİNMEZ, içine <c>null</c> yazılır: <see cref="JsonFileStore"/>
    /// silme sunmuyor ve <c>null</c> okunduğunda zaten "kayıt yok" anlamına geliyor. Böylece
    /// temizleme de yazma kadar atomik olur (yarım kalmış silme diye bir durum kalmaz).
    /// </summary>
    /// <param name="startedAtUtc">
    /// Verilirse YALNIZ o başlangıç anına sahip kayıt silinir; <c>null</c> ise yuva koşulsuz
    /// boşaltılır (yalnız BAYAT kayıt uzlaştırmasında böyle çağrılır).
    /// <para>
    /// <b>NEDEN KARŞILAŞTIRILIYOR:</b> temizleme de ayırtma gibi kilidin altında yapılıyor, ama
    /// kilit tek başına yetmez — koşulsuz bir temizleme, BAŞKA bir taramanın yeni açtığı kaydı
    /// silebilirdi (biten tarama ile başlayan tarama sıralanmış olsa bile). O an panel "tarama
    /// yok" der, ekran boşalır ve ÜÇÜNCÜ bir tarama başlatılabilirdi. Aynı savunma
    /// <c>RunningJobService.EndAsync</c>'te de yazılı.
    /// </para>
    /// </param>
    /// <remarks><c>internal</c>: "yalnız kendi kaydını siler" güvencesi ancak dışarıdan çağrılarak ölçülebilir.</remarks>
    internal Task ClearRunningScanAsync(DateTime? startedAtUtc, CancellationToken cancellationToken) =>
        fileStore.UpdateAsync<AntivirusRunningScanState?>(
            RunningStatePath,
            null,
            mevcut => startedAtUtc is { } beklenen && mevcut is not null && mevcut.StartedAtUtc != beklenen
                ? mevcut
                : null,
            cancellationToken);

    /// <summary>Kaydın sahibi bu panel süreci mi (PID + süreç başlangıç anı çifti).</summary>
    internal static bool IsOwnedByCurrentProcess(AntivirusRunningScanState state)
    {
        if (state.OwnerProcessId != Environment.ProcessId)
        {
            return false;
        }

        var ticks = CurrentProcessStartTicks();

        // Başlangıç anı ölçülemediyse (kısıtlı ortam) tek elde kalan kanıt PID'dir; onu da yok
        // saymak, süren HER taramayı "yarıda kalmış" ilan etmek olurdu.
        return ticks == 0 || state.OwnerProcessStartedAtTicks == 0 || state.OwnerProcessStartedAtTicks == ticks;
    }

    private static long CurrentProcessStartTicks()
    {
        try
        {
            using var current = Process.GetCurrentProcess();
            return current.StartTime.ToUniversalTime().Ticks;
        }
        catch
        {
            // Bazı ortamlarda süreç başlangıç anı okunamaz; 0 = "ölçülemedi".
            return 0;
        }
    }

    /// <summary>Site adı verilmişse IIS'ten kökünü çözer (mutlak yol yalnız admin ucundan gelir; controller admin-only).</summary>
    private async Task<(string Target, string Path)> ResolveTargetAsync(AntivirusScanRequest request, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(request.SiteName))
        {
            var site = await iisSiteService.GetSiteAsync(request.SiteName.Trim(), cancellationToken);
            var root = string.IsNullOrWhiteSpace(site.ApplicationRootPath) ? site.PhysicalPath : site.ApplicationRootPath;
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                throw new AppException("antivirus.target_not_found", 404, 404, $"Site root '{root}' does not exist.");
            }

            return (site.Name, root);
        }

        var path = (request.Path ?? string.Empty).Trim();
        if (path.Length == 0 || !Path.IsPathFullyQualified(path) || !Directory.Exists(path))
        {
            throw new AppException("antivirus.target_not_found", 404, 404, $"Path '{path}' does not exist.");
        }

        return (path, path);
    }

    private async Task<Core.Entities.InstalledComponent?> FindComponentAsync(CancellationToken cancellationToken)
    {
        var component = await dbContext.InstalledComponents
            .AsNoTracking()
            .FirstOrDefaultAsync(
                item => item.ComponentId == ComponentId && item.Status == ComponentInstallStatus.Installed,
                cancellationToken);

        return component is not null && !string.IsNullOrWhiteSpace(component.InstallDir) && Directory.Exists(component.InstallDir)
            ? component
            : null;
    }

    private async Task<Core.Entities.InstalledComponent> RequireComponentAsync(CancellationToken cancellationToken) =>
        await FindComponentAsync(cancellationToken)
            ?? throw new AppException("antivirus.not_installed", 409, 409);

    private static string GetDatabaseDirectory(string installDirectory) =>
        Path.Combine(installDirectory, "database");

    /// <summary>Tanım dosyalarının (.cvd ana paket / .cld artımlı) en yeni değişiklik zamanı; yoksa null.</summary>
    private static DateTime? GetNewestDefinitionUtc(string databaseDirectory)
    {
        if (!Directory.Exists(databaseDirectory))
        {
            return null;
        }

        DateTime? newest = null;
        foreach (var pattern in new[] { "*.cvd", "*.cld" })
        {
            foreach (var file in Directory.EnumerateFiles(databaseDirectory, pattern))
            {
                var stamp = File.GetLastWriteTimeUtc(file);
                if (newest is null || stamp > newest)
                {
                    newest = stamp;
                }
            }
        }

        return newest;
    }

    private static string? FindExecutable(string installDirectory, string executableName) =>
        Directory.EnumerateFiles(installDirectory, executableName, SearchOption.AllDirectories).FirstOrDefault();

    /// <summary>
    /// freshclam.conf'u kurulum altına yazar (yoksa). Resmi mirror + veritabanı dizini; log/pid gibi
    /// Unix-varsayılanları bilinçli boş bırakılır (Windows'ta yazma izni sorunlarına yol açar).
    /// </summary>
    private static async Task<string> EnsureFreshclamConfigAsync(string installDirectory, string databaseDirectory, CancellationToken cancellationToken)
    {
        var configPath = Path.Combine(installDirectory, "freshclam.conf");
        if (!File.Exists(configPath))
        {
            await File.WriteAllTextAsync(
                configPath,
                $"""
                DatabaseDirectory {databaseDirectory}
                DatabaseMirror database.clamav.net
                """,
                cancellationToken);
        }

        return configPath;
    }

    private static string Truncate(string value) =>
        value.Length <= 2000 ? value.Trim() : value[..2000].Trim();
}
