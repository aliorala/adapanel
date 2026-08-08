using AdaPanel.Api.Extensions;
using AdaPanel.Api.Security;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Users;
using AdaPanel.Core.Interfaces;
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
    ICustomerScopeService customerScopes) : ControllerBase
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
