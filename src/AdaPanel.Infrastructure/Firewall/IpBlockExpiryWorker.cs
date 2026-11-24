using AdaPanel.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AdaPanel.Infrastructure.Firewall;

/// <summary>Suresi dolan IP engellerini her dakika kaldirir (kural + kayit) ve her kaldirmayi denetim kaydina yazar.</summary>
public sealed class IpBlockExpiryWorker(IServiceScopeFactory scopes, ILogger<IpBlockExpiryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                await SweepOnceAsync(scope.ServiceProvider.GetRequiredService<IIpBlockService>(),
                    scope.ServiceProvider.GetRequiredService<IAuditService>(), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogWarning(exception, "IP block expiry sweep will be retried."); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    internal static async Task SweepOnceAsync(IIpBlockService service, IAuditService audit, CancellationToken cancellationToken)
    {
        foreach (var entry in await service.SweepExpiredAsync(cancellationToken))
        {
            await audit.WriteAsync("firewall.ipblock.expired", $"firewall:{entry.RuleName}",
                $"target={entry.Target}; expired={entry.ExpiresAtUtc:O}; rule removed and verified.", null, null, cancellationToken);
        }
    }
}
