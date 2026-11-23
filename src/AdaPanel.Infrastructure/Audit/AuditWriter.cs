using AdaPanel.Core.Entities;
using AdaPanel.Data.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AdaPanel.Infrastructure.Audit;

/// <summary>
/// Denetim kaydını veritabanına YAZAN tek gövde.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Services.AuditService"/>'ten ayrıldı çünkü artık iki farklı bağlamdan yazılıyor:
/// panelin normal servis grafiği ve SUNUCU KONSOLUNDAN çalışan kurtarma aracı. Konsol yolunda
/// <c>AuditService</c>'i ayağa kaldırmak, yalnız okuma/kapsam filtresi için var olan bir bağımlılık
/// zincirini (kapsam servisi → abonelik planları → IIS site servisi) kurtarma anında zorunlu kılardı;
/// panelin çalışmadığı durumda çalışması gereken bir aracın böyle bir zincire bağlanması kabul
/// edilemez.
/// </para>
/// <para>
/// Yazımın iki kuralı (sır temizliği ve başarısız kaydın İZLEMEDEN ÇIKARILMASI) burada, tek yerde
/// duruyor: kopyalansaydı, birinde uygulanıp diğerinde unutulması an meselesiydi.
/// </para>
/// <para>
/// HATA POLİTİKASI: denetim yazımı başarısız olursa, yazım SADECE denetim kaydını bekleyen
/// değişiklik olarak taşıyorsa hata YUTULUR (günlük + <see cref="AuditFailureMonitor"/> sayacı):
/// çağıran zaten yaptığı sistem değişikliğini (site, parola, kural...) denetim diski/kilidi yüzünden
/// "başarısız" saymamalı. Bağlamda başka bekleyen değişiklik varsa hata YUTULMAZ: aynı SaveChanges
/// onları da taşır, hata onlardan da gelmiş olabilir ve yutmak veri kaybını başarı gibi gösterirdi.
/// <c>propagateFailure: true</c> (yetkilendirme öncesi/güvenliğe kritik kayıt: sunucu konsolundan
/// parola kurtarma) her durumda atar: izsiz yüksek etkili müdahale kabul edilmez.
/// </para>
/// </remarks>
internal static class AuditWriter
{
    public static async Task WriteAsync(
        AdaPanelDbContext dbContext,
        string action,
        string resource,
        string details,
        Guid? userId,
        Guid? subscriptionUserId,
        string? ipAddress,
        CancellationToken cancellationToken = default,
        bool propagateFailure = false,
        ILogger? logger = null)
    {
        var hadOtherPendingChanges = dbContext.ChangeTracker.HasChanges();

        // SIR TEMİZLİĞİ + KIRPMA burada, yazma yolunun tek geçidinde. Gerekçe: AuditSecretRedactor.
        var entry = new AuditLog
        {
            UserId = userId,
            SubscriptionUserId = subscriptionUserId,
            Action = AuditSecretRedactor.SanitizeShortField(action),
            Resource = AuditSecretRedactor.SanitizeShortField(resource),
            Details = AuditSecretRedactor.SanitizeDetails(details),
            IpAddress = ipAddress,
            CreatedAtUtc = DateTime.UtcNow
        };

        dbContext.AuditLogs.Add(entry);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            // Yazılamayan kaydı İZLEMEDEN ÇIKAR. Aksi hâlde aynı DbContext'i kullanan bir sonraki
            // SaveChanges (asıl işlemin kendi kaydı) bu bozuk girişi tekrar denerdi; yani denetim
            // yazımının hatası, alakasız bir işlemi de düşürürdü.
            dbContext.Entry(entry).State = EntityState.Detached;

            if (propagateFailure || hadOtherPendingChanges || cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            AuditFailureMonitor.Record();
            logger?.LogError(
                exception,
                "Denetim kaydı yazılamadı (eylem {Action}); asıl işlem geri alınmadı. Toplam kayıp: {Count}.",
                entry.Action,
                AuditFailureMonitor.FailureCount);
        }
    }
}
