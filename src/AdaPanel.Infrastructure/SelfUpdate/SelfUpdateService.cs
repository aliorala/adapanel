using AdaPanel.Core.Dtos.Update;
using AdaPanel.Core.Common;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Support;
using System.Net;
using System.Security.Cryptography;

namespace AdaPanel.Infrastructure.SelfUpdate;

public sealed class SelfUpdateService(
    JsonFileStore fileStore,
    IHttpClientFactory httpClientFactory,
    IUpdateInstallerLauncher installerLauncher) : ISelfUpdateService
{
    private const string ManifestPath = "config/update-manifest.json";
    private const string PendingUpdatePath = "updates/pending-update.json";

    public async Task<UpdateStatusDto> CheckForUpdateAsync(CancellationToken cancellationToken = default)
    {
        var currentVersion = VersionInfo.Current;
        var manifest = await fileStore.ReadAsync(ManifestPath, new UpdateManifest(), cancellationToken);
        var latestVersion = string.IsNullOrWhiteSpace(manifest.LatestVersion) ? currentVersion : manifest.LatestVersion;
        var pendingUpdate = await fileStore.ReadAsync(PendingUpdatePath, new PendingUpdate(), cancellationToken);

        return new UpdateStatusDto
        {
            CurrentVersion = currentVersion,
            LatestVersion = latestVersion,
            DownloadUrl = manifest.DownloadUrl,
            PackageSha256 = manifest.PackageSha256,
            PackagePath = pendingUpdate.PackagePath,
            IsPackageReady = !string.IsNullOrWhiteSpace(pendingUpdate.PackagePath) && File.Exists(pendingUpdate.PackagePath),
            IsInstallScheduled = pendingUpdate.InstallScheduledAtUtc.HasValue,
            InstallScheduledAtUtc = pendingUpdate.InstallScheduledAtUtc,
            Notes = string.IsNullOrWhiteSpace(manifest.Notes) ? "update.feed_not_configured" : manifest.Notes,
            IsUpdateAvailable = !string.Equals(currentVersion, latestVersion, StringComparison.OrdinalIgnoreCase),
            CheckedAtUtc = DateTime.UtcNow
        };
    }

    public async Task<UpdateStatusDto> ApplyUpdateAsync(ApplyUpdateRequest request, CancellationToken cancellationToken = default)
    {
        var status = await CheckForUpdateAsync(cancellationToken);
        var url = string.IsNullOrWhiteSpace(request.DownloadUrl) ? status.DownloadUrl : request.DownloadUrl.Trim();
        if (string.IsNullOrWhiteSpace(url))
        {
            return new UpdateStatusDto
            {
                CurrentVersion = status.CurrentVersion,
                LatestVersion = status.LatestVersion,
                DownloadUrl = status.DownloadUrl,
                PackageSha256 = status.PackageSha256,
                PackagePath = status.PackagePath,
                IsPackageReady = status.IsPackageReady,
                Notes = "update.package_url_missing",
                IsUpdateAvailable = status.IsUpdateAvailable,
                CheckedAtUtc = DateTime.UtcNow
            };
        }

        var packageUri = ResolvePackageUri(url);
        var expectedHash = NormalizeSha256(string.IsNullOrWhiteSpace(request.PackageSha256)
            ? status.PackageSha256
            : request.PackageSha256);
        var updatesDirectory = fileStore.EnsureDirectory("updates");
        var destination = Path.Combine(updatesDirectory, ResolvePackageFileName(packageUri));
        var client = httpClientFactory.CreateClient();
        client.Timeout = Timeout.InfiniteTimeSpan;
        using var response = await client.GetAsync(packageUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new AppException("update.download_not_found", 404, 404, $"Url={packageUri}");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new AppException(
                "update.download_failed",
                (int)response.StatusCode,
                502,
                $"Url={packageUri}\r\nStatusCode={(int)response.StatusCode} {response.ReasonPhrase}");
        }

        await using (var stream = File.Create(destination))
        {
            await response.Content.CopyToAsync(stream, cancellationToken);
        }

        try
        {
            await EnsureWindowsExecutableAsync(destination, cancellationToken);
        }
        catch
        {
            File.Delete(destination);
            throw;
        }

        var actualHash = await CalculateSha256Async(destination, cancellationToken);
        if (!string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(destination);
            throw new AppException("update.checksum_mismatch", 400, 400, $"Expected={expectedHash}\r\nActual={actualHash}");
        }

        var pendingUpdate = new PendingUpdate
        {
            Version = status.LatestVersion,
            DownloadUrl = url,
            PackagePath = destination,
            PackageSha256 = actualHash,
            StagedAtUtc = DateTime.UtcNow,
            InstallScheduledAtUtc = null
        };
        await fileStore.WriteAsync(PendingUpdatePath, pendingUpdate, cancellationToken);

        return new UpdateStatusDto
        {
            CurrentVersion = status.CurrentVersion,
            LatestVersion = status.LatestVersion,
            DownloadUrl = packageUri.ToString(),
            PackageSha256 = actualHash,
            PackagePath = destination,
            IsPackageReady = true,
            IsInstallScheduled = false,
            Notes = "update.package_staged",
            IsUpdateAvailable = status.IsUpdateAvailable,
            CheckedAtUtc = DateTime.UtcNow
        };
    }

    public async Task<UpdateStatusDto> InstallPreparedUpdateAsync(CancellationToken cancellationToken = default)
    {
        var pendingUpdate = await fileStore.ReadAsync(PendingUpdatePath, new PendingUpdate(), cancellationToken);
        if (string.IsNullOrWhiteSpace(pendingUpdate.PackagePath) ||
            string.IsNullOrWhiteSpace(pendingUpdate.PackageSha256) ||
            !File.Exists(pendingUpdate.PackagePath))
        {
            throw new AppException("update.package_not_ready", 409, 409);
        }

        var updatesDirectory = Path.GetFullPath(fileStore.EnsureDirectory("updates"));
        var packagePath = Path.GetFullPath(pendingUpdate.PackagePath);
        if (!packagePath.StartsWith(
                updatesDirectory + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new AppException("update.package_not_ready", 409, 409);
        }

        await EnsureWindowsExecutableAsync(packagePath, cancellationToken);
        var expectedHash = NormalizeSha256(pendingUpdate.PackageSha256);
        var actualHash = await CalculateSha256Async(packagePath, cancellationToken);
        if (!string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new AppException(
                "update.checksum_mismatch",
                400,
                400,
                $"Expected={expectedHash}\r\nActual={actualHash}");
        }

        await installerLauncher.ScheduleAsync(packagePath, actualHash, cancellationToken);
        pendingUpdate.InstallScheduledAtUtc = DateTime.UtcNow;
        await fileStore.WriteAsync(PendingUpdatePath, pendingUpdate, cancellationToken);

        var status = await CheckForUpdateAsync(cancellationToken);
        status.Notes = "update.install_scheduled";
        return status;
    }

    private static async Task<string> CalculateSha256Async(string filePath, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(filePath);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash);
    }

    private static Uri ResolvePackageUri(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
        {
            throw new AppException("update.package_url_invalid", 400, 400, $"Url={url}");
        }

        return uri;
    }

    private static string ResolvePackageFileName(Uri uri)
    {
        var fileName = Path.GetFileName(uri.LocalPath);
        return string.IsNullOrWhiteSpace(fileName)
            ? $"AdaPanelUpdate-{DateTime.UtcNow:yyyyMMddHHmmss}.exe"
            : fileName;
    }

    private static string NormalizeSha256(string? value)
    {
        var normalized = value?.Trim().ToUpperInvariant() ?? string.Empty;
        if (normalized.Length != 64 || normalized.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new AppException("update.checksum_required", 400, 400);
        }

        return normalized;
    }

    private static async Task EnsureWindowsExecutableAsync(string filePath, CancellationToken cancellationToken)
    {
        if (!string.Equals(Path.GetExtension(filePath), ".exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new AppException("update.package_invalid", 400, 400);
        }

        var header = new byte[2];
        await using var stream = File.OpenRead(filePath);
        var read = await stream.ReadAsync(header, cancellationToken);
        if (read != header.Length || header[0] != (byte)'M' || header[1] != (byte)'Z')
        {
            throw new AppException("update.package_invalid", 400, 400);
        }
    }

    private sealed class UpdateManifest
    {
        public string LatestVersion { get; set; } = string.Empty;

        public string DownloadUrl { get; set; } = string.Empty;

        public string PackageSha256 { get; set; } = string.Empty;

        public string Notes { get; set; } = string.Empty;
    }

    private sealed class PendingUpdate
    {
        public string Version { get; set; } = string.Empty;

        public string DownloadUrl { get; set; } = string.Empty;

        public string PackagePath { get; set; } = string.Empty;

        public string PackageSha256 { get; set; } = string.Empty;

        public DateTime StagedAtUtc { get; set; }

        public DateTime? InstallScheduledAtUtc { get; set; }
    }
}
