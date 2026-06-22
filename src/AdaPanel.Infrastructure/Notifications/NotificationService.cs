using System.Net;
using System.Net.Mail;
using System.Text;
using System.Text.Json;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Notifications;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Support;

namespace AdaPanel.Infrastructure.Notifications;

public sealed class NotificationService(
    JsonFileStore fileStore,
    IHttpClientFactory httpClientFactory,
    ISecretProtector secretProtector) : INotificationService
{
    private const string SettingsPath = "config/notifications.json";

    public async Task<NotificationSettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        var settings = await ReadStoredSettingsAsync(cancellationToken);
        return ToClientSettings(settings);
    }

    public async Task<NotificationSettingsDto> UpdateSettingsAsync(UpdateNotificationSettingsRequest request, CancellationToken cancellationToken = default)
    {
        var current = await ReadStoredSettingsAsync(cancellationToken);
        var protectedPassword = string.IsNullOrWhiteSpace(request.SmtpPassword)
            ? current.SmtpPassword
            : secretProtector.Protect(request.SmtpPassword);

        var settings = new NotificationSettingsDto
        {
            SmtpHost = request.SmtpHost.Trim(),
            SmtpPort = request.SmtpPort,
            SmtpUserName = request.SmtpUserName.Trim(),
            SmtpPassword = protectedPassword,
            HasSmtpPassword = !string.IsNullOrWhiteSpace(protectedPassword),
            FromEmail = request.FromEmail.Trim(),
            ToEmail = request.ToEmail.Trim(),
            UseSsl = request.UseSsl,
            WebhookUrl = request.WebhookUrl.Trim()
        };

        await fileStore.WriteAsync(SettingsPath, settings, cancellationToken);
        return ToClientSettings(settings);
    }

    public async Task SendEmailAsync(string subject, string body, CancellationToken cancellationToken = default)
    {
        var settings = await ReadStoredSettingsAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(settings.SmtpHost) ||
            string.IsNullOrWhiteSpace(settings.FromEmail) ||
            string.IsNullOrWhiteSpace(settings.ToEmail))
        {
            throw new AppException("notifications.smtp_not_configured", 400, 400);
        }

        using var client = new SmtpClient(settings.SmtpHost, settings.SmtpPort)
        {
            EnableSsl = settings.UseSsl,
            Credentials = string.IsNullOrWhiteSpace(settings.SmtpUserName)
                ? CredentialCache.DefaultNetworkCredentials
                : new NetworkCredential(settings.SmtpUserName, GetPlaintextPassword(settings.SmtpPassword))
        };

        using var message = new MailMessage(settings.FromEmail, settings.ToEmail, subject, body)
        {
            BodyEncoding = Encoding.UTF8,
            SubjectEncoding = Encoding.UTF8
        };

        await client.SendMailAsync(message, cancellationToken);
    }

    public async Task SendEmailToAsync(string toEmail, string subject, string body, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(toEmail))
        {
            throw new AppException("notifications.recipient_required", 400, 400);
        }

        var settings = await ReadStoredSettingsAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(settings.SmtpHost) || string.IsNullOrWhiteSpace(settings.FromEmail))
        {
            throw new AppException("notifications.smtp_not_configured", 400, 400);
        }

        using var client = new SmtpClient(settings.SmtpHost, settings.SmtpPort)
        {
            EnableSsl = settings.UseSsl,
            Credentials = string.IsNullOrWhiteSpace(settings.SmtpUserName)
                ? CredentialCache.DefaultNetworkCredentials
                : new NetworkCredential(settings.SmtpUserName, GetPlaintextPassword(settings.SmtpPassword))
        };

        using var message = new MailMessage(settings.FromEmail, toEmail.Trim(), subject, body)
        {
            BodyEncoding = Encoding.UTF8,
            SubjectEncoding = Encoding.UTF8
        };

        await client.SendMailAsync(message, cancellationToken);
    }

    public async Task<bool> IsEmailConfiguredAsync(CancellationToken cancellationToken = default)
    {
        var settings = await ReadStoredSettingsAsync(cancellationToken);
        return !string.IsNullOrWhiteSpace(settings.SmtpHost) && !string.IsNullOrWhiteSpace(settings.FromEmail);
    }

    public async Task SendWebhookAsync(string eventName, string body, CancellationToken cancellationToken = default)
    {
        var settings = await ReadStoredSettingsAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(settings.WebhookUrl))
        {
            throw new AppException("notifications.webhook_not_configured", 400, 400);
        }

        var client = httpClientFactory.CreateClient();
        using var response = await client.PostAsync(
            settings.WebhookUrl,
            new StringContent(JsonSerializer.Serialize(new
            {
                eventName,
                body,
                sentAtUtc = DateTime.UtcNow
            }), Encoding.UTF8, "application/json"),
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new AppException("notifications.webhook_failed", (int)response.StatusCode, (int)response.StatusCode);
        }
    }

    public async Task TestAsync(TestNotificationRequest request, CancellationToken cancellationToken = default)
    {
        var channel = request.Channel.Trim().ToLowerInvariant();
        var message = string.IsNullOrWhiteSpace(request.Message)
            ? "AdaPanel test bildirimi"
            : request.Message.Trim();

        switch (channel)
        {
            case "email":
                await SendEmailAsync("AdaPanel test", message, cancellationToken);
                break;
            case "webhook":
                await SendWebhookAsync("adapanel.test", message, cancellationToken);
                break;
            default:
                throw new AppException("notifications.channel_invalid", 400, 400);
        }
    }

    private Task<NotificationSettingsDto> ReadStoredSettingsAsync(CancellationToken cancellationToken)
    {
        return fileStore.ReadAsync(SettingsPath, new NotificationSettingsDto(), cancellationToken);
    }

    private static NotificationSettingsDto ToClientSettings(NotificationSettingsDto settings)
    {
        return new NotificationSettingsDto
        {
            SmtpHost = settings.SmtpHost,
            SmtpPort = settings.SmtpPort,
            SmtpUserName = settings.SmtpUserName,
            SmtpPassword = string.Empty,
            HasSmtpPassword = !string.IsNullOrWhiteSpace(settings.SmtpPassword),
            FromEmail = settings.FromEmail,
            ToEmail = settings.ToEmail,
            UseSsl = settings.UseSsl,
            WebhookUrl = settings.WebhookUrl
        };
    }

    private string GetPlaintextPassword(string storedPassword)
    {
        if (string.IsNullOrWhiteSpace(storedPassword))
        {
            return string.Empty;
        }

        try
        {
            return secretProtector.Unprotect(storedPassword);
        }
        catch
        {
            // Existing installations may still contain the legacy plaintext value.
            return storedPassword;
        }
    }
}
