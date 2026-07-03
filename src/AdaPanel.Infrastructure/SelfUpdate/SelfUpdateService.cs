using AdaPanel.Core.Dtos.Update;
using AdaPanel.Core.Common;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Support;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace AdaPanel.Infrastructure.SelfUpdate;

public sealed class SelfUpdateService(
    JsonFileStore fileStore,
    IHttpClientFactory httpClientFactory,
    IUpdateInstallerLauncher installerLauncher) : ISelfUpdateService
{
    private const string ManifestPath = "config/update-manifest.json";
    private const string GithubConfigPath = "config/update-github.json";
    private const string PendingUpdatePath = "updates/pending-update.json";
    private const string DefaultRepo = "aliorala/adapanel";

    public async Task<UpdateStatusDto> CheckForUpdateAsync(CancellationToken cancellationToken = default)
    {
        var currentVersion = VersionInfo.Current;
        var github = await fileStore.ReadAsync(GithubConfigPath, new GithubConfig { Repo = DefaultRepo }, cancellationToken);
        var fetchNote = string.Empty;

        // GitHub deposu ayarlıysa en son Release'i ÇEK (sürüm + .exe indirme linki + SHA256) ve yerel manifeste
        // önbelleğe yaz → indir/kur akışı yerel manifestle aynen çalışır. Hata olursa net not (UI gösterir), tab çökmiyor.
        if (!string.IsNullOrWhiteSpace(github.Repo))
        {
            try
            {
                var remote = await FetchGithubManifestAsync(github.Repo.Trim(), cancellationToken);
                if (remote is not null)
                {
                    await fileStore.WriteAsync(ManifestPath, remote, cancellationToken);
                }
            }
            catch (AppException appException)
            {
                fetchNote = appException.MessageKey;
            }
            catch
            {
                fetchNote = "update.github_fetch_failed";
            }
        }

        var manifest = await fileStore.ReadAsync(ManifestPath, new UpdateManifest(), cancellationToken);
        var latestVersion = string.IsNullOrWhiteSpace(manifest.LatestVersion) ? currentVersion : manifest.LatestVersion;
        var pendingUpdate = await fileStore.ReadAsync(PendingUpdatePath, new PendingUpdate(), cancellationToken);

        // Guncelleme UYGULANDIYSA (calisan surum == bekleyen paketin surumu) bekleyen durumu temizle:
        // "kurulum planlandi" etiketi sonsuza dek kalmasin, eski ~50MB paket updates klasorunde birikmesin.
        if (!string.IsNullOrWhiteSpace(pendingUpdate.Version) &&
            string.Equals(pendingUpdate.Version, currentVersion, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(pendingUpdate.PackagePath) && File.Exists(pendingUpdate.PackagePath))
                {
                    File.Delete(pendingUpdate.PackagePath);
                }

                File.Delete(fileStore.ResolvePath(PendingUpdatePath));
            }
            catch
            {
                // Temizlik best-effort: dosya kilitliyse bir sonraki kontrolde tekrar denenir.
            }

            pendingUpdate = new PendingUpdate();
        }

        var defaultNote = string.IsNullOrWhiteSpace(github.Repo) ? "update.github_not_configured" : "update.github_ok";
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
            Notes = !string.IsNullOrEmpty(fetchNote) ? fetchNote : (string.IsNullOrWhiteSpace(manifest.Notes) ? defaultNote : manifest.Notes),
            GithubRepo = github.Repo,
            IsUpdateAvailable = IsNewerVersion(currentVersion, latestVersion),
            CheckedAtUtc = DateTime.UtcNow
        };
    }

    public async Task<UpdateStatusDto> SetGithubRepoAsync(string repo, CancellationToken cancellationToken = default)
    {
        var trimmed = (repo ?? string.Empty).Trim();
        await fileStore.WriteAsync(GithubConfigPath, new GithubConfig { Repo = trimmed }, cancellationToken);
        return await CheckForUpdateAsync(cancellationToken);
    }

    // GitHub Releases API'sinden en son sürümü çevirir → UpdateManifest. owner/repo (ya da github.com URL'si) kabul eder.
    // .exe asset'inin indirme linki + SHA256'sı (asset 'digest' alanı veya '<exe>.sha256' asset'i) alınır. PUBLIC repo:
    // kimlik gerektirmez (User-Agent zorunlu). SHA256 bulunamazsa güvenlik gereği net hata (.sha256 ekleyin) verir.
    private async Task<UpdateManifest?> FetchGithubManifestAsync(string repo, CancellationToken cancellationToken)
    {
        var clean = repo.Trim().Trim('/');
        var marker = clean.IndexOf("github.com/", StringComparison.OrdinalIgnoreCase);
        if (marker >= 0)
        {
            clean = clean[(marker + "github.com/".Length)..];
        }
        var parts = clean.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            throw new AppException("update.github_repo_invalid", 400, 400, $"Repo={repo}");
        }
        var owner = parts[0];
        var name = parts[1];

        var client = httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(20);
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "AdaPanel");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/vnd.github+json");
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");

        using var response = await client.GetAsync($"https://api.github.com/repos/{owner}/{name}/releases/latest", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new AppException("update.github_no_release", 404, 404, $"{owner}/{name}");
        }
        if (!response.IsSuccessStatusCode)
        {
            throw new AppException("update.github_fetch_failed", (int)response.StatusCode, 502, $"{owner}/{name} HTTP {(int)response.StatusCode}");
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var tag = root.TryGetProperty("tag_name", out var tagElement) ? tagElement.GetString() ?? string.Empty : string.Empty;
        var version = tag.TrimStart('v', 'V').Trim();
        var notes = root.TryGetProperty("body", out var bodyElement) ? bodyElement.GetString() ?? string.Empty : string.Empty;

        var downloadUrl = string.Empty;
        var sha = string.Empty;
        var exeAssetName = string.Empty;
        string? shaAssetUrl = null;

        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var assetName = asset.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty;
                if (assetName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    downloadUrl = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() ?? string.Empty : string.Empty;
                    exeAssetName = assetName;
                    if (asset.TryGetProperty("digest", out var digestElement) && digestElement.ValueKind == JsonValueKind.String)
                    {
                        var digest = digestElement.GetString() ?? string.Empty;
                        if (digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                        {
                            sha = digest["sha256:".Length..].Trim();
                        }
                    }
                }
            }

            // .exe var ama digest yoksa: bir SHA256 asset'i ara ('<exe>.sha256', '*.sha256', 'SHA256SUMS').
            if (!string.IsNullOrEmpty(downloadUrl) && string.IsNullOrEmpty(sha))
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var assetName = asset.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty;
                    if (assetName.Equals($"{exeAssetName}.sha256", StringComparison.OrdinalIgnoreCase) ||
                        assetName.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase) ||
                        assetName.Equals("SHA256SUMS", StringComparison.OrdinalIgnoreCase))
                    {
                        shaAssetUrl = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                        break;
                    }
                }
            }
        }

        if (string.IsNullOrEmpty(downloadUrl))
        {
            throw new AppException("update.github_no_asset", 404, 404, $"{owner}/{name}: latest release has no .exe asset");
        }

        if (string.IsNullOrEmpty(sha) && shaAssetUrl is not null)
        {
            var shaContent = await client.GetStringAsync(shaAssetUrl, cancellationToken);
            sha = ExtractSha256(shaContent);
        }

        if (string.IsNullOrEmpty(sha))
        {
            throw new AppException("update.github_no_sha", 422, 422, $"{owner}/{name}: .exe asset has no SHA256 (digest/.sha256)");
        }

        return new UpdateManifest
        {
            LatestVersion = version,
            DownloadUrl = downloadUrl,
            PackageSha256 = sha,
            Notes = notes
        };
    }

    // Metinden ilk 64 karakterlik hex SHA256 dizisini çıkarır (".sha256"/SHA256SUMS dosyaları: "hash *dosya" biçimi).
    private static string ExtractSha256(string content)
    {
        foreach (var token in content.Split([' ', '\t', '\r', '\n', '*'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.Length == 64 && token.All(Uri.IsHexDigit))
            {
                return token;
            }
        }
        return string.Empty;
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

    // Yalnizca GERCEKTEN daha yeni surumde "guncelleme var" de: esit ya da ESKI release tag'i
    // (or. panel 0.2.7 iken GitHub'da hala 0.2.6 varsa) uyari dogurmasin. "-preview"/"+hash"
    // ekleri sayisal kiyastan once atilir; sayi olarak cozulemeyen surumlerde eski esitsizlik davranisi korunur.
    internal static bool IsNewerVersion(string currentVersion, string latestVersion)
    {
        if (string.IsNullOrWhiteSpace(latestVersion))
        {
            return false;
        }

        var current = TryParseVersion(currentVersion);
        var latest = TryParseVersion(latestVersion);
        if (current is null || latest is null)
        {
            return !string.Equals(currentVersion, latestVersion, StringComparison.OrdinalIgnoreCase);
        }

        return latest > current;
    }

    private static Version? TryParseVersion(string value)
    {
        var core = value.Split('-', '+')[0].Trim();
        if (!core.Contains('.'))
        {
            core += ".0";
        }

        return Version.TryParse(core, out var parsed) ? parsed : null;
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

    private sealed class GithubConfig
    {
        public string Repo { get; set; } = string.Empty;
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
