namespace AdaPanel.Core.Dtos.Notifications;

public sealed class NotificationSettingsDto
{
    public string SmtpHost { get; set; } = string.Empty;

    public int SmtpPort { get; set; } = 587;

    public string SmtpUserName { get; set; } = string.Empty;

    public string SmtpPassword { get; set; } = string.Empty;

    public bool HasSmtpPassword { get; set; }

    public string FromEmail { get; set; } = string.Empty;

    public string ToEmail { get; set; } = string.Empty;

    public bool UseSsl { get; set; } = true;

    public string WebhookUrl { get; set; } = string.Empty;
}
