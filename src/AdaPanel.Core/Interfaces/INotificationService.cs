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
}
