using AdaPanel.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AdaPanel.Infrastructure.Audit;

/// <summary>
/// Saklama süresi dolmuş denetim kayıtlarını düzenli aralıklarla siler.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden gerekli (ölçüldü):</b> panelde denetim kayıtlarını silen HİÇBİR kod yoktu — tabloya
/// yalnızca yazılıyordu. Yani kayıtlar sonsuza kadar birikiyor; panelin SQLite dosyası sınırsız
/// büyüyor ve KVKK/GDPR'ın "gerektiğinden uzun saklama" yasağıyla çelişiyordu.
/// </para>
/// <para>
/// <b>Neden arka planda:</b> temizlik "yönetici ekrana bakınca" değil, süre dolduğunda olmalı.
/// Elle tetiklenen bir düğmeye bırakılan saklama politikası, politika değil temennidir.
/// </para>
/// <para>
/// <b>Neden günde bir:</b> politika gün çözünürlüğündedir; daha sık koşmak aynı sonucu üretip
/// bedava disk/CPU harcardı.
/// </para>
/// <para>
/// <b>BAĞLANTI (devralana not):</b> bu sınıf kasten kendi kendini kaydetmez. Devreye almak için
/// <c>ServiceCollectionExtensions.AddInfrastructure</c> içine tek satır yeter:
/// <c>services.AddHostedService&lt;Audit.AuditRetentionWorker&gt;();</c>
/// Kayıt yapılmadığı sürece temizlik yalnızca <c>POST /api/v1/audit-logs/purge</c> ile elle çalışır.
/// </para>
/// </remarks>
public sealed class AuditRetentionWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<AuditRetentionWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Aralik = TimeSpan.FromHours(24);

    /// <summary>Açılışta veritabanı göçü (migration) bitene kadar bekleme payı.</summary>
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
                var auditService = scope.ServiceProvider.GetRequiredService<IAuditService>();

                var silinen = await auditService.PurgeAsync(stoppingToken);
                if (silinen > 0)
                {
                    logger.LogInformation("Saklama süresi dolan {Count} denetim kaydı silindi.", silinen);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                // Tek turun hatası döngüyü DÜŞÜRMEZ; bir sonraki turda tekrar denenir.
                logger.LogWarning(exception, "Denetim kaydı temizliği başarısız oldu; bir sonraki turda tekrar denenecek.");
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
