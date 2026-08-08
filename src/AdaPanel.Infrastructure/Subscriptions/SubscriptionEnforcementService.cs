using AdaPanel.Core.Dtos.Users;
using AdaPanel.Core.Interfaces;
using AdaPanel.Data.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AdaPanel.Infrastructure.Subscriptions;

/// <inheritdoc />
public sealed class SubscriptionEnforcementService(
    ICustomerScopeService customerScopes,
    IWebStatisticsService webStatistics,
    IIisSiteService iisSiteService,
    IAuditService auditService,
    AdaPanelDbContext dbContext,
    ILogger<SubscriptionEnforcementService> logger) : ISubscriptionEnforcementService
{
    /// <inheritdoc />
    public async Task<SubscriptionUsageDto> GetUsageAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var scope = await customerScopes.GetScopeAsync(userId, cancellationToken);
        var kullanici = await dbContext.Users.FirstOrDefaultAsync(item => item.Id == userId, cancellationToken);

        var (bayt, olculebildi) = await TrafikOlcAsync(scope.Sites, cancellationToken);

        return new SubscriptionUsageDto
        {
            UserId = userId,
            UserName = kullanici?.UserName ?? string.Empty,
            PlanId = scope.PlanId,
            PlanName = scope.PlanName,
            TrafficBytesThisMonth = bayt,
            TrafficLimitGb = scope.MaxTrafficGbPerMonth,
            TrafficMeasured = olculebildi,
            ExpiresAtUtc = scope.ExpiresAtUtc,
            IsExpired = scope.IsExpired,
            IsSuspended = scope.IsSuspended,
            SuspendedAtUtc = scope.SuspendedAtUtc,
            SuspendReason = scope.SuspendReason,
            SiteCount = scope.Sites.Count,
            MaxSites = scope.MaxSites,
            DiskUsageMb = await customerScopes.GetDiskUsageMbAsync(userId, cancellationToken),
            MaxDiskMb = scope.MaxDiskMb
        };
    }

    /// <summary>
    /// Aboneliğin BU TAKVİM AYINDAKİ trafiğini ölçer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Kaynak, sitelerin IIS erişim günlükleridir (gönderilen bayt). Barındırma sektöründe sayım
    /// takvim ayı üzerinden yapılır ve ayın 1'inde sıfırlanır; panel de öyle yapıyor.
    /// </para>
    /// <para>
    /// <b>Ölçülemeyen site, sıfır SAYILMAZ.</b> Bir sitenin günlüğü okunamıyorsa (IIS günlük
    /// tutmuyor, klasör erişilemez) bunu "trafik yok" saymak, kotayı sessizce devre dışı bırakmak
    /// olurdu. O yüzden ölçüm bayrağı ayrıca döner ve ölçülemeyen abonelikte kota UYGULANMAZ.
    /// </para>
    /// </remarks>
    private async Task<(long Bytes, bool Measured)> TrafikOlcAsync(
        IReadOnlyCollection<string> siteler,
        CancellationToken cancellationToken)
    {
        if (siteler.Count == 0)
        {
            // Sitesi olmayan aboneliğin trafiği gerçekten sıfırdır — bu ölçülmüş bir sıfırdır.
            return (0, true);
        }

        // Ayın başından bugüne kaç gün geçti (dahil).
        var bugun = DateTime.UtcNow.Date;
        var gun = bugun.Day;

        long toplam = 0;
        var hepsiOlculdu = true;

        foreach (var site in siteler)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var istatistik = await webStatistics.GetSiteStatisticsAsync(site, gun, cancellationToken);
                toplam += istatistik.TotalBytes;
            }
            catch (Exception exception)
            {
                hepsiOlculdu = false;
                logger.LogWarning(exception, "Abonelik trafiği okunamadı: {Site}", site);
            }
        }

        return (toplam, hepsiOlculdu);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyCollection<SubscriptionEnforcementChangeDto>> EnforceAllAsync(CancellationToken cancellationToken = default)
    {
        var degisenler = new List<SubscriptionEnforcementChangeDto>();

        foreach (var userId in await customerScopes.GetSubscriptionUserIdsAsync(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var degisim = await AboneligiDenetleAsync(userId, cancellationToken);
                if (degisim is not null)
                {
                    degisenler.Add(degisim);
                }
            }
            catch (Exception exception)
            {
                // Tek bir aboneliğin hatası TURU DÜŞÜRMEZ; diğerleri denetlenmeye devam eder.
                logger.LogWarning(exception, "Abonelik denetimi başarısız: {UserId}", userId);
            }
        }

        return degisenler;
    }

    private async Task<SubscriptionEnforcementChangeDto?> AboneligiDenetleAsync(Guid userId, CancellationToken cancellationToken)
    {
        var scope = await customerScopes.GetScopeAsync(userId, cancellationToken);

        // 1) SÜRE. Ölçüm gerektirmez, en kesin kural.
        if (scope.IsExpired && !scope.IsSuspended)
        {
            await SuspendAsync(userId, "expired", cancellationToken);
            return await DegisimAsync(userId, "suspended", "expired", cancellationToken);
        }

        // 2) TRAFİK. Yalnız kota tanımlıysa VE ölçebildiysek.
        if (scope.MaxTrafficGbPerMonth > 0)
        {
            var (bayt, olculdu) = await TrafikOlcAsync(scope.Sites, cancellationToken);
            var sinir = (long)scope.MaxTrafficGbPerMonth * 1024 * 1024 * 1024;

            if (olculdu && bayt >= sinir && !scope.IsSuspended)
            {
                await SuspendAsync(userId, "traffic", cancellationToken);
                return await DegisimAsync(userId, "suspended", "traffic", cancellationToken);
            }

            // TRAFİKTEN askıya alınmış bir abonelik, YENİ AY başlayınca kendiliğinden açılır.
            // Ayın sıfırlanmasını beklemek yerine ölçümün kendisine bakıyoruz: kota altına
            // düşmüşse sebep ortadan kalkmıştır.
            if (scope.IsSuspended && scope.SuspendReason == "traffic" && olculdu && bayt < sinir && !scope.IsExpired)
            {
                await ResumeAsync(userId, cancellationToken);
                return await DegisimAsync(userId, "resumed", "traffic", cancellationToken);
            }
        }

        // 3) SÜRESİ UZATILMIŞSA geri aç. "expired" ile askıya alınmış ama artık süresi dolu
        //    olmayan abonelik (yönetici tarihi ileri aldı) beklemede kalmamalı.
        if (scope.IsSuspended && scope.SuspendReason == "expired" && !scope.IsExpired)
        {
            await ResumeAsync(userId, cancellationToken);
            return await DegisimAsync(userId, "resumed", "expired", cancellationToken);
        }

        return null;
    }

    private async Task<SubscriptionEnforcementChangeDto> DegisimAsync(Guid userId, string eylem, string sebep, CancellationToken cancellationToken)
    {
        var kullanici = await dbContext.Users.FirstOrDefaultAsync(item => item.Id == userId, cancellationToken);
        return new SubscriptionEnforcementChangeDto
        {
            UserId = userId,
            UserName = kullanici?.UserName ?? string.Empty,
            Action = eylem,
            Reason = sebep
        };
    }

    /// <inheritdoc />
    public async Task SuspendAsync(Guid userId, string reason, CancellationToken cancellationToken = default)
    {
        await customerScopes.SuspendAsync(userId, reason, cancellationToken);

        var scope = await customerScopes.GetScopeAsync(userId, cancellationToken);
        foreach (var site in scope.Sites)
        {
            try
            {
                await iisSiteService.StopSiteAsync(site, cancellationToken);
            }
            catch (Exception exception)
            {
                // Bir sitenin durdurulamaması askıyı geri almaz: kayıt zaten askıda ve yönetici
                // durumu görebilmeli. Sessiz de geçmiyoruz.
                logger.LogWarning(exception, "Askıya alma sırasında site durdurulamadı: {Site}", site);
            }
        }

        await auditService.WriteAsync(
            "subscription.suspended",
            "subscription",
            $"Subscription suspended (reason: {reason}); {scope.Sites.Count} site(s) stopped.",
            userId,
            null,
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task ResumeAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        await customerScopes.ResumeAsync(userId, cancellationToken);

        var scope = await customerScopes.GetScopeAsync(userId, cancellationToken);
        foreach (var site in scope.Sites)
        {
            try
            {
                await iisSiteService.StartSiteAsync(site, cancellationToken);
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Askı kaldırılırken site başlatılamadı: {Site}", site);
            }
        }

        await auditService.WriteAsync(
            "subscription.resumed",
            "subscription",
            $"Subscription resumed; {scope.Sites.Count} site(s) started.",
            userId,
            null,
            cancellationToken);
    }
}
