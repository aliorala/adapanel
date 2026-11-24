using System.Security.Claims;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Users;
using AdaPanel.Core.Interfaces;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AdaPanel.Infrastructure.Subscriptions;

/// <inheritdoc />
/// <param name="httpContextAccessor">
/// Askıya almanın GERÇEK aktörünü ve IP'sini çözmek için.
/// <para>
/// <b>Neden isteğe bağlı ve neden bu yoldan:</b> aynı iki metot iki farklı bağlamdan çağrılır —
/// panelden (yönetici/bayi düğmeye basar) ve arka plan işçisinden (kota turu). Aktörü
/// <see cref="ISubscriptionEnforcementService"/> imzasına parametre olarak eklemek, arka plan
/// çağıranını "aktör yok" değerini elle geçirmeye zorlar ve o parametreyi unutan/yanlış dolduran
/// her çağıran sessizce yanlış kayıt üretirdi. Bağlam okunduğunda ise varsayılan DOĞRUDUR: HTTP
/// isteği varsa aktör odur, yoksa kayıt "system" kalır.
/// </para>
/// <para>
/// Varsayılanı <c>null</c>: doğrudan kurulan (testler) örnekler arka plan bağlamı gibi davranır.
/// </para>
/// </param>
public sealed class SubscriptionEnforcementService(
    ICustomerScopeService customerScopes,
    IWebStatisticsService webStatistics,
    IIisSiteService iisSiteService,
    IAuditService auditService,
    AdaPanelDbContext dbContext,
    ILogger<SubscriptionEnforcementService> logger,
    IHttpContextAccessor? httpContextAccessor = null,
    SubscriptionResourceSuspender? resourceSuspender = null) : ISubscriptionEnforcementService
{
    // Doğrudan kurulan (test) örneklerde de aynı mantık çalışsın: FTP/cron servisi yoksa o türler
    // "desteklenmiyor" olarak KAYDEDİLİR (sessiz geçilmez).
    private readonly SubscriptionResourceSuspender suspender = resourceSuspender
        ?? new SubscriptionResourceSuspender(
            customerScopes, iisSiteService, Microsoft.Extensions.Logging.Abstractions.NullLogger<SubscriptionResourceSuspender>.Instance);

    /// <inheritdoc />
    public async Task<SubscriptionUsageDto> GetUsageAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var scope = await customerScopes.GetScopeAsync(userId, cancellationToken);
        var kullanici = await dbContext.Users.FirstOrDefaultAsync(item => item.Id == userId, cancellationToken);

        var (bayt, olculebildi) = await TrafikOlcAsync(scope.Sites, cancellationToken);

        // DİSK DE TRAFİK GİBİ: sayının yanında "ölçülebildi mi" de taşınır. Bu ekran müşterinin
        // kotasına ne kadar kaldığını okuduğu yerdir; kesilmiş bir ölçümü tam değer gibi yazmak,
        // müşteriyi dolmak üzere olan bir kotayla "yerim var" sanmaya bırakırdı.
        var disk = await customerScopes.GetDiskUsageMbAsync(userId, cancellationToken);

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
            DiskUsageMb = disk.UsageMb,
            DiskMeasured = disk.Measured,
            DiskUsageTruncatedReasonKey = disk.TruncatedReasonKey,
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

        // Disk aşımı bayrağı ZATEN olan abonelikler: her turda aynı JSON'u yeniden yazmamak için.
        var diskBayrakli = new HashSet<Guid>();
        try
        {
            foreach (var durum in await customerScopes.GetAttentionStatesAsync(cancellationToken))
            {
                if (durum.DiskOverage is not null)
                {
                    diskBayrakli.Add(durum.UserId);
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Disk aşımı bayrakları okunamadı.");
        }

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

                await BekleyenAdimlariYenidenDeneAsync(userId, cancellationToken);
                await DiskAsimiBayragiAsync(userId, diskBayrakli.Contains(userId), cancellationToken);
            }
            catch (Exception exception)
            {
                // Tek bir aboneliğin hatası TURU DÜŞÜRMEZ; diğerleri denetlenmeye devam eder.
                logger.LogWarning(exception, "Abonelik denetimi başarısız: {UserId}", userId);
            }
        }

        return degisenler;
    }

    /// <summary>
    /// M2: kapatılamayan (askıda hâlâ açık) ya da açılamayan (askı kalktı, hâlâ kapalı) kaynakları her
    /// turda yeniden dener. Eskiden durdurulamayan site bir daha DENENMİYORDU: abonelik "askıda" görünür,
    /// site yayında kalırdı. Sonuç denetim kaydına yazılır; hâlâ başarısızsa dikkat akışında görünür.
    /// </summary>
    private async Task BekleyenAdimlariYenidenDeneAsync(Guid userId, CancellationToken cancellationToken)
    {
        var scope = await customerScopes.GetScopeAsync(userId, cancellationToken);
        var kullanici = await dbContext.Users.AsNoTracking().FirstOrDefaultAsync(item => item.Id == userId, cancellationToken);

        // Yönetici müşteriyi ELLE pasife aldıysa (IsActive=false) kaynaklar kapalı KALMALIDIR.
        var kapaliOlmali = scope.IsSuspended || kullanici is { IsActive: false };
        var rapor = await suspender.RetryAsync(userId, kapaliOlmali, scope.Sites, cancellationToken);
        if (rapor is null)
        {
            return;
        }

        var (aktor, ip, tetikleyici) = Aktor();
        var cumle = kapaliOlmali
            ? SiteSuspensionReport.Stopped(rapor.SitesRequested, rapor.SitesFailed)
            : SiteSuspensionReport.Started(rapor.SitesRequested, rapor.SitesFailed);
        if (rapor.Extras.Length > 0)
        {
            cumle += " " + rapor.Extras;
        }

        await auditService.WriteForSubscriptionAsync(
            "subscription.retry",
            $"subscription:{userId:N}",
            $"Pending {(kapaliOlmali ? "suspension" : "resume")} steps retried (trigger: {tetikleyici}); {cumle}",
            aktor,
            userId,
            ip,
            cancellationToken);
    }

    /// <summary>
    /// M3: plan disk tavanı aşıldıysa BAYRAK konur (dikkat akışı gösterir), altına inince kalkar. Otomatik
    /// askı YOKTUR: planda böyle bir ayar yok ve yeni bir askı politikası uydurulmadı. Kesilmiş ölçüm bir
    /// ALT SINIRDIR; alt sınır tavanı aşıyorsa gerçek kullanım da aşar, bu yüzden bayrak güvenle konur.
    /// </summary>
    private async Task DiskAsimiBayragiAsync(Guid userId, bool zatenBayrakli, CancellationToken cancellationToken)
    {
        var scope = await customerScopes.GetScopeAsync(userId, cancellationToken);
        if (scope.MaxDiskMb <= 0)
        {
            if (zatenBayrakli)
            {
                await customerScopes.SetDiskOverageAsync(userId, null, cancellationToken);
            }

            return;
        }

        var disk = await customerScopes.GetDiskUsageMbAsync(userId, cancellationToken);
        var asildi = disk.UsageMb > scope.MaxDiskMb;
        if (asildi && !zatenBayrakli)
        {
            await customerScopes.SetDiskOverageAsync(
                userId,
                new DiskOverageState { DetectedAtUtc = DateTime.UtcNow, UsageMb = disk.UsageMb, MaxDiskMb = scope.MaxDiskMb },
                cancellationToken);
        }
        else if (!asildi && zatenBayrakli && disk.Measured)
        {
            // Yalnız TAM ölçümle kaldırılır: eksik ölçüm "sığıyor" demek değildir.
            await customerScopes.SetDiskOverageAsync(userId, null, cancellationToken);
        }
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
            // `MaxTrafficGbPerMonth` adı "Gb" der, değeri GiB'dir — bilinçli bir tel uyumu; gerekçesi
            // PanelUnits'te yazılı. Taban oradan gelir, burada yeniden türetilmez.
            var sinir = PanelUnits.GibibytesToBytes(scope.MaxTrafficGbPerMonth);

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

        // DURDURULAN sayilir, ISTENEN degil (bkz. SiteSuspensionReport). Siteler + FTP hesaplari +
        // zamanlanmis gorevler kapatilir; her kaynagin ONCEKI hali kaydedilir ve acilista yalniz
        // askidan once acik olanlar geri acilir. Kapatilamayanlar bekleyen adim olarak kalir:
        // her denetim turunda yeniden denenir ve dikkat akisinda gorunur.
        var rapor = await suspender.CloseAsync(userId, scope.Sites, cancellationToken);
        var durdurulamayan = rapor.SitesFailed;

        var (aktor, ip, tetikleyici) = Aktor();

        // Basarisiz siteler ADIYLA yazilir: "3 tanesi durdurulamadi" demek yoneticiye hangi
        // sitenin hâlâ yayinda oldugunu SOYLEMEZ, yani kayit okunabilir ama ise yaramaz olurdu.
        //
        // CUMLE TEK KAYNAKTAN gelir (SiteSuspensionReport): ayni karar yoneticinin musteriyi ELLE
        // askiya aldigi yolda (UserManagementService) da verilir ve orasi kopyalanmadigi icin
        // ISTENEN sayiyi yazmaya devam ediyordu.
        var durum = SiteSuspensionReport.Stopped(scope.Sites.Count, durdurulamayan);
        if (rapor.Extras.Length > 0)
        {
            durum += " " + rapor.Extras;
        }

        await auditService.WriteForSubscriptionAsync(
            "subscription.suspended",
            $"subscription:{userId:N}",
            $"Subscription suspended (rule: {reason}, trigger: {tetikleyici}); {durum}",
            aktor,
            userId,
            ip,
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task ResumeAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var oncekiKapsam = await customerScopes.GetScopeAsync(userId, cancellationToken);
        await customerScopes.ResumeAsync(userId, cancellationToken);

        // KARDES YOL, AYNI HATA: acilmayan kaynak sessiz kalmaz. Yalniz askidan ONCE acik olanlar acilir
        // (musterinin elle durdurdugu site/hesap/gorev kapali kalir); acilamayanlar bekleyen adimdir.
        var rapor = await suspender.RestoreAsync(
            userId, cancellationToken, oncekiKapsam.IsSuspended ? oncekiKapsam.Sites : null);
        var baslatilamayan = rapor.SitesFailed;

        var (aktor, ip, tetikleyici) = Aktor();

        var durum = SiteSuspensionReport.Started(rapor.SitesRequested, baslatilamayan);
        if (rapor.Extras.Length > 0)
        {
            durum += " " + rapor.Extras;
        }

        await auditService.WriteForSubscriptionAsync(
            "subscription.resumed",
            $"subscription:{userId:N}",
            $"Subscription resumed (trigger: {tetikleyici}); {durum}",
            aktor,
            userId,
            ip,
            cancellationToken);
    }

    /// <summary>Askı/açma kaydının AKTÖRÜ, IP'si ve tetikleyicisi — ölçülerek.</summary>
    /// <remarks>
    /// <para>
    /// <b>Düzeltilen hata:</b> bu iki kayıt <c>userId</c> olarak ABONELİĞİN SAHİBİNİ yazıyordu ve IP
    /// hiç yoktu. Yani bir yönetici müşteriyi elle askıya aldığında kayıt "müşteri kendini askıya
    /// aldı" diyordu — denetim kaydının cevaplaması gereken tek soru ("kim yaptı") tam tersine
    /// cevaplanıyordu. Üstelik yanlış olduğu biliniyordu: denetleyiciye, doğru aktörü taşıyan İKİNCİ
    /// bir kayıt eklenmişti; yani her elle askı iki satır üretiyor, biri yalan söylüyordu.
    /// </para>
    /// <para>
    /// Ayrım TAHMİN EDİLMEZ, ÖLÇÜLÜR: HTTP bağlamı varsa işlemi bir insan panelden tetiklemiştir
    /// (aktör + IP vardır); yoksa arka plan turudur ve aktörü yoktur. "Kural" (manual/traffic/
    /// expired) ile "tetikleyici" (panel/arka plan) ayrı yazılır çünkü ayrı şeylerdir: yönetici
    /// kota turunu elle de başlatabilir; o zaman kural "traffic", tetikleyici "panel"dir.
    /// </para>
    /// </remarks>
    private (Guid? Aktor, string? Ip, string Tetikleyici) Aktor()
    {
        var context = httpContextAccessor?.HttpContext;
        if (context is null)
        {
            return (null, null, "automatic background enforcement");
        }

        var deger = context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return (
            Guid.TryParse(deger, out var aktor) ? aktor : null,
            context.Connection.RemoteIpAddress?.ToString(),
            "panel request");
    }
}
