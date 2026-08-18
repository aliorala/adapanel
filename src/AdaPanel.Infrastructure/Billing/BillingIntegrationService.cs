using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Billing;
using AdaPanel.Core.Interfaces;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AdaPanel.Infrastructure.Billing;

/// <inheritdoc />
public sealed class BillingIntegrationService(
    JsonFileStore store,
    ICustomerScopeService customerScopes,
    ISubscriptionEnforcementService enforcement,
    AdaPanelDbContext dbContext,
    ILogger<BillingIntegrationService> logger) : IBillingIntegrationService
{
    /// <summary>
    /// Ayar dosyası. Veritabanı göçü (migration) GEREKTİRMEZ — panelde aynı kalıp otomasyon,
    /// brute-force ve müşteri kapsamı için de kullanılıyor.
    /// </summary>
    private const string SettingsPath = "config/billing.json";

    /// <summary>
    /// Saklanan çağrı sayısı. Sınırsız büyüseydi ayar dosyası her cron turunda şişer ve bir süre
    /// sonra okunması pahalı bir günlüğe dönüşürdü; denetim izinin kalıcı yeri IAuditService'tir.
    /// </summary>
    private const int MaxCallLogEntries = 20;

    /// <inheritdoc />
    public async Task<BillingSettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        var durum = await store.ReadAsync(SettingsPath, new BillingState(), cancellationToken);
        return Cevir(durum);
    }

    /// <inheritdoc />
    public async Task<BillingSettingsDto> UpdateSettingsAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        var durum = await store.UpdateAsync(
            SettingsPath,
            new BillingState(),
            mevcut =>
            {
                mevcut.Enabled = enabled;
                Ekle(mevcut, "settings", "ok", enabled ? "Entegrasyon acildi." : "Entegrasyon kapatildi.");
                return mevcut;
            },
            cancellationToken);

        return Cevir(durum);
    }

    /// <inheritdoc />
    public async Task<BillingUsageReportDto> BuildUsageReportAsync(CancellationToken cancellationToken = default)
    {
        await EnsureEnabledAsync("usage", cancellationToken);

        var abonelikler = await customerScopes.GetSubscriptionUserIdsAsync(cancellationToken);
        var hesaplar = new List<BillingUsageAccountDto>(abonelikler.Count);
        var olculemeyen = 0;

        foreach (var kimlik in abonelikler)
        {
            cancellationToken.ThrowIfCancellationRequested();

            SubscriptionSatiri satir;
            try
            {
                satir = await SatirOlusturAsync(kimlik, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // TEK abonelik ölçülemediğinde bütün rapor düşmemeli: WHMCS turu 500 alırsa
                // O SUNUCUDAKI HİÇBİR hizmetin kullanımı güncellenmez. Bir hesabın eksik kalması,
                // hepsinin eksik kalmasından iyidir.
                logger.LogWarning(exception, "Faturalama kullanim raporunda abonelik atlandi: {UserId}", kimlik);
                continue;
            }

            if (!satir.Hesap.BandwidthMeasured)
            {
                olculemeyen++;
            }

            hesaplar.Add(satir.Hesap);
        }

        var rapor = new BillingUsageReportDto
        {
            GeneratedAtUtc = DateTime.UtcNow,
            Accounts = hesaplar,
            UnmeasuredTrafficCount = olculemeyen
        };

        await KaydetAsync(
            "usage",
            "ok",
            $"{hesaplar.Count} abonelik raporlandi ({olculemeyen} tanesinin trafigi olculemedi).",
            hesaplar.Count,
            cancellationToken);

        return rapor;
    }

    /// <inheritdoc />
    public async Task<BillingTerminationCheckDto> CheckTerminationAsync(
        string? userName,
        string? domain,
        CancellationToken cancellationToken = default)
    {
        await EnsureEnabledAsync("terminate_check", cancellationToken);

        // Yalnız gerekli sütunlar çekilir; parola karması gibi alanların bu karara hiç girmemesi
        // gerekir ve belleğe alınmaması en iyisidir.
        var kayitlar = await dbContext.Users
            .AsNoTracking()
            .Select(kullanici => new { kullanici.Id, kullanici.UserName, kullanici.Role })
            .ToListAsync(cancellationToken);

        var adaylar = new List<BillingTerminationGuard.Candidate>(kayitlar.Count);
        foreach (var kayit in kayitlar)
        {
            // Kapsam YALNIZCA ada uyan hesap için okunur: her kullanıcı için kapsam dosyasına
            // gitmek, yüzlerce hesaplı bir panelde tek denetimi onlarca dosya okumasına çevirirdi.
            var siteler = string.Equals(kayit.UserName?.Trim(), (userName ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase)
                ? (await customerScopes.GetAssignedSitesAsync(kayit.Id, cancellationToken)).ToList()
                : [];

            adaylar.Add(new BillingTerminationGuard.Candidate(kayit.Id, kayit.UserName ?? string.Empty, kayit.Role, siteler));
        }

        var karar = BillingTerminationGuard.Evaluate(adaylar, userName, domain);

        await KaydetAsync(
            "terminate_check",
            karar.SafeToTerminate ? "ok" : "denied",
            $"'{karar.UserName}' icin silme denetimi: {karar.Reason}.",
            accountCount: null,
            cancellationToken);

        return karar;
    }

    /// <summary>
    /// Entegrasyon kapalıysa isteği reddeder.
    /// </summary>
    /// <remarks>
    /// Anahtar doğrulamasının YERİNE değil ÜSTÜNE binen ikinci kapı: sızmış geçerli bir anahtar
    /// varken yöneticinin elinde tek düğmelik bir kapatma olmalı. Servis içinde uygulanıyor ki
    /// yeni bir uç eklendiğinde denetimi çağırmayı unutmak mümkün olmasın.
    /// </remarks>
    private async Task EnsureEnabledAsync(string action, CancellationToken cancellationToken)
    {
        var durum = await store.ReadAsync(SettingsPath, new BillingState(), cancellationToken);
        if (durum.Enabled)
        {
            return;
        }

        await KaydetAsync(action, "denied", "Entegrasyon kapali oldugu icin istek reddedildi.", null, cancellationToken);
        throw new AppException("billing.disabled", 403, 403);
    }

    private async Task<SubscriptionSatiri> SatirOlusturAsync(Guid userId, CancellationToken cancellationToken)
    {
        var kullanim = await enforcement.GetUsageAsync(userId, cancellationToken);
        var siteler = await customerScopes.GetAssignedSitesAsync(userId, cancellationToken);

        return new SubscriptionSatiri(new BillingUsageAccountDto
        {
            UserId = kullanim.UserId,
            UserName = kullanim.UserName,
            Domains = siteler.ToList(),
            PlanId = kullanim.PlanId,
            PlanName = kullanim.PlanName,
            DiskUsageMb = kullanim.DiskUsageMb < 0 ? 0 : kullanim.DiskUsageMb,
            DiskLimitMb = BillingUnits.NormalizeLimitMb(kullanim.MaxDiskMb),
            BandwidthUsageMb = BillingUnits.BytesToMegabytes(kullanim.TrafficBytesThisMonth),
            BandwidthLimitMb = BillingUnits.GigabytesToMegabytes(kullanim.TrafficLimitGb),
            BandwidthMeasured = kullanim.TrafficMeasured,
            IsSuspended = kullanim.IsSuspended
        });
    }

    private async Task KaydetAsync(
        string action,
        string outcome,
        string detail,
        int? accountCount,
        CancellationToken cancellationToken)
    {
        try
        {
            await store.UpdateAsync(
                SettingsPath,
                new BillingState(),
                mevcut =>
                {
                    Ekle(mevcut, action, outcome, detail);
                    if (outcome == "ok")
                    {
                        mevcut.LastCallAtUtc = DateTime.UtcNow;
                        if (accountCount is { } sayi)
                        {
                            mevcut.LastAccountCount = sayi;
                        }
                    }

                    return mevcut;
                },
                cancellationToken);
        }
        catch (Exception exception)
        {
            // Günlük yazamamak, ASIL işi (kullanım raporu) düşürmemeli. Denetim izinin kalıcı
            // yeri zaten IAuditService; burası ayar ekranındaki kolaylık listesidir.
            logger.LogWarning(exception, "Faturalama cagri gunlugu yazilamadi.");
        }
    }

    private static void Ekle(BillingState state, string action, string outcome, string detail)
    {
        state.RecentCalls.Insert(0, new BillingCallLogEntryDto
        {
            AtUtc = DateTime.UtcNow,
            Action = action,
            Outcome = outcome,
            Detail = detail
        });

        if (state.RecentCalls.Count > MaxCallLogEntries)
        {
            state.RecentCalls.RemoveRange(MaxCallLogEntries, state.RecentCalls.Count - MaxCallLogEntries);
        }
    }

    private static BillingSettingsDto Cevir(BillingState state) => new()
    {
        Enabled = state.Enabled,
        LastCallAtUtc = state.LastCallAtUtc,
        LastAccountCount = state.LastAccountCount,
        RecentCalls = state.RecentCalls
    };

    private readonly record struct SubscriptionSatiri(BillingUsageAccountDto Hesap);

    /// <summary>
    /// Diskteki biçim. Varsayılan KAPALI: entegrasyon uçları hesap açıp silebildiği için,
    /// kurulumdan sonra kimse açmadan çalışır durumda olmamalıdır.
    /// </summary>
    private sealed class BillingState
    {
        public bool Enabled { get; set; }

        public DateTime? LastCallAtUtc { get; set; }

        public int LastAccountCount { get; set; }

        public List<BillingCallLogEntryDto> RecentCalls { get; set; } = [];
    }
}
