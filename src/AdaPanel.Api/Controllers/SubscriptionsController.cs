using AdaPanel.Api.Extensions;
using AdaPanel.Api.Security;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Users;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Subscriptions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AdaPanel.Api.Controllers;

/// <summary>
/// Abonelik iş modeli: plan bağı, kullanım, askıya alma.
/// </summary>
/// <remarks>
/// Müşteri KENDİ kullanımını görebilir (kotasının ne kadarını harcadığını bilmeli); askıya
/// alma/geri açma ve başkasının kullanımını görme yönetici ile bayiye özeldir.
/// </remarks>
[ApiController]
[Authorize(Roles = "Administrator,Reseller,Customer")]
[Route("api/v1/subscriptions")]
public sealed class SubscriptionsController(
    ISubscriptionEnforcementService enforcement,
    ICustomerScopeService customerScopes,
    IServicePlanService servicePlans,
    IResellerQuotaService resellerQuotas) : ControllerBase
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
        await customerScopes.SetPlanAsync(userId, request.PlanId, cancellationToken);
        return Ok(ApiResponse<SubscriptionUsageDto>.Ok(
            await enforcement.GetUsageAsync(userId, cancellationToken),
            "subscription.plan_saved"));
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
        await customerScopes.SetExpiryAsync(userId, request.ExpiresAtUtc, cancellationToken);
        return Ok(ApiResponse<SubscriptionUsageDto>.Ok(
            await enforcement.GetUsageAsync(userId, cancellationToken),
            "subscription.expiry_saved"));
    }

    /// <summary>Aboneliği elle askıya alır (siteleri durur).</summary>
    [HttpPost("{userId:guid}/suspend")]
    [Authorize(Roles = "Administrator,Reseller")]
    public async Task<ActionResult<ApiResponse<SubscriptionUsageDto>>> SuspendAsync(Guid userId, CancellationToken cancellationToken)
    {
        await EnsureManageableAsync(userId, cancellationToken);
        await enforcement.SuspendAsync(userId, "manual", cancellationToken);
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
        await resellerQuotas.SetQuotaAsync(userId, request, cancellationToken);
        return Ok(ApiResponse<ResellerQuotaUsageDto>.Ok(
            await ResellerQuotaGuard.MeasureAsync(resellerQuotas, customerScopes, userId, cancellationToken),
            "subscription.reseller_quota_saved"));
    }

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
