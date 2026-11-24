using AdaPanel.Infrastructure.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AdaPanel.Infrastructure.Backup;

/// <summary>Acilista, onceki calismalardan kalan restore-test/test-* artiklarini siler.</summary>
public sealed class BackupTestRestoreCleanupWorker(IServiceScopeFactory scopes, ILogger<BackupTestRestoreCleanupWorker> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<JsonFileStore>();
            var removed = BackupTestRestore.CleanupStale(store.EnsureDirectory("restore-test"));
            if (removed > 0) logger.LogInformation("Removed {Count} stale test-restore folder(s).", removed);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Stale test-restore cleanup failed; it will be retried at the next test restore.");
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
