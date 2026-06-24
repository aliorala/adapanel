using System.Text;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Support;

namespace AdaPanel.Infrastructure.Iis;

/// <summary>
/// panel "Password-Protected Directories" karşılığı. Korunacak klasöre, Windows hesabı
/// gerektirmeyen, IIS URL Rewrite tabanlı Basic Auth uygulayan kendi-içeren bir web.config
/// yazar. Kayıtlar config/protected-dirs.json içinde listeleme için tutulur.
/// </summary>
public sealed class DirectoryProtectionService(
    IIisSiteService iisSiteService,
    JsonFileStore fileStore) : IDirectoryProtectionService
{
    private const string StatePath = "config/protected-dirs.json";
    private const string Marker = "adapanel-protected";

    public async Task<IReadOnlyCollection<ProtectedDirectoryDto>> GetProtectedDirectoriesAsync(string siteName, CancellationToken cancellationToken = default)
    {
        var all = await fileStore.ReadAsync(StatePath, Array.Empty<ProtectedDirectoryDto>(), cancellationToken);
        return all
            .Where(item => string.Equals(item.SiteName, siteName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<ProtectedDirectoryDto> ProtectAsync(string siteName, ProtectDirectoryRequest request, CancellationToken cancellationToken = default)
    {
        var relative = SanitizeRelativePath(request.RelativePath);
        if (string.IsNullOrWhiteSpace(request.UserName) || string.IsNullOrWhiteSpace(request.Password))
        {
            throw new AppException("protected.credentials_required", 400, 400);
        }

        var folder = ResolveFolder(await GetSiteRootAsync(siteName, cancellationToken), relative);
        Directory.CreateDirectory(folder);

        var webConfig = Path.Combine(folder, "web.config");
        if (File.Exists(webConfig) &&
            !File.ReadAllText(webConfig).Contains(Marker, StringComparison.OrdinalIgnoreCase))
        {
            // Kullanıcının kendi web.config'i varsa ezme.
            throw new AppException("protected.web_config_exists", 409, 409);
        }

        var realm = SanitizeRealm(string.IsNullOrWhiteSpace(request.Realm) ? $"{siteName} - {relative}" : request.Realm);
        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{request.UserName}:{request.Password}"));
        var escapedToken = EscapeRegex(token);

        var content = $$"""
            <?xml version="1.0" encoding="utf-8"?>
            <!-- {{Marker}} -->
            <configuration>
              <system.webServer>
                <rewrite>
                  <rules>
                    <rule name="adapanel-basic-auth" stopProcessing="true">
                      <match url="(.*)" />
                      <conditions>
                        <add input="{HTTP_AUTHORIZATION}" pattern="^Basic {{escapedToken}}$" negate="true" />
                      </conditions>
                      <action type="CustomResponse" statusCode="401" statusReason="Unauthorized" statusDescription="Authentication required" />
                    </rule>
                  </rules>
                </rewrite>
                <httpProtocol>
                  <customHeaders>
                    <add name="WWW-Authenticate" value="Basic realm=&quot;{{realm}}&quot;" />
                  </customHeaders>
                </httpProtocol>
              </system.webServer>
            </configuration>
            """;
        await File.WriteAllTextAsync(webConfig, content, cancellationToken);

        var dto = new ProtectedDirectoryDto
        {
            SiteName = siteName,
            RelativePath = relative,
            UserName = request.UserName,
            Realm = realm
        };

        var all = (await fileStore.ReadAsync(StatePath, Array.Empty<ProtectedDirectoryDto>(), cancellationToken)).ToList();
        all.RemoveAll(item => string.Equals(item.SiteName, siteName, StringComparison.OrdinalIgnoreCase) &&
                              string.Equals(item.RelativePath, relative, StringComparison.OrdinalIgnoreCase));
        all.Add(dto);
        await fileStore.WriteAsync(StatePath, all, cancellationToken);

        return dto;
    }

    public async Task UnprotectAsync(string siteName, string relativePath, CancellationToken cancellationToken = default)
    {
        var relative = SanitizeRelativePath(relativePath);
        var folder = ResolveFolder(await GetSiteRootAsync(siteName, cancellationToken), relative);
        var webConfig = Path.Combine(folder, "web.config");

        // Yalnız bizim yazdığımız (marker'lı) web.config'i sil; kullanıcının dosyasına dokunma.
        if (File.Exists(webConfig) &&
            File.ReadAllText(webConfig).Contains(Marker, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(webConfig);
        }

        var all = (await fileStore.ReadAsync(StatePath, Array.Empty<ProtectedDirectoryDto>(), cancellationToken)).ToList();
        var changed = all.RemoveAll(item => string.Equals(item.SiteName, siteName, StringComparison.OrdinalIgnoreCase) &&
                                            string.Equals(item.RelativePath, relative, StringComparison.OrdinalIgnoreCase));
        if (changed > 0)
        {
            await fileStore.WriteAsync(StatePath, all, cancellationToken);
        }
    }

    private async Task<string> GetSiteRootAsync(string siteName, CancellationToken cancellationToken)
    {
        var site = await iisSiteService.GetSiteAsync(siteName, cancellationToken);
        var root = string.IsNullOrWhiteSpace(site.ApplicationRootPath) ? site.PhysicalPath : site.ApplicationRootPath;
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new AppException("sites.site_not_found", 404, 404);
        }

        return root;
    }

    private static string ResolveFolder(string siteRoot, string relative)
    {
        var rootFull = Path.GetFullPath(siteRoot);
        var combined = Path.GetFullPath(Path.Combine(rootFull, relative.Replace('/', Path.DirectorySeparatorChar)));

        // Zincir kontrolü: hedef site kökünün dışına çıkamaz.
        if (!combined.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
        {
            throw new AppException("protected.invalid_path", 400, 400);
        }

        return combined;
    }

    private static string SanitizeRelativePath(string value)
    {
        var raw = (value ?? string.Empty).Trim().Replace('\\', '/');
        var segments = raw.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var clean = new List<string>();
        foreach (var segment in segments)
        {
            if (segment is "." or ".." || segment.Contains(':'))
            {
                throw new AppException("protected.invalid_path", 400, 400);
            }

            clean.Add(segment);
        }

        if (clean.Count == 0)
        {
            throw new AppException("protected.invalid_path", 400, 400);
        }

        return string.Join('/', clean);
    }

    private static string SanitizeRealm(string value)
    {
        var filtered = new string((value ?? string.Empty)
            .Where(ch => char.IsLetterOrDigit(ch) || ch is ' ' or '-' or '.' or '_')
            .ToArray())
            .Trim();
        return string.IsNullOrWhiteSpace(filtered) ? "Protected" : filtered;
    }

    // URL Rewrite ECMAScript regex'inde özel karakterleri kaçır (base64 '+' içerebilir).
    private static string EscapeRegex(string value)
    {
        var builder = new StringBuilder(value.Length + 4);
        foreach (var ch in value)
        {
            if (ch is '+' or '/' or '=' or '.' or '\\' or '*' or '?' or '^' or '$' or '(' or ')' or '[' or ']' or '{' or '}' or '|')
            {
                builder.Append('\\');
            }

            builder.Append(ch);
        }

        return builder.ToString();
    }
}
