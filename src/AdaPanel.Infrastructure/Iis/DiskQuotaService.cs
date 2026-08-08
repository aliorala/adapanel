using System.Text.Json;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Support;

namespace AdaPanel.Infrastructure.Iis;

/// <summary>
/// Windows File Server Resource Manager (FSRM) kotalarını kullanarak site
/// uygulama köküne disk kullanım sınırı uygular. FSRM rolü kurulu değilse
/// servis bunu durum olarak raporlar, hata fırlatmaz.
/// </summary>
public sealed class DiskQuotaService(IIisSiteService iisSiteService) : IDiskQuotaService
{
    public async Task<SiteDiskQuotaDto> GetQuotaAsync(string siteName, CancellationToken cancellationToken = default)
    {
        var path = await ResolveSitePathAsync(siteName, cancellationToken);

        // $$ ile çift süslü interpolasyon: PowerShell'in tek süslü parantezleri olduğu gibi kalır.
        var script = $$"""
            $path = '{{EscapePowerShellLiteral(path)}}'
            if (-not (Get-Command Get-FsrmQuota -ErrorAction SilentlyContinue)) {
              [pscustomobject]@{ fsrmAvailable = $false; quotaEnabled = $false } | ConvertTo-Json
              return
            }
            $quota = Get-FsrmQuota -Path $path -ErrorAction SilentlyContinue
            if (-not $quota) {
              [pscustomobject]@{ fsrmAvailable = $true; quotaEnabled = $false } | ConvertTo-Json
            } else {
              [pscustomobject]@{
                fsrmAvailable = $true
                quotaEnabled = $true
                limitBytes = [int64]$quota.Size
                usedBytes = [int64]$quota.Usage
                softLimit = [bool]$quota.SoftLimit
              } | ConvertTo-Json
            }
            """;

        var result = await ProcessRunner.RunPowerShellAsync(script, cancellationToken);
        if (!result.Success)
        {
            throw new AppException("quota.read_failed", 500, 500, result.StandardError);
        }

        return MapResult(siteName, path, result.StandardOutput);
    }

    public async Task<SiteDiskQuotaDto> SetQuotaAsync(string siteName, SetSiteDiskQuotaRequest request, CancellationToken cancellationToken = default)
    {
        var path = await ResolveSitePathAsync(siteName, cancellationToken);
        var sizeBytes = Math.Max(1, request.LimitMegabytes) * 1024L * 1024L;
        var softSwitch = request.SoftLimit ? "$true" : "$false";

        var script = $$"""
            $path = '{{EscapePowerShellLiteral(path)}}'
            if (-not (Get-Command New-FsrmQuota -ErrorAction SilentlyContinue)) { exit 21 }
            if (-not (Test-Path -LiteralPath $path)) { New-Item -ItemType Directory -Path $path -Force | Out-Null }
            if (Get-FsrmQuota -Path $path -ErrorAction SilentlyContinue) {
              Set-FsrmQuota -Path $path -Size {{sizeBytes}} -SoftLimit:{{softSwitch}} | Out-Null
            } else {
              New-FsrmQuota -Path $path -Size {{sizeBytes}} -SoftLimit:{{softSwitch}} | Out-Null
            }
            """;

        var result = await ProcessRunner.RunPowerShellAsync(script, cancellationToken);
        if (result.ExitCode == 21)
        {
            throw new AppException("quota.fsrm_unavailable", 409, 409);
        }

        if (!result.Success)
        {
            throw new AppException("quota.write_failed", 500, 500, result.StandardError);
        }

        return await GetQuotaAsync(siteName, cancellationToken);
    }

    public async Task<SiteDiskQuotaDto> RemoveQuotaAsync(string siteName, CancellationToken cancellationToken = default)
    {
        var path = await ResolveSitePathAsync(siteName, cancellationToken);

        var script = $$"""
            $path = '{{EscapePowerShellLiteral(path)}}'
            if (Get-Command Remove-FsrmQuota -ErrorAction SilentlyContinue) {
              Remove-FsrmQuota -Path $path -Confirm:$false -ErrorAction SilentlyContinue | Out-Null
            }
            """;

        var result = await ProcessRunner.RunPowerShellAsync(script, cancellationToken);
        if (!result.Success)
        {
            throw new AppException("quota.write_failed", 500, 500, result.StandardError);
        }

        return await GetQuotaAsync(siteName, cancellationToken);
    }

    private async Task<string> ResolveSitePathAsync(string siteName, CancellationToken cancellationToken)
    {
        var site = await iisSiteService.GetSiteAsync(siteName, cancellationToken);
        var path = string.IsNullOrWhiteSpace(site.ApplicationRootPath) ? site.PhysicalPath : site.ApplicationRootPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new AppException("quota.path_not_found", 404, 404);
        }

        return path;
    }

    private static SiteDiskQuotaDto MapResult(string siteName, string path, string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new SiteDiskQuotaDto { SiteName = siteName, Path = path };
        }

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        return new SiteDiskQuotaDto
        {
            SiteName = siteName,
            Path = path,
            FsrmAvailable = GetBool(root, "fsrmAvailable"),
            QuotaEnabled = GetBool(root, "quotaEnabled"),
            LimitBytes = GetLong(root, "limitBytes"),
            UsedBytes = GetLong(root, "usedBytes"),
            SoftLimit = GetBool(root, "softLimit")
        };
    }

    private static bool GetBool(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static long GetLong(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.TryGetInt64(out var parsed) ? parsed : 0;

    private static string EscapePowerShellLiteral(string value) => value.Replace("'", "''");

    /// <inheritdoc />
    public async Task<SiteDiskQuotaDto> InstallFsrmAsync(string siteName, CancellationToken cancellationToken = default)
    {
        // Sunucu sürümünde rol, istemci sürümünde karşılığı YOK. İkisini ayırt edip net konuşuyoruz:
        // "kurulamadı" demek, kullanıcıyı olmayan bir çözümü aramaya iter.
        const string script = """
            if (-not (Get-Command Install-WindowsFeature -ErrorAction SilentlyContinue)) {
              Write-Error 'FSRM yalnizca Windows Server surumlerinde bulunur.'
              exit 2
            }
            Install-WindowsFeature -Name FS-Resource-Manager -IncludeManagementTools -ErrorAction Stop | Out-Null
            if (-not (Get-Command Get-FsrmQuota -ErrorAction SilentlyContinue)) {
              Write-Error 'FSRM kuruldu ama komutlari yuklenmedi; sunucuyu yeniden baslatmak gerekebilir.'
              exit 3
            }
            """;

        var result = await ProcessRunner.RunPowerShellAsync(script, cancellationToken);
        if (!result.Success)
        {
            // ProcessRunner istisna FIRLATMAZ, sonuç döndürür — çıkış kodu okunmazsa hata sessizce
            // "başarı" olurdu. (Bu depoda tam olarak o hata bir kez yaşandı.)
            throw new AppException(
                "quota.fsrm_install_failed",
                500,
                500,
                string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError);
        }

        // Kurulum sonrası durumu TAZE oku: kullanıcı kartın gerçekten açıldığını görsün.
        return await GetQuotaAsync(siteName, cancellationToken);
    }

}
