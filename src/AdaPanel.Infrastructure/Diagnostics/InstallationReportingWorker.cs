using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AdaPanel.Infrastructure.Diagnostics;

public sealed class InstallationReportingWorker(InstallationReportingService reporting,
    ILogger<InstallationReportingWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(Random.Shared.Next(30, 120)), stoppingToken);
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
            do
            {
                try { await reporting.ReportIfDueAsync(stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                catch (Exception) { logger.LogWarning("Installation reporting could not complete; panel operation is unaffected."); }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
