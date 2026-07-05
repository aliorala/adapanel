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

    // clamscan bulgu satırı: "<dosya yolu>: <Imza.Adi> FOUND"
    private static readonly Regex FoundLine = new(@"^(?<path>.+?): (?<sig>\S+) FOUND\s*$", RegexOptions.Compiled);
    private static readonly Regex ScannedFilesLine = new(@"^Scanned files:\s*(?<count>\d+)", RegexOptions.Compiled);

    public async Task<AntivirusStatusDto> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var component = await FindComponentAsync(cancellationToken);
        var lastScan = await fileStore.ReadAsync<AntivirusScanResultDto?>(StatePath, null, cancellationToken);

        if (component is null)
        {
            return new AntivirusStatusDto { IsInstalled = false, LastScan = lastScan };
        }

        var databaseDirectory = GetDatabaseDirectory(component.InstallDir);
        var newestDefinition = GetNewestDefinitionUtc(databaseDirectory);

        return new AntivirusStatusDto
        {
            IsInstalled = true,
            Version = component.Version,
            DatabaseReady = newestDefinition is not null,
            DatabaseUpdatedAtUtc = newestDefinition,
            LastScan = lastScan
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
        var startedAt = DateTime.UtcNow;
        var stopwatch = Stopwatch.StartNew();

        // --infected: yalnız bulgular yazılır (dev sitelerde çıktı şişmez); özet (Scanned files) korunur.
        var result = await ProcessRunner.RunAsync(
            clamscan,
            $"--database=\"{databaseDirectory}\" --recursive --infected \"{path}\"",
            workingDirectory: Path.GetDirectoryName(clamscan),
            cancellationToken: cancellationToken,
            timeout: TimeSpan.FromHours(2));

        stopwatch.Stop();

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
                    scannedFiles = int.Parse(scanned.Groups["count"].Value);
                }
            }

            scan = new AntivirusScanResultDto
            {
                Target = target,
                StartedAtUtc = startedAt,
                DurationSeconds = Math.Round(stopwatch.Elapsed.TotalSeconds, 1),
                ScannedFiles = scannedFiles,
                InfectedCount = findings.Count,
                Infected = findings,
                Status = findings.Count > 0 ? "infected" : "clean"
            };
        }
        else
        {
            scan = new AntivirusScanResultDto
            {
                Target = target,
                StartedAtUtc = startedAt,
                DurationSeconds = Math.Round(stopwatch.Elapsed.TotalSeconds, 1),
                Status = "failed",
                Detail = Truncate(result.StandardError.Length > 0 ? result.StandardError : result.StandardOutput)
            };
        }

        await fileStore.WriteAsync(StatePath, scan, cancellationToken);
        return scan;
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
