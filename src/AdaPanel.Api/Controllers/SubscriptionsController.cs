using AdaPanel.Api.Extensions;
using AdaPanel.Api.Security;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Users;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Audit;
using AdaPanel.Infrastructure.Subscriptions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AdaPanel.Api.Controllers;

/// <summary>
/// Abonelik iş modeli: plan bağı, kullanım, askıya alma.
/// </summary>
/// <remarks>
/// <para>
/// Müşteri KENDİ kullanımını görebilir (kotasının ne kadarını harcadığını bilmeli); askıya
/// alma/geri açma ve başkasının kullanımını görme yönetici ile bayiye özeldir.
/// </para>
/// <para>
/// <b>Denetim kaydı:</b> buradaki uçlar müşterinin SATIN ALDIĞI şeyi değiştirir — bağlı plan,
/// bitiş tarihi, bayi tavanı. Hepsi doğrudan faturalama uyuşmazlığına konu olur ("paketimi kim
/// düşürdü", "aboneliğim neden bir ay erken bitti") ve kaynak kayıtlar yalnız SON durumu tutar,
/// geçmişi tutmaz. Bu yüzden her yazma ucu ESKİ ve YENİ değeri birlikte kaydeder.
/// </para>
/// <para>
/// Her kayıtta <c>subscriptionUserId</c> = aboneliğin sahibi. Bu olmadan kiracı, kendi aboneliğine
/// yapılan yönetici/bayi dokunuşunu kendi denetim görünümünde HİÇ göremezdi — sütun tam bu soru
/// için var.
/// </para>
/// </remarks>
[ApiController]
[Authorize(Roles = "Administrator,Reseller,Customer")]
[Route("api/v1/subscriptions")]
public sealed class SubscriptionsController(
    ISubscriptionEnforcementService enforcement,
    ICustomerScopeService customerScopes,
    IServicePlanService servicePlans,
    IResellerQuotaService resellerQuotas,
    IAuditService auditService) : ControllerBase
{
    /// <summary>Giriş yapmış kullanıcının kendi abonelik kullanımı.</summary>
    [HttpGet("me")]
    public async Task<ActionResult<ApiResponse<SubscriptionUsageDto>>> GetMineAsync(CancellationToken cancellationToken)
    {
        // Alt-kullanıcı parent'ın aboneliğini paylaşır; kota da orada tutulur.
        var subscriptionId = await CustomerAccess.GetSubscriptionUserIdAsync(customerScopes, User, cancellationToken);
        var usage = await enforcement.GetUsageAsync(subscriptionId, cancellationToken);
        return Ok(ApiResponse<SubscriptionUsageDto>.Ok(usage));
    }

    /// <summary>Belirli bir aboneliğin kullanımı (yönetici; bayi yalnız kendi müşterisini).</summary>
    [HttpGet("{userId:guid}")]
    [Authorize(Roles = "Administrator,Reseller")]
    public async Task<ActionResult<ApiResponse<SubscriptionUsageDto>>> GetAsync(Guid userId, CancellationToken cancellationToken)
    {
        await EnsureManageableAsync(userId, cancellationToken);
        var usage = await enforcement.GetUsageAsync(userId, cancellationToken);
        return Ok(ApiResponse<SubscriptionUsageDto>.Ok(usage));
    }

    /// <summary>Aboneliği bir servis planına bağlar (boş planId = bağı kaldır).</summary>
    [HttpPut("{userId:guid}/plan")]
    [Authorize(Roles = "Administrator,Reseller")]
    public async Task<ActionResult<ApiResponse<SubscriptionUsageDto>>> SetPlanAsync(
        Guid userId,
        [FromBody] SetSubscriptionPlanRequest request,
        CancellationToken cancellationToken)
    {
        await EnsureManageableAsync(userId, cancellationToken);
        await EnsurePlanUsableAsync(userId, request.PlanId, cancellationToken);

        // ÖNCEKİ plan değişiklikten ÖNCE ölçülür: abonelik kaydı yalnız güncel plan kimliğini tutar,
        // "hangi paketten hangi pakete geçildi" bilgisi yazma anından sonra hiçbir yerde kalmaz.
        var oncesi = await enforcement.GetUsageAsync(userId, cancellationToken);

        await customerScopes.SetPlanAsync(userId, request.PlanId, cancellationToken);

        var sonrasi = await enforcement.GetUsageAsync(userId, cancellationToken);

        await auditService.AuditAsync(
            HttpContext,
            "subscription.plan.changed",
            $"subscription:{userId:N}",
            $"Subscription plan changed for '{sonrasi.UserName}': "
            + $"'{PlanTanimi(oncesi)}' → '{PlanTanimi(sonrasi)}'.",
            userId);

        return Ok(ApiResponse<SubscriptionUsageDto>.Ok(sonrasi, "subscription.plan_saved"));
    }

    /// <summary>Aboneliğin bitiş tarihini ayarlar (null = süresiz).</summary>
    [HttpPut("{userId:guid}/expiry")]
    [Authorize(Roles = "Administrator,Reseller")]
    public async Task<ActionResult<ApiResponse<SubscriptionUsageDto>>> SetExpiryAsync(
        Guid userId,
        [FromBody] SetSubscriptionExpiryRequest request,
        CancellationToken cancellationToken)
    {
        await EnsureManageableAsync(userId, cancellationToken);

        var oncesi = await enforcement.GetUsageAsync(userId, cancellationToken);

        await customerScopes.SetExpiryAsync(userId, request.ExpiresAtUtc, cancellationToken);

        var sonrasi = await enforcement.GetUsageAsync(userId, cancellationToken);

        // Bitiş tarihi, aboneliğin OTOMATİK ASKIYA ALINMA anıdır: tarihi öne çekmek, müşterinin
        // sitelerini o gün kapatır. "Aboneliğim neden bir ay erken bitti" sorusunun tek kanıtı budur.
        await auditService.AuditAsync(
            HttpContext,
            "subscription.expiry.changed",
            $"subscription:{userId:N}",
            $"Subscription expiry changed for '{sonrasi.UserName}': "
            + $"{Tarih(oncesi.ExpiresAtUtc)} → {Tarih(sonrasi.ExpiresAtUtc)}.",
            userId);

        return Ok(ApiResponse<SubscriptionUsageDto>.Ok(sonrasi, "subscription.expiry_saved"));
    }

    /// <summary>Aboneliği elle askıya alır (siteleri durur).</summary>
    [HttpPost("{userId:guid}/suspend")]
    [Authorize(Roles = "Administrator,Reseller")]
    public async Task<ActionResult<ApiResponse<SubscriptionUsageDto>>> SuspendAsync(Guid userId, CancellationToken cancellationToken)
    {
        await EnsureManageableAsync(userId, cancellationToken);
        await enforcement.SuspendAsync(userId, "manual", cancellationToken);

        // NEDEN İKİNCİ BİR KAYIT: SubscriptionEnforcementService de bir kayıt yazar, ama o kaydın
        // AKTÖRÜ abonelik sahibinin kendisidir (userId) ve İP adresi yoktur — çünkü aynı metot arka
        // plan uygulayıcısı tarafından da çağrılır. Yani elle askıya almada kayıt "müşteri kendini
        // askıya aldı" der; asıl soru olan "hangi yönetici/bayi, nereden" cevapsız kalır. Buradaki
        // kayıt gerçek aktörü ve İP'yi taşır; servisin kaydı ise kaç sitenin durduğunu.
        await auditService.AuditAsync(
            HttpContext,
            "subscription.suspended.manual",
            $"subscription:{userId:N}",
            "Subscription suspended manually from the panel.",
            userId);

        return Ok(ApiResponse<SubscriptionUsageDto>.Ok(
            await enforcement.GetUsageAsync(userId, cancellationToken),
            "subscription.suspended"));
    }

    /// <summary>Askıyı kaldırır (siteleri başlatır).</summary>
    [HttpPost("{userId:guid}/resume")]
    [Authorize(Roles = "Administrator,Reseller")]
    public async Task<ActionResult<ApiResponse<SubscriptionUsageDto>>> ResumeAsync(Guid userId, CancellationToken cancellationToken)
    {
        await EnsureManageableAsync(userId, cancellationToken);
        await enforcement.ResumeAsync(userId, cancellationToken);

        // Gerekçe: SuspendAsync ile aynı (servisin kaydı aktörü aboneliğin kendisi olarak yazar).
        await auditService.AuditAsync(
            HttpContext,
            "subscription.resumed.manual",
            $"subscription:{userId:N}",
            "Subscription resumed manually from the panel.",
            userId);

        return Ok(ApiResponse<SubscriptionUsageDto>.Ok(
            await enforcement.GetUsageAsync(userId, cancellationToken),
            "subscription.resumed"));
    }

    /// <summary>Bütün abonelikleri hemen denetler (arka plan turunu beklemeden).</summary>
    [HttpPost("enforce")]
    [Authorize(Roles = "Administrator")]
    public async Task<ActionResult<ApiResponse<IReadOnlyCollection<SubscriptionEnforcementChangeDto>>>> EnforceAsync(CancellationToken cancellationToken)
    {
        var changes = await enforcement.EnforceAllAsync(cancellationToken);

        // Elle tetiklenen tur, BÜTÜN abonelikleri askıya alabilir/açabilir. Tek tek değişiklikler
        // enforcement tarafında kayda geçer; buradaki kayıt turu KİMİN başlattığını söyler — arka
        // plan turuyla elle turun ayırt edilebilmesi için.
        await auditService.AuditAsync(
            HttpContext,
            "subscription.enforced",
            "subscriptions",
            $"Subscription enforcement run manually; {changes.Count} subscription(s) changed state.");

        return Ok(ApiResponse<IReadOnlyCollection<SubscriptionEnforcementChangeDto>>.Ok(changes, "subscription.enforced"));
    }

    // --- Bayi tavanı (bayinin KENDİ dağıtım sınırı) ---

    /// <summary>
    /// Bir bayinin tavanı ve tavanın ne kadarının dağıtıldığı.
    /// </summary>
    /// <remarks>
    /// Bayi KENDİ tavanını okuyabilir (ne kadar hakkı kaldığını bilmeden müşteri ekleyemez);
    /// başkasının tavanını yalnız yönetici okur.
    /// </remarks>
    [HttpGet("resellers/{userId:guid}/quota")]
    [Authorize(Roles = "Administrator,Reseller")]
    public async Task<ActionResult<ApiResponse<ResellerQuotaUsageDto>>> GetResellerQuotaAsync(Guid userId, CancellationToken cancellationToken)
    {
        if (CustomerAccess.IsReseller(User) && userId != User.GetRequiredUserId())
        {
            throw new Core.Common.AppException("users.forbidden", 403, 403);
        }

        var usage = await ResellerQuotaGuard.MeasureAsync(resellerQuotas, customerScopes, userId, cancellationToken);
        return Ok(ApiResponse<ResellerQuotaUsageDto>.Ok(usage));
    }

    /// <summary>
    /// Bayiye tavan atar (0 = sınırsız). YALNIZ yönetici: bayi kendi tavanını yükseltebilseydi
    /// tavan diye bir şey olmazdı.
    /// </summary>
    [HttpPut("resellers/{userId:guid}/quota")]
    [Authorize(Roles = "Administrator")]
    public async Task<ActionResult<ApiResponse<ResellerQuotaUsageDto>>> SetResellerQuotaAsync(
        Guid userId,
        [FromBody] ResellerQuotaRequest request,
        CancellationToken cancellationToken)
    {
        var oncesi = await resellerQuotas.GetQuotaAsync(userId, cancellationToken);

        await resellerQuotas.SetQuotaAsync(userId, request, cancellationToken);

        // Bayi tavanı, bayinin SATIN ALDIĞI toptan haktır: düşürülmesi bayinin yeni müşteri
        // açmasını anında durdurur, yükseltilmesi ise faturalanmamış kaynak dağıtımına izin verir.
        // Her iki yön de ticari uyuşmazlık konusudur.
        await auditService.AuditAsync(
            HttpContext,
            "subscription.reseller_quota.changed",
            $"reseller:{userId:N}",
            $"Reseller allowance changed: "
            + $"{(oncesi is null ? "unset" : $"sites {oncesi.MaxSites}, disk {oncesi.MaxDiskMb}MB")} "
            + $"→ sites {request.MaxSites}, disk {request.MaxDiskMb}MB.",
            userId);

        return Ok(ApiResponse<ResellerQuotaUsageDto>.Ok(
            await ResellerQuotaGuard.MeasureAsync(resellerQuotas, customerScopes, userId, cancellationToken),
            "subscription.reseller_quota_saved"));
    }

    /// <summary>Kayda yazılacak plan tanımı: ad + kimlik (plan bağı yoksa "no plan").</summary>
    /// <remarks>
    /// Yalnız plan ADI yetmez — plan adları yeniden kullanılır ve silinen bir planın adı başka bir
    /// plana verilebilir; kimlik olmadan kayıt yıllar sonra hangi plana işaret ettiğini söyleyemez.
    /// </remarks>
    private static string PlanTanimi(SubscriptionUsageDto usage) =>
        string.IsNullOrWhiteSpace(usage.PlanId)
            ? "no plan (custom limits)"
            : $"{usage.PlanName} [{usage.PlanId}]";

    /// <summary>Bitiş tarihi metni; null = süresiz.</summary>
    private static string Tarih(DateTime? value) =>
        value is null ? "unlimited" : value.Value.ToString("u");

    /// <summary>
    /// GÜVENLİK (çapraz-kiracı): bayi yalnız KENDİ müşterisinin aboneliğini yönetebilir.
    /// Aksi hâlde bir bayi, başka bir bayinin müşterisini askıya alıp sitelerini kapatabilirdi.
    /// </summary>
    private async Task EnsureManageableAsync(Guid userId, CancellationToken cancellationToken)
    {
        if (!CustomerAccess.IsReseller(User))
        {
            return;
        }

        var owner = await customerScopes.GetOwnerAsync(userId, cancellationToken);
        if (owner != User.GetRequiredUserId())
        {
            throw new Core.Common.AppException("users.forbidden", 403, 403);
        }
    }

    /// <summary>
    /// Bayinin bağlamak istediği planın hem ERİŞİLEBİLİR hem de TAVANINA SIĞAN bir plan olduğunu doğrular.
    /// </summary>
    /// <remarks>
    /// <para>
    /// İki ayrı açık kapatılıyor:
    /// </para>
    /// <list type="number">
    /// <item>
    /// <b>Başka bayinin planı:</b> plan kimliği bir GUID metni; tahmin edilemez ama sızabilir
    /// (ekran görüntüsü, günlük, destek yazışması). Liste ucu başka bayinin planını gizlerken
    /// bağlama ucunun kabul etmesi, gizlemeyi süse çevirirdi.
    /// </item>
    /// <item>
    /// <b>Tavan aşımı:</b> bayi sistem planlarını da bağlayabildiği için, tavanı 10 site olan bir
    /// bayi 100 sitelik SİSTEM planını müşterisine bağlayarak tavanını atlayabilirdi. Bu yüzden
    /// denetim planın sahibine değil, dağıtılan TOPLAMA bakar.
    /// </item>
    /// </list>
    /// </remarks>
    private async Task EnsurePlanUsableAsync(Guid customerId, string? planId, CancellationToken cancellationToken)
    {
        if (!CustomerAccess.IsReseller(User))
        {
            return;
        }

        var bayiId = User.GetRequiredUserId();

        if (string.IsNullOrWhiteSpace(planId))
        {
            // Plan bağını KALDIRMAK, müşteriyi kayıttaki özel limitlere döndürür; tavan denetimi
            // o değerlerle yapılır.
            var mevcut = await customerScopes.GetScopeAsync(customerId, cancellationToken);
            await ResellerQuotaGuard.EnsureAllocationFitsAsync(
                resellerQuotas, customerScopes, bayiId, customerId, mevcut.MaxSites, mevcut.MaxDiskMb, cancellationToken);
            return;
        }

        var plan = await servicePlans.GetPlanAsync(planId, cancellationToken)
            ?? throw new Core.Common.AppException("plans.not_found", 404, 404);

        var benim = bayiId.ToString("N");
        if (plan.OwnerId.Length > 0 && !string.Equals(plan.OwnerId, benim, StringComparison.OrdinalIgnoreCase))
        {
            throw new Core.Common.AppException("users.forbidden", 403, 403);
        }

        await ResellerQuotaGuard.EnsureAllocationFitsAsync(
            resellerQuotas, customerScopes, bayiId, customerId, plan.MaxSites, plan.MaxDiskMb, cancellationToken);
    }
}

/// <summary>Abonelik ↔ plan bağı isteği.</summary>
public sealed class SetSubscriptionPlanRequest
{
    /// <summary>Boş ya da null: plan bağını kaldır, özel limitlere dön.</summary>
    public string? PlanId { get; set; }
}

/// <summary>Abonelik bitiş tarihi isteği.</summary>
public sealed class SetSubscriptionExpiryRequest
{
    /// <summary>null = süresiz.</summary>
    public DateTime? ExpiresAtUtc { get; set; }
}
