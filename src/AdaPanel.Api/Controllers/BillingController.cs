using AdaPanel.Api.Extensions;
using AdaPanel.Core.Dtos.Billing;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Billing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AdaPanel.Api.Controllers;

/// <summary>
/// Faturalama sistemi (WHMCS) entegrasyonu: toplu kullanım raporu ve silme öncesi güvenlik denetimi.
/// </summary>
/// <remarks>
/// <para>
/// <b>Bu denetleyici sağlama YAPMAZ.</b> Hesap açma/askı/iptal panelde ZATEN var
/// (<c>UsersController</c>, <c>SubscriptionsController</c>) ve WHMCS modülü doğrudan onları çağırır.
/// Burada yalnızca faturalama sisteminin panelde karşılığı OLMAYAN iki ihtiyacı karşılanır:
/// </para>
/// <list type="number">
/// <item>
/// <b>Toplu kullanım (<c>UsageUpdate</c>):</b> WHMCS bu işlevi SUNUCU başına çağırır, hizmet başına
/// değil — modüle hizmet listesi vermez. Tek tek <c>subscriptions/{id}</c> çağırmak, 500 müşterili
/// bir sunucuda cron turunu 500 isteğe çevirirdi.
/// </item>
/// <item>
/// <b>Silme denetimi (<c>TerminateAccount</c>):</b> modül hesabı yalnız kullanıcı adıyla buluyordu;
/// yanlış hesabı silmemek için kararın panelde, kanıta bakarak verilmesi gerekir.
/// </item>
/// </list>
/// <para>
/// <b>Kimlik:</b> sınıf düzeyinde <c>[Authorize]</c> — kimliği doğrulanmamış hiçbir istek gövdeye
/// ulaşamaz. Panel anahtarı (<c>Bearer adap_…</c>) burada ÇALIŞIR (<see cref="Api.Security.PanelAuthentication"/>
/// yönlendirici şeması) ve entegrasyonun tercih edilen yoludur: dakikada 5 istekle sınırlı
/// <c>login</c> politikasına takılmaz. Uçların hepsi GET olduğu için <b>salt-okur bir anahtar
/// yeter</b> — kullanım toplayan cron'a yazma yetkisi vermek gereksiz bir risktir.
/// </para>
/// </remarks>
[ApiController]
[Authorize(Roles = "Administrator")]
[Route("api/v1/billing")]
public sealed class BillingController(
    IBillingIntegrationService billing,
    IAuditService auditService) : ControllerBase
{
    /// <summary>Entegrasyon ayarı + son çağrılar (ayar ekranı).</summary>
    [HttpGet("settings")]
    public async Task<ActionResult<ApiResponse<BillingSettingsDto>>> GetSettingsAsync(CancellationToken cancellationToken)
    {
        return Ok(ApiResponse<BillingSettingsDto>.Ok(await billing.GetSettingsAsync(cancellationToken)));
    }

    /// <summary>Entegrasyonu açar/kapatır.</summary>
    [HttpPut("settings")]
    public async Task<ActionResult<ApiResponse<BillingSettingsDto>>> UpdateSettingsAsync(
        [FromBody] UpdateBillingSettingsRequest request,
        CancellationToken cancellationToken)
    {
        var settings = await billing.UpdateSettingsAsync(request.Enabled, cancellationToken);
        await AuditAsync(
            "billing.settings_changed",
            request.Enabled ? "Faturalama entegrasyonu ACILDI." : "Faturalama entegrasyonu KAPATILDI.",
            cancellationToken);

        return Ok(ApiResponse<BillingSettingsDto>.Ok(settings, "billing.settings_saved"));
    }

    /// <summary>
    /// Bütün aboneliklerin kullanımı — WHMCS <c>UsageUpdate</c> bunu tek çağrıda okur.
    /// </summary>
    [HttpGet("usage")]
    public async Task<ActionResult<ApiResponse<BillingUsageReportDto>>> GetUsageAsync(CancellationToken cancellationToken)
    {
        var report = await billing.BuildUsageReportAsync(cancellationToken);
        await AuditAsync(
            "billing.usage_exported",
            $"{report.Accounts.Count} abonelik disa aktarildi ({report.UnmeasuredTrafficCount} olculemeyen trafik).",
            cancellationToken);

        return Ok(ApiResponse<BillingUsageReportDto>.Ok(report));
    }

    /// <summary>
    /// Bir hesabın faturalama isteğiyle SİLİNEBİLİR olup olmadığını ölçer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Silmeyi bu uç YAPMAZ, yalnızca karar verir; silme yine <c>DELETE /api/v1/users/{id}</c> ile
    /// olur. Ayrımın sebebi: karar salt-okunurdur ve salt-okur bir anahtarla sorulabilmelidir,
    /// silme ise yıkıcı yetki ister. İkisini tek uçta birleştirmek, denetimi silme yetkisinin
    /// arkasına saklardı.
    /// </para>
    /// <para>
    /// Sonuç <c>safeToTerminate=false</c> ise modül silmeyi yapmaz. Bu, WHMCS'te iptal edilen bir
    /// hizmetin YANLIŞ hesabı yok etmesini önleyen kapıdır.
    /// </para>
    /// </remarks>
    [HttpGet("terminate-check")]
    public async Task<ActionResult<ApiResponse<BillingTerminationCheckDto>>> CheckTerminationAsync(
        [FromQuery] string? userName,
        [FromQuery] string? domain,
        CancellationToken cancellationToken)
    {
        var decision = await billing.CheckTerminationAsync(userName, domain, cancellationToken);

        // REDDEDİLEN denetim de kayda geçer: "iptal neden çalışmadı" sorusunun yanıtı burada olmalı.
        await AuditAsync(
            "billing.terminate_checked",
            $"'{decision.UserName}' icin silme denetimi: safe={decision.SafeToTerminate}, reason={decision.Reason}.",
            cancellationToken);

        return Ok(ApiResponse<BillingTerminationCheckDto>.Ok(decision));
    }

    /// <summary>
    /// Denetim kaydı. Faturalama uçları hesap verisine dokunduğu için her çağrı iz bırakır;
    /// hiçbirine anahtar/parola yazılmaz.
    /// </summary>
    private Task AuditAsync(string action, string details, CancellationToken cancellationToken) =>
        auditService.WriteAsync(
            action,
            "billing",
            details,
            User.GetRequiredUserId(),
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken);
}
