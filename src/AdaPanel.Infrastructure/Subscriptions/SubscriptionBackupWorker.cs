using System.Runtime.Versioning;
using AdaPanel.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AdaPanel.Infrastructure.Subscriptions;

/// <summary>
/// Abonelik başına yedek programlarını çalıştıran arka plan işçisi.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden AutomationWorker'a eklenmedi:</b> sunucu geneli otomasyon turu tek bir kilidin
/// içinde saatlerce sürebilir. Abonelik yedekleri oraya eklenseydi, 50 müşterili bir sunucuda
/// tek bir turun içinde 50 arşiv üretilirdi ve o kilit boyunca "şimdi yedekle" isteği de,
/// SSL yenilemesi de beklerdi. Ayrı işçi + ayrı kuyruk, birinin gecikmesinin diğerini
/// durdurmamasını sağlar.
/// </para>
/// <para>
/// <b>Neden on dakikada bir:</b> en sık program saatliktir; on dakikalık çözünürlük, bir turu
/// en fazla on dakika geciktirir ve buna karşılık boşta CPU harcamaz. Aralığın kendisi
/// programın <c>IntervalHours</c> değeriyle uygulanır, bu tur sıklığıyla değil.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class SubscriptionBackupWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<SubscriptionBackupWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Aralik = TimeSpan.FromMinutes(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Açılışta IIS, veritabanı motorları ve mail servisi hazır olana kadar bekle: ilk
        // dakikalarda alınan bir yedek, henüz başlamamış bir servis yüzünden eksik çıkardı.
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(3), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var schedules = scope.ServiceProvider.GetRequiredService<ISubscriptionBackupScheduleService>();
                await schedules.RunDueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                // Tek turun hatası döngüyü DÜŞÜRMEZ; bir sonraki turda tekrar denenir.
                logger.LogWarning(exception, "Abonelik yedek turu basarisiz oldu; bir sonraki turda tekrar denenecek.");
            }

            try
            {
                await Task.Delay(Aralik, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
