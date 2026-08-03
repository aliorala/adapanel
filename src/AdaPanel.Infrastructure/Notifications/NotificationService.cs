using System.Net;
using System.Text;
using System.Text.Json;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Notifications;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Support;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

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

        await SendViaSmtpAsync(settings, settings.ToEmail, subject, body, cancellationToken);
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

        await SendViaSmtpAsync(settings, toEmail.Trim(), subject, body, cancellationToken);
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

    /// <summary>
    /// SMTP gönderiminin TEK yolu. Şifreleme kipi PORTA göre seçilir.
    /// <para>
    /// Neden MailKit: panel önceden <c>System.Net.Mail.SmtpClient</c> kullanıyordu ve o sınıf ÖRTÜK
    /// TLS'i (port 465) hiç desteklemez — <c>EnableSsl</c> orada yalnız STARTTLS demektir. 465 yazan
    /// kullanıcı düz metin bağlanıyor, sunucu TLS beklediği için el sıkışma kopuyor ve panel
    /// "TLS el sıkışması başarısız" diyordu. 465 hemen her sağlayıcının belgelediği porttur, yani
    /// bu ayarı yapanların çoğu doğrudan bu duvara çarpıyordu. Microsoft da SmtpClient'ı yeni
    /// geliştirmeler için önermiyor ve MailKit'e yönlendiriyor.
    /// </para>
    /// <para>
    /// STARTTLS bilerek ZORUNLU (WhenAvailable değil): kullanıcı şifreleme istediğinde, sunucu
    /// STARTTLS duyurmuyorsa sessizce düz metne düşmek parolayı ağa açık göndermek olurdu.
    /// </para>
    /// </summary>
    private async Task SendViaSmtpAsync(
        NotificationSettingsDto settings,
        string toEmail,
        string subject,
        string body,
        CancellationToken cancellationToken)
    {
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(settings.FromEmail));
        message.To.Add(MailboxAddress.Parse(toEmail));
        message.Subject = subject;
        message.Body = new TextPart("plain") { Text = body };

        var options = ResolveSecureSocketOptions(settings.UseSsl, settings.SmtpPort);

        using var client = new SmtpClient();
        try
        {
            await client.ConnectAsync(settings.SmtpHost, settings.SmtpPort, options, cancellationToken);

            if (!string.IsNullOrWhiteSpace(settings.SmtpUserName))
            {
                await client.AuthenticateAsync(
                    settings.SmtpUserName,
                    GetPlaintextPassword(settings.SmtpPassword),
                    cancellationToken);
            }

            await client.SendAsync(message, cancellationToken);
        }
        catch (System.Security.Authentication.AuthenticationException exception)
        {
            // El sıkışma hatasının EN SIK sebebi port/kip uyuşmazlığıdır. Çıplak "TLS el sıkışması
            // başarısız" mesajı kullanıcıya ne yapacağını söylemez; hangi kipin denendiği ve doğru
            // eşleşmenin ne olduğu yazılır.
            throw new AppException(
                "notifications.smtp_tls_failed",
                400,
                400,
                $"{settings.SmtpHost}:{settings.SmtpPort} ({options}) - {exception.Message}");
        }
        finally
        {
            if (client.IsConnected)
            {
                await client.DisconnectAsync(true, cancellationToken);
            }
        }
    }

    /// <summary>
    /// Port + "şifreleme açık mı" bilgisinden kip seçer. 465 örtük TLS içindir (bağlantı daha ilk
    /// bayttan şifrelidir), 587/25 ise düz metin başlayıp STARTTLS ile yükselir. Bu ikisi
    /// birbirinin yerine kullanılamaz; karıştırıldığında el sıkışma kopar.
    /// </summary>
    internal static SecureSocketOptions ResolveSecureSocketOptions(bool useSsl, int port)
    {
        if (!useSsl)
        {
            return SecureSocketOptions.None;
        }

        return port == 465 ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls;
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
