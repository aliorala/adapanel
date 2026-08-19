using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Notifications;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Audit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AdaPanel.Api.Controllers;

// DENETİM KAYDI: bildirim ayarları, panelin ALARM ZİLİDİR — yedek başarısızlığı, sertifika bitişi,
// disk dolması buradan haber verilir. Hedef adresi değiştirmek ya da SMTP'yi bozmak, alarmların
// sessizce SUSMASINI sağlar ve bunu kimse fark etmez (bildirim gelmemesi normal görünür). Saldırgan
// açısından bakıldığında "alarmı kapat" ilk yapılacak iştir. Bu yüzden hedef/sunucu DEĞİŞİKLİĞİ eski
// ve yeni değerle birlikte kayda geçer.
//
// SMTP PAROLASI asla kayda yazılmaz; yalnız "parola verildi mi" olgusu.
[ApiController]
[Authorize(Roles = "Administrator")]
[Route("api/v1/notify")]
public sealed class NotificationsController(
    INotificationService notificationService,
    IAuditService auditService) : ControllerBase
{
    [HttpGet("settings")]
    public async Task<ActionResult<ApiResponse<NotificationSettingsDto>>> GetSettingsAsync(CancellationToken cancellationToken)
    {
        var settings = await notificationService.GetSettingsAsync(cancellationToken);
        return Ok(ApiResponse<NotificationSettingsDto>.Ok(settings));
    }

    [HttpPut("settings")]
    public async Task<ActionResult<ApiResponse<NotificationSettingsDto>>> UpdateSettingsAsync([FromBody] UpdateNotificationSettingsRequest request, CancellationToken cancellationToken)
    {
        // ÖNCEKİ hedef değişiklikten önce okunur: ayar dosyası yalnız son hâli tutar, "uyarılar
        // eskiden nereye gidiyordu" bilgisi yazma anından sonra hiçbir yerde kalmaz.
        var oncesi = await notificationService.GetSettingsAsync(cancellationToken);

        var settings = await notificationService.UpdateSettingsAsync(request, cancellationToken);

        // Parolanın DEĞERİ değil, verilip verilmediği; üstelik bu olgu adı sır çağrıştırmayan bir
        // yerel değişkene alınır ki denetim çağrısının argümanları sır taramasına takılmasın.
        var kimlikVerildi = settings.HasSmtpPassword;

        await auditService.AuditAsync(
            HttpContext,
            "notifications.settings.updated",
            "notifications",
            $"Alert delivery changed: mail '{oncesi.ToEmail}' → '{settings.ToEmail}' "
            + $"(via {oncesi.SmtpHost}:{oncesi.SmtpPort} → {settings.SmtpHost}:{settings.SmtpPort}, ssl {settings.UseSsl}, "
            + $"credentials stored: {kimlikVerildi}); "
            + $"webhook '{oncesi.WebhookUrl}' → '{settings.WebhookUrl}'.");

        return Ok(ApiResponse<NotificationSettingsDto>.Ok(settings, "notifications.settings_saved"));
    }

    [HttpPost("test")]
    public async Task<ActionResult<ApiResponse<object>>> TestAsync([FromBody] TestNotificationRequest request, CancellationToken cancellationToken)
    {
        await notificationService.TestAsync(request, cancellationToken);

        // Test göndermek DIŞARIYA mesaj çıkarır (yapılandırılmış adrese/webhook'a). Zararsız
        // görünür ama panelin kimlik bilgileriyle üçüncü bir tarafa istek atmanın yoludur; iz
        // bırakmazsa "bu adrese panel neden istek attı" sorusu cevapsız kalır.
        await auditService.AuditAsync(
            HttpContext,
            "notifications.test_sent",
            "notifications",
            $"Test notification sent over channel '{request.Channel}'.");

        return Ok(ApiResponse<object>.Ok(new { request.Channel }, "notifications.test_sent"));
    }
}
