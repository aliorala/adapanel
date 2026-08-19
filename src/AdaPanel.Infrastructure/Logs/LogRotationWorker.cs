using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AdaPanel.Infrastructure.Logs;

/// <summary>
/// Zamanı gelen log döndürme turunu arka planda çalıştırır.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden yeni bir zamanlayıcı YOK:</b> panelde arka plan işi zaten <see cref="BackgroundService"/>
/// ile yazılıyor (<c>AuditRetentionWorker</c>, <c>AutomationWorker</c>, <c>QueryLogWorker</c>). Bu
/// sınıf <c>AuditRetentionWorker</c> ile aynı kalıptır: açılış gecikmesi, tur, turun hatasını yutup
/// döngüyü yaşatma.
/// </para>
/// <para>
/// <b>Neden saatte bir uyanıp "zamanı geldi mi" diye soruyor:</b> tur aralığı bir AYARDIR
/// (varsayılan 24 saat). İşçi doğrudan o kadar uyusaydı, yönetici aralığı değiştirdiğinde yeni
/// değer bir sonraki uyanışa kadar geçerli olmazdı ve panel yeniden başlatıldığında sayaç sıfırdan
/// başlardı. Zamanı geldi mi kararı diskteki <c>LastRunUtc</c>'ye bakar
/// (<c>LogRotationService.IsDue</c>), yani yeniden başlatma turu ne öne alır ne erteler.
/// </para>
/// <para>
/// <b>Neden açılışta beş dakika bekliyor:</b> panel yeni açılmışken IIS ve veri kökü henüz hazır
/// olmayabilir; ilk dakikalarda dosya silen bir iş başlatmak için hiçbir sebep yok.
/// </para>
/// </remarks>
public sealed class LogRotationWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<LogRotationWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Aralik = TimeSpan.FromHours(1);

    /// <summary>Açılışta IIS/veri kökü hazır olana kadar bekleme payı.</summary>
    private static readonly TimeSpan IlkGecikme = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!await BeklAsync(IlkGecikme, stoppingToken))
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var rotation = scope.ServiceProvider.GetRequiredService<ILogRotationService>();

                if (await rotation.RunDueAsync(stoppingToken))
                {
                    logger.LogInformation("Log dondurme turu tamamlandi.");
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                // Tek turun hatası döngüyü DÜŞÜRMEZ; bir sonraki turda tekrar denenir.
                logger.LogWarning(exception, "Log dondurme turu basarisiz oldu; bir sonraki turda tekrar denenecek.");
            }

            if (!await BeklAsync(Aralik, stoppingToken))
            {
                return;
            }
        }
    }

    /// <summary>Bekler; kapanış istendiyse false döner.</summary>
    private static async Task<bool> BeklAsync(TimeSpan sure, CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(sure, stoppingToken);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
