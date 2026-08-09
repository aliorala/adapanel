using AdaPanel.Api.Extensions;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Users;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Subscriptions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AdaPanel.Api.Controllers;

/// <summary>
/// Servis planları (satılan paketler).
/// </summary>
/// <remarks>
/// <para>
/// Controller tümüyle <c>Administrator</c>'a kilitliydi ve bu, BAYİ KANALINI fiilen çalışmaz
/// kılıyordu: bayi kendi paketlerini tanımlayamıyor, her fiyat/limit değişikliği için yöneticiye
/// başvurmak zorunda kalıyordu. Plesk'te bayinin ilk yaptığı iş kendi planlarını kurmaktır.
/// </para>
/// <para>
/// <b>İzolasyon kuralı:</b> bayi YALNIZ kendi planlarını değiştirir/siler. Sistem planlarını
/// (yöneticinin tanımladığı, sahipsiz planlar) okuyabilir — çünkü kendi müşterisine
/// bağlayabilmesi gerekir — ama düzenleyemez. Başka bayinin planını ne görür ne de değiştirir.
/// </para>
/// <para>
/// <b>Tavan kuralı:</b> tavanı olan bir bayi, tavanının üstünde limit veren plan tanımlayamaz
/// (<see cref="ResellerQuotaGuard.EnsurePlanFitsAsync"/>). Aksi hâlde bayi kendi satın almadığı
/// kaynağı satabilirdi ve bu ancak müşteri siteyi açmaya çalıştığında ortaya çıkardı.
/// </para>
/// </remarks>
[ApiController]
[Authorize(Roles = "Administrator,Reseller")]
[Route("api/v1/service-plans")]
public sealed class ServicePlansController(
    IServicePlanService servicePlanService,
    IResellerQuotaService resellerQuotas) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ApiResponse<IReadOnlyCollection<ServicePlanDto>>>> GetAsync(CancellationToken cancellationToken)
    {
        var plans = await servicePlanService.GetPlansAsync(cancellationToken);
        return Ok(ApiResponse<IReadOnlyCollection<ServicePlanDto>>.Ok(Gorunur(plans)));
    }

    [HttpPost]
    public async Task<ActionResult<ApiResponse<ServicePlanDto>>> CreateAsync([FromBody] ServicePlanRequest request, CancellationToken cancellationToken)
    {
        Guid? sahip = null;
        if (User.IsInRole("Reseller"))
        {
            sahip = User.GetRequiredUserId();
            await ResellerQuotaGuard.EnsurePlanFitsAsync(
                resellerQuotas, sahip.Value, request.MaxSites, request.MaxDiskMb, cancellationToken);
        }

        var plan = await servicePlanService.CreatePlanAsync(request, sahip, cancellationToken);
        return Ok(ApiResponse<ServicePlanDto>.Ok(plan, "plans.created"));
    }

    [HttpPut("{id}")]
    public async Task<ActionResult<ApiResponse<ServicePlanDto>>> UpdateAsync(string id, [FromBody] ServicePlanRequest request, CancellationToken cancellationToken)
    {
        await EnsureYonetilebilirAsync(id, cancellationToken);

        if (User.IsInRole("Reseller"))
        {
            await ResellerQuotaGuard.EnsurePlanFitsAsync(
                resellerQuotas, User.GetRequiredUserId(), request.MaxSites, request.MaxDiskMb, cancellationToken);
        }

        var plan = await servicePlanService.UpdatePlanAsync(id, request, cancellationToken);
        return Ok(ApiResponse<ServicePlanDto>.Ok(plan, "plans.updated"));
    }

    [HttpDelete("{id}")]
    public async Task<ActionResult<ApiResponse<object>>> DeleteAsync(string id, CancellationToken cancellationToken)
    {
        await EnsureYonetilebilirAsync(id, cancellationToken);
        await servicePlanService.DeletePlanAsync(id, cancellationToken);
        return Ok(ApiResponse<object>.Ok(new { }, "plans.deleted"));
    }

    /// <summary>
    /// Çağıranın GÖREBİLECEĞİ planlar: yönetici hepsini, bayi kendi planları + sistem planları.
    /// </summary>
    /// <remarks>
    /// Başka bayinin planı listede yer almaz; plan adları fiyat/paket bilgisi taşır ve bir bayinin
    /// rakibinin paket yapısını panelden okuyabilmesi, kiracı izolasyonunun ticari tarafıdır.
    /// </remarks>
    private IReadOnlyCollection<ServicePlanDto> Gorunur(IReadOnlyCollection<ServicePlanDto> plans)
    {
        if (!User.IsInRole("Reseller"))
        {
            return plans;
        }

        var benim = User.GetRequiredUserId().ToString("N");
        return plans
            .Where(plan => plan.OwnerId.Length == 0 || string.Equals(plan.OwnerId, benim, StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    /// <summary>
    /// GÜVENLİK (çapraz-bayi): bayi yalnız KENDİ sahipli planını düzenleyip silebilir.
    /// </summary>
    /// <remarks>
    /// Sistem planı (sahipsiz) de korunur: bir bayi onu düzenleseydi, o plana bağlı BÜTÜN
    /// abonelikler — başka bayilerinkiler ve yöneticinin doğrudan müşterileri dahil — anında
    /// etkilenirdi, çünkü plan bir kopya değil bağdır (bkz. CustomerScopeService.GetScopeAsync).
    /// </remarks>
    private async Task EnsureYonetilebilirAsync(string id, CancellationToken cancellationToken)
    {
        if (!User.IsInRole("Reseller"))
        {
            return;
        }

        var plan = await servicePlanService.GetPlanAsync(id, cancellationToken)
            ?? throw new AppException("plans.not_found", 404, 404);

        var benim = User.GetRequiredUserId().ToString("N");
        if (!string.Equals(plan.OwnerId, benim, StringComparison.OrdinalIgnoreCase))
        {
            // Sahipsiz (sistem) plan da buraya düşer: bayi için o plan salt okunurdur.
            throw new AppException("users.forbidden", 403, 403);
        }
    }
}
