using AdaPanel.Core.Dtos.Notifications;

namespace AdaPanel.Core.Interfaces;

public interface INotificationService
{
    Task<NotificationSettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default);

    Task<NotificationSettingsDto> UpdateSettingsAsync(UpdateNotificationSettingsRequest request, CancellationToken cancellationToken = default);

    Task SendEmailAsync(string subject, string body, CancellationToken cancellationToken = default);

    /// <summary>Yapılandırılmış SMTP ile belirli bir alıcıya e-posta gönderir (hoş geldin, parola sıfırlama vb.).</summary>
    Task SendEmailToAsync(string toEmail, string subject, string body, CancellationToken cancellationToken = default);

    /// <summary>SMTP gönderimi yapılandırılmış mı (host + from dolu).</summary>
    Task<bool> IsEmailConfiguredAsync(CancellationToken cancellationToken = default);

    Task SendWebhookAsync(string eventName, string body, CancellationToken cancellationToken = default);

    Task TestAsync(TestNotificationRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Webhook imza anahtarını (X-AdaPanel-Signature HMAC sırrı) YENİDEN ÜRETİR ve yeni değeri
    /// döndürür. Eski anahtar hemen geçersiz olur: alıcı tarafın da güncellenmesi gerekir.
    /// Varsayılan gövde (uygulamayan test sahteleri için) açıkça desteklenmez der.
    /// </summary>
    Task<string> RotateWebhookSecretAsync(CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
}
