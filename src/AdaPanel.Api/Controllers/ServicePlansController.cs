using AdaPanel.Api.Extensions;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Users;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Audit;
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
/// <para>
/// <b>Denetim kaydı:</b> plan, satılan paketin TANIMIDIR — fiyatı, site sayısını, disk sınırını
/// belirler. Bir plan değiştiğinde ona bağlı BÜTÜN abonelikler anında etkilenir (plan bir kopya
/// değil bağdır). Faturalama uyuşmazlığında — "ben 10 siteli paket almıştım, panel 3 diyor" —
/// planın ne zaman, kim tarafından, hangi değerden hangi değere çekildiği tek kanıttır; plan
/// kaydının kendisi yalnız SON hâli tutar, geçmişi tutmaz.
/// </para>
/// </remarks>
[ApiController]
[Authorize(Roles = "Administrator,Reseller")]
[Route("api/v1/service-plans")]
public sealed class ServicePlansController(
    IServicePlanService servicePlanService,
    IResellerQuotaService resellerQuotas,
    IAuditService auditService) : ControllerBase
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

        await auditService.AuditAsync(
            HttpContext,
            "plan.created",
            $"service-plan:{plan.Id}",
            $"Service plan '{plan.Name}' created ({Limitler(request)}), owner: {(sahip is null ? "system" : sahip.Value.ToString("N"))}.",
            sahip);

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

        // ÖNCEKİ değerler değişiklikten ÖNCE okunur: plan kaydı yalnız son hâli tutar, "neydi"
        // bilgisi güncellemeden sonra hiçbir yerde kalmaz. Faturalama uyuşmazlığının cevabı tam
        // olarak eski→yeni farkıdır.
        var oncesi = await servicePlanService.GetPlanAsync(id, cancellationToken);

        var plan = await servicePlanService.UpdatePlanAsync(id, request, cancellationToken);

        await auditService.AuditAsync(
            HttpContext,
            "plan.updated",
            $"service-plan:{id}",
            $"Service plan '{plan.Name}' updated. Before: {(oncesi is null ? "unknown" : Limitler(oncesi))} → after: {Limitler(request)}.",
            PlanSahibi(oncesi));

        return Ok(ApiResponse<ServicePlanDto>.Ok(plan, "plans.updated"));
    }

    [HttpDelete("{id}")]
    public async Task<ActionResult<ApiResponse<object>>> DeleteAsync(string id, CancellationToken cancellationToken)
    {
        await EnsureYonetilebilirAsync(id, cancellationToken);

        // Silmeden önce okunur: silinen planın adı/limitleri sonradan hiçbir yerden öğrenilemez,
        // oysa o plana bağlı abonelikler kayıtta bu plan kimliğiyle anılır.
        var silinen = await servicePlanService.GetPlanAsync(id, cancellationToken);

        await servicePlanService.DeletePlanAsync(id, cancellationToken);

        await auditService.AuditAsync(
            HttpContext,
            "plan.deleted",
            $"service-plan:{id}",
            silinen is null
                ? $"Service plan '{id}' deleted."
                : $"Service plan '{silinen.Name}' deleted ({Limitler(silinen)}).",
            PlanSahibi(silinen));

        return Ok(ApiResponse<object>.Ok(new { }, "plans.deleted"));
    }

    /// <summary>
    /// Planın sahibi bayi (varsa) — kaydın o bayiye GÖRÜNMESİ için.
    /// </summary>
    /// <remarks>
    /// Bayi kendi planlarını satar; yöneticinin o plana dokunması bayinin fiyat/limit yapısını
    /// değiştirir. <c>subscriptionUserId</c> doldurulmazsa bayi, kendi planına yapılan bu dokunuşu
    /// denetim görünümünde hiç göremez. Sistem planlarında sahip yoktur → null (sunucu geneli).
    /// </remarks>
    private static Guid? PlanSahibi(ServicePlanDto? plan) =>
        plan is not null && Guid.TryParse(plan.OwnerId, out var sahip) ? sahip : null;

    /// <summary>Planın satılan sınırlarının tek satırlık özeti (kayıt için).</summary>
    /// <remarks>
    /// Kayda plan ADINI yazmak yetmez: ad aynı kalırken limitler düşürülebilir ve müşteri farkı
    /// ancak kotasına çarptığında görür. Bu yüzden DEĞERLER yazılır.
    /// </remarks>
    private static string Limitler(ServicePlanRequest request) =>
        $"sites {request.MaxSites}, disk {request.MaxDiskMb}MB, databases {request.MaxDatabases}, "
        + $"mailboxes {request.MaxMailboxes}, tasks {request.MaxScheduledTasks}, ftp {request.MaxFtpUsers}, "
        + $"traffic {request.MaxTrafficGbPerMonth}GB/month, validity {request.ValidityDays} days, "
        + $"backups {request.MaxBackupsPerDay}/day";

    /// <inheritdoc cref="Limitler(ServicePlanRequest)"/>
    private static string Limitler(ServicePlanDto plan) =>
        $"sites {plan.MaxSites}, disk {plan.MaxDiskMb}MB, databases {plan.MaxDatabases}, "
        + $"mailboxes {plan.MaxMailboxes}, tasks {plan.MaxScheduledTasks}, ftp {plan.MaxFtpUsers}, "
        + $"traffic {plan.MaxTrafficGbPerMonth}GB/month, validity {plan.ValidityDays} days, "
        + $"backups {plan.MaxBackupsPerDay}/day";

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
