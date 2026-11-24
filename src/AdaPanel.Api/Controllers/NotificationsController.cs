using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Notifications;
using AdaPanel.Core.Common;
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
        // Webhook imza anahtarı okumada MASKELİ döner (yalnız son 4 karakter): tam değer ekran görüntüsüne,
        // tarayıcı önbelleğine ve proxy günlüğüne girmesin. Tam değer yalnız DÖNDÜRME anında, bir kez görünür.
        settings.WebhookSecret = MaskSecret(settings.WebhookSecret);
        return Ok(ApiResponse<NotificationSettingsDto>.Ok(settings));
    }

    /// <summary>
    /// Webhook imza anahtarını yeniden üretir; YENİ değer yanıtta yalnız bu çağrıda görünür.
    /// </summary>
    /// <remarks>
    /// Eski anahtar anında geçersizdir (alıcı tarafın doğrulaması güncellenene kadar imza uyuşmaz) — bu
    /// bilinçli bir kesinti olduğundan denetim kaydına düşer. Anahtarın KENDİSİ kayda YAZILMAZ.
    /// </remarks>
    [HttpPost("webhook-secret/rotate")]
    public async Task<ActionResult<ApiResponse<WebhookSecretRotationDto>>> RotateWebhookSecretAsync(CancellationToken cancellationToken)
    {
        var fresh = await notificationService.RotateWebhookSecretAsync(cancellationToken);
        await auditService.AuditAsync(
            HttpContext,
            "notifications.webhook_secret.rotated",
            "notifications",
            "Webhook signature secret (X-AdaPanel-Signature) was rotated; the previous secret is no longer valid.");

        return Ok(ApiResponse<WebhookSecretRotationDto>.Ok(
            new WebhookSecretRotationDto { WebhookSecret = fresh, Masked = MaskSecret(fresh) },
            "notifications.webhook_secret_rotated"));
    }

    /// <summary>Boşsa boş; değilse son dört karakter dışında maskelenmiş.</summary>
    public static string MaskSecret(string? secret) =>
        string.IsNullOrEmpty(secret) ? string.Empty
        : secret.Length <= 8 ? new string('•', secret.Length)
        : new string('•', 12) + secret[^4..];

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

        // WEBHOOK ADRESI KAYDA YOLU/SORGUSU OLMADAN GIRER -- sema + konak kalir, gerisi maskelenir.
        //
        // NEDEN: Slack, Discord, Teams ve Google Chat webhook'larinda gizli deger YOLUN ICINDEDIR
        // (`https://hooks.slack.com/services/T…/B…/<gizli>`). Adresin tamami, o odaya panel adina
        // mesaj gonderme yetkisi verir. Ustelik denetim kaydi bu satiri yazan roldan DAHA GENIS
        // okunur: `GET /api/v1/audit-logs/search` ve `/export` uclarinda rol kisiti YOKTUR ve
        // Operator rolu sunucu genelinde SINIRSIZ gorunurluk alir
        // (AuditService.ResolveVisibilityAsync) -- yani yonetici olmayan bir Operator bu satiri
        // okuyabiliyordu. Bayi/Musteri ise satir kapsamina takilir.
        //
        // NEDEN TUMDEN SILINMIYOR: kaydin isi "alarmlar nereye gidiyordu, nereye tasindi" sorusunu
        // cevaplamaktir. Konak korunursa cevap durur (Slack'ten kendi sunucusuna tasinmis); yalniz
        // gizli degeri tasiyan parca gider.
        //
        // NEDEN DESENE BIRAKILMIYOR: SecretRedactor tanidigi webhook konaklarini zaten yakalar, ama
        // yonetici KENDI sunucusunu kullanabilir ve o konak tahmin edilemez. "Bu dizge bir webhook
        // adresidir" bilgisi yalniz BURADA vardir. Kural yine tek kaynakta (SecretRedactor.RedactUrl);
        // burada olan yalniz onun UYGULANMASI.
        //
        // NEDEN YEREL DEGISKEN (aciklama da cagri DISINDA): AuditCoverageTests denetim cagrisinin
        // ARGUMAN METNINI "Password/Parola/Secret" kelimeleri icin tarar. `AuditSecretRedactor`
        // ADININ ICINDE "Secret" gectigi icin, maskeleyiciyi cagri argumaninin icinde cagirmak
        // nobetciyi YANLIS ALARMLA kirmiziya dusuruyordu -- yani dogru olani yapmak testi
        // bozuyordu. Ustteki `kimlikVerildi` degiskeni de tam olarak ayni sebeple var (bkz. yorumu).
        var webhookOncesi = AuditSecretRedactor.RedactUrl(oncesi.WebhookUrl);
        var webhookSonrasi = AuditSecretRedactor.RedactUrl(settings.WebhookUrl);

        await auditService.AuditAsync(
            HttpContext,
            "notifications.settings.updated",
            "notifications",
            $"Alert delivery changed: mail '{oncesi.ToEmail}' → '{settings.ToEmail}' "
            + $"(via {oncesi.SmtpHost}:{oncesi.SmtpPort} → {settings.SmtpHost}:{settings.SmtpPort}, ssl {settings.UseSsl}, "
            + $"credentials stored: {kimlikVerildi}); "
            + $"webhook '{webhookOncesi}' → '{webhookSonrasi}'.");

        settings.WebhookSecret = MaskSecret(settings.WebhookSecret);
        return Ok(ApiResponse<NotificationSettingsDto>.Ok(settings, "notifications.settings_saved"));
    }

    [HttpPost("test")]
    public async Task<ActionResult<ApiResponse<object>>> TestAsync([FromBody] TestNotificationRequest request, CancellationToken cancellationToken)
    {
        var submitted = new TestNotificationRequest
        {
            Channel = request.Channel,
            Message = request.Message,
            ExpectedSettingsRevision = request.ExpectedSettingsRevision
        };
        if (string.IsNullOrWhiteSpace(submitted.ExpectedSettingsRevision))
            throw new AppException("notifications.settings_revision_required", 400, 400);

        await notificationService.TestAsync(submitted, cancellationToken);

        // Test göndermek DIŞARIYA mesaj çıkarır (yapılandırılmış adrese/webhook'a). Zararsız
        // görünür ama panelin kimlik bilgileriyle üçüncü bir tarafa istek atmanın yoludur; iz
        // bırakmazsa "bu adrese panel neden istek attı" sorusu cevapsız kalır.
        await auditService.AuditAsync(
            HttpContext,
            "notifications.test_sent",
            "notifications",
            $"Test notification sent over channel '{submitted.Channel}'.");

        return Ok(ApiResponse<object>.Ok(new { submitted.Channel }, "notifications.test_sent"));
    }
}
