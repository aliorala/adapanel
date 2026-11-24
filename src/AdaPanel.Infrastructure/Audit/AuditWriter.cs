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
/// HATA POLİTİKASI (bilinçli karar): VARSAYILAN = hata ATILIR. Mevcut çağıranlar denetim yazımını
/// işlemin ATOMİK parçası sayar ve buna dayanır (2FA kurtarma kodları, SSL içe aktarma, durum
/// değiştiren her uç: AuditCoverageTests, AuthTwoFactorLifecycleTests, SslImportLifecycleTests);
/// varsayılanı yutmaya çevirmek bu sözleşmeyi 40+ çağrı yerinde sessizce bozardı. Her başarısızlık
/// ayrıca günlüğe yazılır ve <see cref="AuditFailureMonitor"/> sayacını artırır (görünür boşluk).
/// <c>propagateFailure: false</c> (en-iyi-çaba) yalnız, sistem değişikliği ZATEN yapılmış ve geri
/// alınamayan çağıranlar için açık bir seçimdir: bekleyen başka değişiklik yoksa hata yutulur (sayaç
/// artar); başka bekleyen değişiklik varsa yine atılır, çünkü aynı SaveChanges onları da taşır ve
/// yutmak veri kaybını başarı gibi gösterirdi.
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
        bool propagateFailure = true,
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

            if (!cancellationToken.IsCancellationRequested)
            {
                AuditFailureMonitor.Record();
                logger?.LogError(
                    exception,
                    "Denetim kaydı yazılamadı (eylem {Action}). Toplam başarısız yazım: {Count}.",
                    entry.Action,
                    AuditFailureMonitor.FailureCount);
            }

            if (propagateFailure || hadOtherPendingChanges || cancellationToken.IsCancellationRequested)
            {
                throw;
            }
        }
    }
}
