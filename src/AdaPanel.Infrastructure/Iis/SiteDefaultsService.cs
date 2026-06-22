using AdaPanel.Core.Dtos.Waf;
using AdaPanel.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace AdaPanel.Infrastructure.Iis;

public sealed class SiteDefaultsService(
    IDnsService dnsService,
    ISiteHostingSettingsService hostingSettingsService,
    IWafService wafService,
    ISslService sslService,
    IFtpService ftpService,
    IAspNetService aspNetService,
    IAppRuntimeService appRuntimeService,
    IReverseProxyService reverseProxyService,
    IPanelPreferencesService panelPreferencesService,
    ILogger<SiteDefaultsService> logger) : ISiteDefaultsService
{
    public async Task<AdaPanel.Core.Dtos.Sites.SiteDefaultsResultDto> EnsureDefaultsAsync(
        AdaPanel.Core.Dtos.Sites.SiteDto site,
        CancellationToken cancellationToken = default)
    {
        var readyFeatures = new List<string>();
        var failedFeatures = new List<string>();

        await TrackAsync("files", () =>
        {
            Directory.CreateDirectory(site.PhysicalPath);
            return Task.CompletedTask;
        });
        await TrackAsync("logs", () =>
        {
            Directory.CreateDirectory(Path.Combine(site.PhysicalPath, "App_Data", "AdaPanel", "logs"));
            Directory.CreateDirectory(Path.Combine(site.PhysicalPath, "App_Data", "AdaPanel", "temp"));
            return Task.CompletedTask;
        });
        await TrackAsync("backup", async () =>
        {
            var preferences = await panelPreferencesService.GetAsync(cancellationToken);
            var backupRoot = Environment.ExpandEnvironmentVariables(preferences.DefaultBackupRoot);
            Directory.CreateDirectory(Path.Combine(backupRoot, "sites", ToSafeDirectoryName(site.Name)));
        });
        // Site olusturulurken DNS bolgesini hazirla VE (Windows DNS varsa) otomatik yayinla;
        // boylece kullanici hicbir manuel adim olmadan dogrudan calisir bir DNS bolgesi alir.
        await TrackAsync("dns", async () => await dnsService.EnsureZonePublishedAsync(site.Name, cancellationToken));
        await TrackAsync("hosting", async () => _ = await hostingSettingsService.GetAsync(site.Name, cancellationToken));
        await TrackAsync("security", async () =>
        {
            var current = await wafService.GetSiteConfigurationAsync(site.Name, cancellationToken);
            if (!current.BasicRulesEnabled)
            {
                await wafService.EnableBasicRulesAsync(site.Name, cancellationToken);
            }

            if (!current.FolderAclApplied)
            {
                await wafService.ApplyFolderAclAsync(
                    site.Name,
                    new ApplyFolderAclRequest
                    {
                        Principal = $"IIS AppPool\\{site.ApplicationPoolName}",
                        Rights = "M"
                    },
                    cancellationToken);
            }
        });
        await TrackAsync("ssl", async () => _ = await sslService.GetSiteWorkspaceAsync(site.Name, cancellationToken));
        await TrackAsync("ftp", async () => _ = await ftpService.EnsureServerAsync(cancellationToken));
        await TrackAsync("aspnet", async () => _ = await aspNetService.GetSiteConfigurationAsync(site.Name, cancellationToken));
        await TrackAsync("runtime", async () => _ = await appRuntimeService.GetSiteConfigurationAsync(site.Name, cancellationToken));
        await TrackAsync("proxy", async () => _ = await reverseProxyService.GetSiteConfigurationAsync(site.Name, cancellationToken));

        return new AdaPanel.Core.Dtos.Sites.SiteDefaultsResultDto
        {
            SiteName = site.Name,
            ReadyFeatures = readyFeatures.ToArray(),
            FailedFeatures = failedFeatures.ToArray()
        };

        async Task TrackAsync(string feature, Func<Task> operation)
        {
            if (await TryEnsureAsync(feature, site.Name, operation))
            {
                readyFeatures.Add(feature);
            }
            else
            {
                failedFeatures.Add(feature);
            }
        }
    }

    private async Task<bool> TryEnsureAsync(string feature, string siteName, Func<Task> operation)
    {
        try
        {
            await operation();
            return true;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Default {Feature} configuration could not be prepared for site {SiteName}.", feature, siteName);
            return false;
        }
    }

    private static string ToSafeDirectoryName(string value)
    {
        var invalidCharacters = Path.GetInvalidFileNameChars();
        var safeName = new string(value
            .Trim()
            .Select(character => invalidCharacters.Contains(character) ? '-' : character)
            .ToArray());

        return string.IsNullOrWhiteSpace(safeName) ? "site" : safeName;
    }
}
