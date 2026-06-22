using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;

namespace AdaPanel.Infrastructure.AppStore;

/// <summary>
/// Plesk WordPress Toolkit'in temel, dosya tabanlı karşılığı. WP-CLI gerektirmez:
/// wp-config.php varlığına göre algılar, wp-includes/version.php'den sürümü okur,
/// bakım modunu .maintenance dosyasıyla yönetir.
/// </summary>
public sealed partial class WordPressToolkitService(IIisSiteService iisSiteService) : IWordPressToolkitService
{
    public async Task<WordPressSiteDto> DetectAsync(string siteName, CancellationToken cancellationToken = default)
    {
        var site = await iisSiteService.GetSiteAsync(siteName, cancellationToken);
        var root = string.IsNullOrWhiteSpace(site.ApplicationRootPath) ? site.PhysicalPath : site.ApplicationRootPath;
        var latest = await GetLatestCoreVersionAsync(cancellationToken);
        return BuildDto(site.Name, root, site.Bindings.FirstOrDefault()?.HostName, latest);
    }

    public async Task<IReadOnlyCollection<WordPressSiteDto>> DetectAllAsync(CancellationToken cancellationToken = default)
    {
        var sites = await iisSiteService.GetSitesAsync(cancellationToken);
        var latest = await GetLatestCoreVersionAsync(cancellationToken);
        var results = new List<WordPressSiteDto>();
        foreach (var site in sites)
        {
            var root = string.IsNullOrWhiteSpace(site.ApplicationRootPath) ? site.PhysicalPath : site.ApplicationRootPath;
            var dto = BuildDto(site.Name, root, site.Bindings.FirstOrDefault()?.HostName, latest);
            if (dto.IsWordPress)
            {
                results.Add(dto);
            }
        }

        return results;
    }

    public async Task<WordPressSiteDto> SetMaintenanceAsync(string siteName, bool enabled, CancellationToken cancellationToken = default)
    {
        var site = await iisSiteService.GetSiteAsync(siteName, cancellationToken);
        var root = string.IsNullOrWhiteSpace(site.ApplicationRootPath) ? site.PhysicalPath : site.ApplicationRootPath;
        var maintenanceFile = Path.Combine(root, ".maintenance");

        if (enabled)
        {
            await File.WriteAllTextAsync(maintenanceFile, "<?php $upgrading = time(); ?>", cancellationToken);
        }
        else if (File.Exists(maintenanceFile))
        {
            File.Delete(maintenanceFile);
        }

        var latest = await GetLatestCoreVersionAsync(cancellationToken);
        return BuildDto(site.Name, root, site.Bindings.FirstOrDefault()?.HostName, latest);
    }

    private static WordPressSiteDto BuildDto(string siteName, string root, string? hostName, string latestVersion = "")
    {
        var dto = new WordPressSiteDto { SiteName = siteName, Path = root };
        if (string.IsNullOrWhiteSpace(root) || !File.Exists(Path.Combine(root, "wp-config.php")))
        {
            return dto;
        }

        dto.IsWordPress = true;
        dto.MaintenanceMode = File.Exists(Path.Combine(root, ".maintenance"));
        dto.Version = ReadVersion(root);
        dto.Plugins = ReadPlugins(root);
        dto.Themes = ReadThemes(root);
        dto.LatestVersion = latestVersion;
        dto.UpdateAvailable = IsOlderVersion(dto.Version, latestVersion);
        if (!string.IsNullOrWhiteSpace(hostName))
        {
            dto.AdminUrl = $"http://{hostName}/wp-admin/";
        }

        return dto;
    }

    // wp-content/plugins: her klasörde "Plugin Name:" başlıklı ana php dosyası + tek-dosyalık eklentiler.
    private static IReadOnlyCollection<WordPressComponentDto> ReadPlugins(string root)
    {
        var pluginsDir = Path.Combine(root, "wp-content", "plugins");
        var list = new List<WordPressComponentDto>();
        if (!Directory.Exists(pluginsDir))
        {
            return list;
        }

        try
        {
            foreach (var folder in Directory.EnumerateDirectories(pluginsDir))
            {
                foreach (var file in Directory.EnumerateFiles(folder, "*.php", SearchOption.TopDirectoryOnly))
                {
                    var header = ReadHeader(file);
                    if (header.TryGetValue("Plugin Name", out var name))
                    {
                        list.Add(new WordPressComponentDto
                        {
                            Slug = Path.GetFileName(folder),
                            Name = name,
                            Version = header.GetValueOrDefault("Version", string.Empty)
                        });
                        break;
                    }
                }
            }

            foreach (var file in Directory.EnumerateFiles(pluginsDir, "*.php", SearchOption.TopDirectoryOnly))
            {
                var header = ReadHeader(file);
                if (header.TryGetValue("Plugin Name", out var name))
                {
                    list.Add(new WordPressComponentDto
                    {
                        Slug = Path.GetFileNameWithoutExtension(file),
                        Name = name,
                        Version = header.GetValueOrDefault("Version", string.Empty)
                    });
                }
            }
        }
        catch
        {
            // erişilemeyen klasör/dosya atlanır
        }

        return list.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // wp-content/themes/*/style.css başlığından temalar.
    private static IReadOnlyCollection<WordPressComponentDto> ReadThemes(string root)
    {
        var themesDir = Path.Combine(root, "wp-content", "themes");
        var list = new List<WordPressComponentDto>();
        if (!Directory.Exists(themesDir))
        {
            return list;
        }

        try
        {
            foreach (var folder in Directory.EnumerateDirectories(themesDir))
            {
                var style = Path.Combine(folder, "style.css");
                if (!File.Exists(style))
                {
                    continue;
                }

                var header = ReadHeader(style);
                list.Add(new WordPressComponentDto
                {
                    Slug = Path.GetFileName(folder),
                    Name = header.GetValueOrDefault("Theme Name", Path.GetFileName(folder)),
                    Version = header.GetValueOrDefault("Version", string.Empty)
                });
            }
        }
        catch
        {
            // erişilemeyen klasör/dosya atlanır
        }

        return list.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // WordPress eklenti/tema başlığı: dosyanın ilk ~8KB'ında "Anahtar: Değer" satırları.
    private static Dictionary<string, string> ReadHeader(string file)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var reader = new StreamReader(file);
            var buffer = new char[8192];
            var read = reader.Read(buffer, 0, buffer.Length);
            var text = new string(buffer, 0, read);
            foreach (Match match in HeaderRegex().Matches(text))
            {
                var key = match.Groups[1].Value.Trim();
                if (!result.ContainsKey(key))
                {
                    result[key] = match.Groups[2].Value.Trim();
                }
            }
        }
        catch
        {
            // okunamayan dosya boş başlık döner
        }

        return result;
    }

    private static string ReadVersion(string root)
    {
        var versionFile = Path.Combine(root, "wp-includes", "version.php");
        if (!File.Exists(versionFile))
        {
            return string.Empty;
        }

        try
        {
            var content = File.ReadAllText(versionFile);
            var match = VersionRegex().Match(content);
            return match.Success ? match.Groups[1].Value : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    // En güncel kararlı WordPress çekirdek sürümü (wordpress.org), süreç ömrü boyunca 6 saat cache'li.
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private static string cachedLatestVersion = string.Empty;
    private static DateTime cachedLatestAtUtc = DateTime.MinValue;
    private static readonly SemaphoreSlim VersionLock = new(1, 1);

    private static async Task<string> GetLatestCoreVersionAsync(CancellationToken cancellationToken)
    {
        if (cachedLatestVersion.Length > 0 && DateTime.UtcNow - cachedLatestAtUtc < TimeSpan.FromHours(6))
        {
            return cachedLatestVersion;
        }

        await VersionLock.WaitAsync(cancellationToken);
        try
        {
            if (cachedLatestVersion.Length > 0 && DateTime.UtcNow - cachedLatestAtUtc < TimeSpan.FromHours(6))
            {
                return cachedLatestVersion;
            }

            using var response = await Http.GetAsync("https://api.wordpress.org/core/version-check/1.7/", cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (document.RootElement.TryGetProperty("offers", out var offers)
                && offers.ValueKind == JsonValueKind.Array
                && offers.GetArrayLength() > 0
                && offers[0].TryGetProperty("current", out var current)
                && current.ValueKind == JsonValueKind.String)
            {
                cachedLatestVersion = current.GetString() ?? string.Empty;
                cachedLatestAtUtc = DateTime.UtcNow;
            }
        }
        catch
        {
            // İnternet yok / API erişilemez: güncelleme kontrolü sessizce atlanır (mevcut cache korunur).
        }
        finally
        {
            VersionLock.Release();
        }

        return cachedLatestVersion;
    }

    // installed < latest mi (her ikisi de geçerli sürümse). Belirsizse false (güncelleme önerme).
    private static bool IsOlderVersion(string installed, string latest)
    {
        if (string.IsNullOrWhiteSpace(installed) || string.IsNullOrWhiteSpace(latest))
        {
            return false;
        }

        return Version.TryParse(Normalize(installed), out var installedVersion)
            && Version.TryParse(Normalize(latest), out var latestVersion)
            && installedVersion < latestVersion;

        static string Normalize(string value)
        {
            var core = value.Split('-', '+')[0];
            var parts = core.Split('.');
            return parts.Length switch
            {
                1 => $"{parts[0]}.0",
                _ => core
            };
        }
    }

    [GeneratedRegex(@"\$wp_version\s*=\s*'([^']+)'")]
    private static partial Regex VersionRegex();

    // Eklenti/tema başlık satırı: olası yorum işaretlerinden sonra "Anahtar: Değer".
    [GeneratedRegex(@"(?m)^[ \t/*#@]*([A-Za-z][A-Za-z ]+):[ \t]*(.+?)[ \t]*$")]
    private static partial Regex HeaderRegex();
}
