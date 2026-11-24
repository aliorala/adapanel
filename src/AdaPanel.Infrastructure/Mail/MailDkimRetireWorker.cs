using System.Runtime.Versioning;
using AdaPanel.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AdaPanel.Infrastructure.Mail;

/// <summary>
/// DKIM rotasyonundan sonra bekleme süresi dolan ESKİ anahtarı (ve DNS kaydını) kaldırır.
/// Hata sessiz değil: kayıt dosyada kalır ve sonraki turda yeniden denenir.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MailDkimRetireWorker(IServiceScopeFactory scopeFactory, ILogger<MailDkimRetireWorker> logger) : BackgroundService
{
    internal static readonly TimeSpan FirstDelay = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(FirstDelay, stoppingToken); } catch (OperationCanceledException) { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var removed = await scope.ServiceProvider.GetRequiredService<IMailService>().FinalizeDkimRotationsAsync(stoppingToken);
                if (removed > 0) logger.LogInformation("DKIM: {Count} eski anahtar bekleme suresi sonunda kaldirildi.", removed);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "DKIM eski anahtar temizligi tamamlanamadi; sonraki turda yeniden denenecek.");
            }

            try { await Task.Delay(Interval, stoppingToken); } catch (OperationCanceledException) { return; }
        }
    }
}
