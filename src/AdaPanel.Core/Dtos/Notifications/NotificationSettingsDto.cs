namespace AdaPanel.Core.Dtos.Notifications;

public sealed class NotificationSettingsDto
{
    public string SettingsRevision { get; set; } = string.Empty;

    public string SmtpHost { get; set; } = string.Empty;

    public int SmtpPort { get; set; } = 587;

    public string SmtpUserName { get; set; } = string.Empty;

    public string SmtpPassword { get; set; } = string.Empty;

    public bool HasSmtpPassword { get; set; }

    public string FromEmail { get; set; } = string.Empty;

    public string ToEmail { get; set; } = string.Empty;

    public bool UseSsl { get; set; } = true;

    public string WebhookUrl { get; set; } = string.Empty;

    /// <summary>
    /// Webhook imza anahtarı (HMAC-SHA256). Diskte <c>ISecretProtector</c> ile korunur; istemciye
    /// düz gider ki alıcı taraf imzayı doğrulayabilsin. Boşsa ilk kayıt/gönderimde otomatik üretilir.
    /// İstek DTO'sunda YOK: istemci anahtarı belirleyemez.
    /// </summary>
    public string WebhookSecret { get; set; } = string.Empty;
}

/// <summary>Webhook anahtarı döndürme sonucu: tam değer YALNIZ bu yanıtta bulunur.</summary>
public sealed class WebhookSecretRotationDto
{
    public string WebhookSecret { get; init; } = string.Empty;

    public string Masked { get; init; } = string.Empty;
}
