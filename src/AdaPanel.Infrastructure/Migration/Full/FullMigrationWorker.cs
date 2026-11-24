using AdaPanel.Core.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AdaPanel.Infrastructure.Migration.Full;

/// <summary>Request-independent worker. Persisted running jobs are interrupted, never replayed.</summary>
public sealed class FullMigrationWorker(IServiceScopeFactory scopeFactory, ILogger<FullMigrationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let host startup complete. Native IIS availability may lag service startup.
        await Task.Yield();
        var recovered = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            var worked = false;
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var acquisition = scope.ServiceProvider.GetRequiredService<MigrationAcquisitionService>();
                var target = scope.ServiceProvider.GetRequiredService<FullMigrationCoordinator>();
                if (!recovered)
                {
                    await acquisition.RecoverInterruptedAsync(stoppingToken);
                    await target.RecoverInterruptedAsync(stoppingToken);
                    recovered = true;
                }
                // One target mutation/export at a time on this panel. A long source acquisition
                // cannot race another acquisition after browser reload or connection loss.
                worked = await target.RunNextAsync(stoppingToken);
                if (!worked) worked = await acquisition.RunNextAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception exception)
            {
                // Do not log raw source/native exception text; it may contain credentials or SQL.
                var code = exception is AppException app && app.MessageKey.StartsWith("migration.", StringComparison.Ordinal)
                    && app.MessageKey.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_')
                    ? app.MessageKey : "migration.worker_unavailable";
                logger.LogWarning("Migration worker paused after {ErrorCode}; pending work remains recorded.", code);
                try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            }
            if (!worked)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            }
        }
    }
}
