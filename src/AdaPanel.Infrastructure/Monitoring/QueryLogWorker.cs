using System.Runtime.Versioning;
using AdaPanel.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AdaPanel.Infrastructure.Monitoring;

/// <summary>
/// Veritabanı oturum listesini düzenli aralıklarla örnekler ve biten sorguları geçmişe yazar.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden arka planda:</b> geçmiş, panel açık olmasa da tutulmalı. "Dün gece sunucuyu ne yordu?"
/// sorusunun cevabı, o gece birinin tarayıcıyı açık bırakmış olmasına bağlı olamaz.
/// </para>
/// <para>
/// <b>Yük:</b> okunan şey motorun kendi oturum listesidir (MySQL/MariaDB'de <c>PROCESSLIST</c>,
/// PostgreSQL'de <c>pg_stat_activity</c>) — bellek içi bir görünüm, tablo taraması değil. Aynı uç
/// canlı ekran açıkken zaten saniyeler aralığıyla çağrılıyordu; bu döngü onun sunucu tarafındaki
/// karşılığı.
/// </para>
/// <para>
/// Kurulu olmayan motor için servis "installed: false" döner ve kayıt yapılmaz; boşuna denenmez.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class QueryLogWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<QueryLogWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Aralik = TimeSpan.FromSeconds(QueryLogService.SampleIntervalSeconds);

    /// <summary>Günlük temizliği her turda değil, saatte bir yapılır.</summary>
    private static readonly TimeSpan TemizlikAraligi = TimeSpan.FromHours(1);

    /// <summary>Örneklenen motorlar. "iis" DAHİL DEĞİL: bu geçmiş veritabanı sorguları içindir.</summary>
    private static readonly string[] Kaynaklar = ["mariadb", "mysql", "postgresql"];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Açılışta motorlar hazır olana kadar bekle.
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(45), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        var sonTemizlik = DateTime.UtcNow;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                sonTemizlik = await RunCycleAsync(sonTemizlik, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                // Tek bir tur hatası döngüyü DÜŞÜRMEZ: motor yeniden başlatılıyor olabilir.
                logger.LogWarning(exception, "Sorgu geçmişi örneklemesi başarısız oldu; bir sonraki turda tekrar denenecek.");
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

    internal async Task<DateTime> RunCycleAsync(DateTime lastCleanupUtc, CancellationToken stoppingToken)
    {
        stoppingToken.ThrowIfCancellationRequested();
        await using var scope = scopeFactory.CreateAsyncScope();
        var attribution = scope.ServiceProvider.GetRequiredService<IResourceAttributionService>();
        var queryLog = scope.ServiceProvider.GetRequiredService<IQueryLogService>();

        foreach (var kaynak in Kaynaklar)
        {
            stoppingToken.ThrowIfCancellationRequested();
            try
            {
                // The service caps the list at 200; TotalCount identifies partial samples.
                var snapshot = await attribution.GetConsumersAsync(kaynak, limit: 200, stoppingToken);
                stoppingToken.ThrowIfCancellationRequested();
                if (snapshot.Installed)
                    await queryLog.RecordSampleAsync(snapshot, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "{Source} sorgu geçmişi örneklenemedi; diğer kaynaklar devam edecek.", kaynak);
            }
        }

        stoppingToken.ThrowIfCancellationRequested();
        if (DateTime.UtcNow - lastCleanupUtc >= TemizlikAraligi)
        {
            var silinen = await queryLog.PurgeExpiredAsync(stoppingToken);
            stoppingToken.ThrowIfCancellationRequested();
            if (silinen > 0)
                logger.LogInformation("Sorgu geçmişinden {Adet} eski gün dosyası silindi.", silinen);
            return DateTime.UtcNow;
        }

        return lastCleanupUtc;
    }
}
