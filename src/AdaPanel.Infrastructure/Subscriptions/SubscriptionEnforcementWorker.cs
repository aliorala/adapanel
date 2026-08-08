using System.Runtime.Versioning;
using AdaPanel.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AdaPanel.Infrastructure.Subscriptions;

/// <summary>
/// Abonelik kurallarını düzenli aralıklarla uygular: trafik kotası dolanı ve süresi bitmiş olanı
/// askıya alır, sebebi ortadan kalkanı geri açar.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden arka planda:</b> kota "yönetici bakınca" değil, dolduğunda işlemeli. Panel açık
/// olmasa da sunucudaki siteler yayında; kuralı uygulayan taraf da o yüzden sunucuda olmalı.
/// </para>
/// <para>
/// <b>Neden saatte bir:</b> trafik ölçümü sitelerin IIS erişim günlüklerini tarar; bu işi
/// dakikada bir yapmak, kazancı olmayan sürekli bir disk yükü demektir. Kota aşımının bir saat
/// içinde yakalanması barındırma pratiğinde fazlasıyla yeterlidir.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class SubscriptionEnforcementWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<SubscriptionEnforcementWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Aralik = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Açılışta IIS ve günlükler hazır olana kadar bekle.
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
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
                var enforcement = scope.ServiceProvider.GetRequiredService<ISubscriptionEnforcementService>();

                var degisenler = await enforcement.EnforceAllAsync(stoppingToken);
                foreach (var degisim in degisenler)
                {
                    logger.LogInformation(
                        "Abonelik durumu değişti: {UserName} → {Action} ({Reason})",
                        degisim.UserName,
                        degisim.Action,
                        degisim.Reason);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                // Tek turun hatası döngüyü DÜŞÜRMEZ.
                logger.LogWarning(exception, "Abonelik denetim turu başarısız oldu; bir sonraki turda tekrar denenecek.");
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
