using System.Runtime.Versioning;
using AdaPanel.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AdaPanel.Infrastructure.Automation;

/// <summary>
/// Panel içi zamanlanmış otomasyon motoru. Dakikada bir uyanır ve zamanı gelmiş
/// otomatik yedekleme / SSL yenileme job'larını çalıştırır. <see cref="IAutomationService"/>
/// scoped olduğu için her tetiklemede yeni bir DI scope açılır.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AutomationWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<AutomationWorker> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Panel açılışında DB/IIS hazır olana kadar kısa bir gecikme.
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        using var timer = new PeriodicTimer(PollInterval);

        do
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var automation = scope.ServiceProvider.GetRequiredService<IAutomationService>();
                await automation.RunDueJobsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                // Bir tur başarısız olsa bile zamanlayıcı yaşamaya devam etmeli.
                logger.LogError(exception, "Otomasyon turu beklenmedik bir hatayla sonuçlandı.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
