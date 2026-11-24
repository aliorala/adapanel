using AdaPanel.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace AdaPanel.Infrastructure.Php;

public sealed class PhpSiteLogMaintenance(IIisSiteService sites, IConcurrencyLock gate, ILogger<PhpSiteLogMaintenance> logger)
{
    public async Task RunAsync(CancellationToken token)
    {
        var config = new PhpSiteLogConfiguration(new PhpSiteLogHttpProtection(gate).EnsureAsync);
        foreach (var site in await sites.GetSitesAsync(token))
        {
            try
            {
                var plan = await config.InspectAsync(site, token);
                if (plan.Managed && plan.ConfigurationError is null)
                    await PhpSiteLogRotation.ArchiveIfNeededAsync(plan, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception error) { logger.LogWarning(error, "PHP günlük arşivleme tamamlanamadı: {Site}", site.Name); }
        }
    }
}
