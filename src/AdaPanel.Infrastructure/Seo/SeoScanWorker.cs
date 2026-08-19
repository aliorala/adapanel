using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AdaPanel.Infrastructure.Seo;

/// <summary>
/// SEO tarama kuyruğunun TEK tüketicisi (kalıp: <c>AuditRetentionWorker</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden arka planda:</b> tarama dakikalarca sürer. Denetleyicide çalıştırılsaydı istek o
/// süre boyunca açık kalır, ters vekil/tarayıcı zaman aşımına düşer ve kullanıcı işin bitip
/// bitmediğini asla öğrenemezdi.
/// </para>
/// <para>
/// <b>Neden tek tüketici (sıralı):</b> tarama, taradığı siteyle AYNI sunucudadır. Paralel
/// taramalar müşterilerin sitelerini panelin kendisi yavaşlatırdı. Sıra beklemek arayüzde
/// görünür (<c>queueLength</c>).
/// </para>
/// <para>
/// <b>Neden kapanışta kuyruk boşaltılmıyor:</b> panel kapanırken süren tarama iptal edilir ve
/// kuyruktakiler düşer. Yarım bir tarama diskte "sürüyor" izi bırakmaz; kullanıcı yeniden
/// başlatabilir. Kalıcı kuyruk, kapanışta kaybolan işin sahte bir kaydını üretirdi.
/// </para>
/// </remarks>
public sealed class SeoScanWorker(
    SeoScanQueue queue,
    IServiceScopeFactory scopeFactory,
    ILogger<SeoScanWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var job in queue.ReadAllAsync(stoppingToken))
            {
                using var jobCancellation = queue.TryBeginRun(job);
                if (jobCancellation is null)
                {
                    // Sırada beklerken iptal edilmiş: hiç başlatma.
                    continue;
                }

                using var linked = CancellationTokenSource.CreateLinkedTokenSource(jobCancellation.Token, stoppingToken);
                try
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    var runner = scope.ServiceProvider.GetRequiredService<SeoScanRunner>();
                    await runner.RunAsync(job, linked.Token);
                }
                catch (Exception exception)
                {
                    // Tek işin hatası TÜKETİCİYİ DÜŞÜRMEZ; düşseydi panel yeniden başlatılana kadar
                    // hiçbir tarama çalışmazdı ve bunu kimse fark etmezdi.
                    logger.LogWarning(exception, "SEO tarama isi calistirilamadi: {Site}", job.SiteName);
                }
                finally
                {
                    queue.Finish(job.SiteName);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Panel kapanıyor.
        }
    }
}
