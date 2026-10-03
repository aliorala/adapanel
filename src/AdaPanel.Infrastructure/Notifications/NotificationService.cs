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
    ISecretProtector secretProtector,
    // İSTEĞE BAĞLI: "iç ağ hedeflerine izin ver" açık kapısı. Verilmemişse KAPALI sayılır.
    Microsoft.Extensions.Options.IOptions<Options.PanelRuntimeOptions>? runtimeOptions = null,
    // İSTEĞE BAĞLI: adres çözümleyici. Testte sahte — birim testi gerçek DNS'e çıkmamalı.
    OutboundAddressGuard.OutboundHostResolver? outboundResolver = null) : INotificationService
{
    private const string SettingsPath = "config/notifications.json";

    internal Func<ISmtpClient> SmtpClientFactory { get; init; } = static () => new SmtpClient();
    internal Func<CancellationToken, Task>? BeforeSettingsWrite { get; init; }
    internal Func<IPAddress[], int, CancellationToken, ValueTask<Stream>>? SmtpConnector { get; init; }
    internal Func<CancellationToken, Task>? BeforeTestDispatch { get; init; }

    /// <summary>
    /// Bildirim ayarlarının işaret ettiği iki dış hedefi (webhook adresi ve SMTP konağı) denetler.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Yüzey neden var:</b> her iki alan da HTTP istek gövdesinden gelir ve tek yaptıkları
    /// <c>.Trim()</c>'di. <c>SendWebhookAsync</c> adrese POST atar, <c>SendViaSmtpAsync</c>
    /// <c>SmtpHost:SmtpPort</c>'a TCP açar — yani ikisi de sunucunun ağından çıkar.
    /// </para>
    /// <para>
    /// Kaydetme denetimi geçersiz ayara erken yanıt verir. Gönderimde HTTP istemcisi ve SMTP
    /// akış bağlantısı hedefi yeniden denetler; kaydetmeden sonra değişen DNS güvenilmez.
    /// </para>
    /// <para>
    /// <b>AÇIK KAPI BURADA ÖZELLİKLE ÖNEMLİ.</b> İç ağdaki bir SMTP aktarıcısı (ya da bu panelin
    /// KENDİ makinesindeki hMailServer/Stalwart) tamamen meşru ve YAYGIN bir yapılandırmadır;
    /// bayrak olmasaydı bu değişiklik çalışan posta kurulumlarını kırardı. Bayrak açıkken denetim
    /// hiç uygulanmaz.
    /// </para>
    /// <para>
    /// <b>ESKİ DÜRÜST SINIR — KAPATILDI.</b> Webhook gönderimi <c>CreateClient()</c> ile VARSAYILAN
    /// istemciyi kullanıyordu ve orada <c>AllowAutoRedirect</c> .NET varsayılanı olan
    /// <c>true</c>'dur (ölçüldü: <c>ServiceCollectionExtensions</c> yalnız adlı istemcilerde
    /// <c>false</c> yapıyordu), yani 302 ile iç ağa atlama bu kapının arkasından geçiyordu. Gönderim
    /// artık <see cref="OutboundConnectGuard.GuardedClientName"/> istemcisini kullanıyor: zincirin
    /// her adımı soket açılmadan denetleniyor.
    /// </para>
    /// <para>
    /// SMTP de <see cref="OutboundConnectGuard"/> üzerinden çözülmüş adreslere
    /// bağlanır. MailKit hazır akışı alır; TLS kimliği için özgün konak adı korunur ve yeniden
    /// DNS çözümlemesi yapılmaz. MailKit'in varsayılan sertifika doğrulaması değiştirilmez.
    /// </para>
    /// </remarks>
    private async Task EnsureNotificationTargetsAllowedAsync(
        string webhookUrl,
        string smtpHost,
        CancellationToken cancellationToken)
    {
        var policy = OutboundTargetPolicy.Connecting(runtimeOptions?.Value.AllowInternalOutboundTargets ?? false);
        var resolver = outboundResolver ?? OutboundAddressGuard.SystemResolver;

        if (!string.IsNullOrWhiteSpace(webhookUrl))
        {
            if (!Uri.TryCreate(webhookUrl, UriKind.Absolute, out var adres) ||
                (adres.Scheme != Uri.UriSchemeHttp && adres.Scheme != Uri.UriSchemeHttps))
            {
                throw new AppException("notifications.webhook_invalid", 400, 400);
            }

            var engellenenWebhook = await OutboundAddressGuard.FindBlockedHostAsync(
                adres.DnsSafeHost, resolver, policy, cancellationToken);
            if (engellenenWebhook is not null)
            {
                throw new AppException("notifications.webhook_forbidden", 403, 403, engellenenWebhook);
            }
        }

        if (!string.IsNullOrWhiteSpace(smtpHost))
        {
            // SMTP konağı ÇIPLAK bir dizedir (URL değil); sayısal IPv4 yazımlarını
            // `IPAddress.TryParse` yakalıyor (ölçüldü, gerekçe OutboundAddressGuard'da).
            var engellenenSmtp = await OutboundAddressGuard.FindBlockedHostAsync(
                smtpHost, resolver, policy, cancellationToken);
            if (engellenenSmtp is not null)
            {
                throw new AppException("notifications.smtp_host_forbidden", 403, 403, engellenenSmtp);
            }
        }
    }

    public async Task<NotificationSettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        var settings = await ReadStoredSettingsAsync(cancellationToken);
        return ToClientSettings(settings);
    }

    public async Task<NotificationSettingsDto> UpdateSettingsAsync(UpdateNotificationSettingsRequest request, CancellationToken cancellationToken = default)
    {
        // Freeze the whole submission before DNS or file I/O can yield. The saved target
        // must be the one that passed validation, even for non-HTTP callers sharing a DTO.
        var settings = new NotificationSettingsDto
        {
            SmtpHost = request.SmtpHost?.Trim() ?? string.Empty,
            SmtpPort = request.SmtpPort,
            SmtpUserName = request.SmtpUserName?.Trim() ?? string.Empty,
            FromEmail = request.FromEmail?.Trim() ?? string.Empty,
            ToEmail = request.ToEmail?.Trim() ?? string.Empty,
            UseSsl = request.UseSsl,
            WebhookUrl = request.WebhookUrl?.Trim() ?? string.Empty
        };
        var submittedPassword = request.SmtpPassword;
        if (settings.SmtpPort is < 1 or > 65535 ||
            (settings.FromEmail.Length > 0 && !MailboxAddress.TryParse(settings.FromEmail, out _)) ||
            (settings.ToEmail.Length > 0 && !MailboxAddress.TryParse(settings.ToEmail, out _)))
        {
            throw new AppException("notifications.settings_invalid", 400, 400);
        }

        await EnsureNotificationTargetsAllowedAsync(settings.WebhookUrl, settings.SmtpHost, cancellationToken);
        var protectedPassword = string.IsNullOrWhiteSpace(submittedPassword)
            ? null
            : secretProtector.Protect(submittedPassword);

        if (BeforeSettingsWrite is not null) await BeforeSettingsWrite(cancellationToken);
        var updated = await fileStore.UpdateAsync(SettingsPath, new NotificationSettingsDto(), current =>
        {
            // A blank password means keep the latest stored secret. Reading it under the
            // publication lock prevents another request's password rotation being undone.
            settings.SmtpPassword = protectedPassword ?? current.SmtpPassword ?? string.Empty;
            settings.HasSmtpPassword = !string.IsNullOrWhiteSpace(settings.SmtpPassword);
            settings.SettingsRevision = SettingsRevision.New();
            return settings;
        }, cancellationToken);
        return ToClientSettings(updated);
    }

    public async Task SendEmailAsync(string subject, string body, CancellationToken cancellationToken = default)
    {
        var settings = await ReadStoredSettingsAsync(cancellationToken);
        await SendEmailAsync(settings, subject, body, cancellationToken);
    }

    private async Task SendEmailAsync(NotificationSettingsDto settings, string subject, string body, CancellationToken cancellationToken)
    {
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
        await SendWebhookAsync(settings, eventName, body, cancellationToken);
    }

    private async Task SendWebhookAsync(NotificationSettingsDto settings, string eventName, string body, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.WebhookUrl))
        {
            throw new AppException("notifications.webhook_not_configured", 400, 400);
        }

        // DENETLENEN İSTEMCİ: adres kaydetme anında denetlendi ama gönderme anında yönlendirme ya da
        // değişmiş bir DNS kaydı hedefi başka yere taşıyabilir. Bu istemcide her bağlantı ayrı denetlenir.
        var client = httpClientFactory.CreateClient(OutboundConnectGuard.GuardedClientName);

        HttpResponseMessage response;
        try
        {
            response = await client.PostAsync(
                settings.WebhookUrl,
                new StringContent(JsonSerializer.Serialize(new
                {
                    eventName,
                    body,
                    sentAtUtc = DateTime.UtcNow
                }), Encoding.UTF8, "application/json"),
                cancellationToken);
        }
        // ZİNCİR TARANIR, tür yakalanmaz: .NET geri çağrıdan atılan istisnayı kendi
        // HttpRequestException'ının içine sarar (ölçüldü — bkz. OutboundBlockedException.Cikar).
        catch (Exception exception) when (OutboundBlockedException.Cikar(exception) is { } blocked)
        {
            // "Ağ hatası" demek yanıltıcı olurdu: kullanıcı adresini düzeltemez, çünkü adres
            // kaydedilirken TEMİZDİ. Kapının kendi hata kodu, açık kapı ipucunu da taşıyor.
            throw new AppException("notifications.webhook_forbidden", 403, 403, blocked.BlockedTarget);
        }

        using var atilacakYanit = response;

        if (!response.IsSuccessStatusCode)
        {
            throw new AppException("notifications.webhook_failed", (int)response.StatusCode, (int)response.StatusCode);
        }
    }

    public async Task TestAsync(TestNotificationRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var channel = request.Channel.Trim().ToLowerInvariant();
        var message = string.IsNullOrWhiteSpace(request.Message)
            ? "AdaPanel test bildirimi"
            : request.Message.Trim();
        var expectedRevision = request.ExpectedSettingsRevision;
        if (channel is not ("email" or "webhook"))
            throw new AppException("notifications.channel_invalid", 400, 400);

        // HTTP callers must submit a revision. Trusted legacy callers may omit it, but all
        // callers dispatch exactly this measured snapshot instead of rereading latest settings.
        var settings = await ReadStoredSettingsAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(expectedRevision) &&
            !string.Equals(expectedRevision, GetRevision(settings), StringComparison.Ordinal))
            throw new AppException("notifications.settings_changed", 409, 409);

        if (BeforeTestDispatch is not null) await BeforeTestDispatch(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        switch (channel)
        {
            case "email":
                await SendEmailAsync(settings, "AdaPanel test", message, cancellationToken);
                break;
            case "webhook":
                await SendWebhookAsync(settings, "adapanel.test", message, cancellationToken);
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
            SettingsRevision = GetRevision(settings),
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

    private static string GetRevision(NotificationSettingsDto settings) => SettingsRevision.GetOrCompute(
        settings.SettingsRevision,
        new
        {
            SmtpHost = settings.SmtpHost?.Trim() ?? string.Empty,
            settings.SmtpPort,
            SmtpUserName = settings.SmtpUserName?.Trim() ?? string.Empty,
            // Use the stored value only. Legacy plaintext is protected by the helper's
            // process-private HMAC key; no decryption occurs while forming the revision.
            StoredCredential = settings.SmtpPassword ?? string.Empty,
            FromEmail = settings.FromEmail?.Trim() ?? string.Empty,
            ToEmail = settings.ToEmail?.Trim() ?? string.Empty,
            settings.UseSsl,
            WebhookUrl = settings.WebhookUrl?.Trim() ?? string.Empty
        });

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

        var client = SmtpClientFactory();
        Stream? transport = null;
        try
        {
            var endpoint = new DnsEndPoint(settings.SmtpHost, settings.SmtpPort);
            var policy = OutboundTargetPolicy.Connecting(runtimeOptions?.Value.AllowInternalOutboundTargets ?? false);
            var resolver = outboundResolver ?? OutboundAddressGuard.SystemResolver;
            // Preserve MailKit's hostname-connect timeout for the DNS/socket phase that
            // now happens outside MailKit. Its TLS/SMTP phase keeps the original token
            // and client timeout; an expired connect timer must not cancel an active send.
            using (var connectionTimeout = new CancellationTokenSource(client.Timeout))
            using (var connection = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, connectionTimeout.Token))
            {
                try
                {
                    transport = SmtpConnector is null
                        ? await OutboundConnectGuard.ConnectAsync(endpoint, policy, resolver, null, connection.Token)
                        : await OutboundConnectGuard.ConnectAsync(endpoint, policy, resolver, null, connection.Token, null, SmtpConnector);
                    connection.Token.ThrowIfCancellationRequested();
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && connectionTimeout.IsCancellationRequested)
                {
                    throw new TimeoutException("SMTP connection timed out.");
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            await client.ConnectAsync(transport, settings.SmtpHost, settings.SmtpPort, options, cancellationToken);

            if (!string.IsNullOrWhiteSpace(settings.SmtpUserName))
            {
                await client.AuthenticateAsync(
                    settings.SmtpUserName,
                    GetPlaintextPassword(settings.SmtpPassword),
                    cancellationToken);
            }

            await client.SendAsync(message, cancellationToken);
        }
        catch (Exception exception) when (OutboundBlockedException.Cikar(exception) is { } blocked)
        {
            throw new AppException("notifications.smtp_host_forbidden", 403, 403, blocked.BlockedTarget);
        }
        catch (SmtpCommandException exception) when (IsSenderRejected(exception))
        {
            // GÖNDEREN REDDEDİLDİ — ve bu, panelin KENDİ postasını sessizce öldürebilecek tek hata.
            //
            // Sebep ölçüldü: posta sunucusu artık kimlik doğrulaması OLMAYAN bir oturumun,
            // sunucunun KENDİ barındırdığı bir alan adı adına göndermesine izin vermiyor
            // (gerekçe: Mail/MailSenderPolicy — kiracıdan kiracıya sahteciliği kapatan kural).
            // Panel SMTP'si "localhost:25, kullanıcı adı boş, gönderen kendi alan adımız" diye
            // ayarlanmışsa parola sıfırlama e-postası tam da buradan düşer.
            //
            // Parola sıfırlama akışı hatayı BİLEREK yutar (kullanıcı numaralandırmasını önlemek
            // için), yani çıplak bırakılırsa arıza HİÇBİR YERDE görünmez. Bu yüzden hata burada
            // NE YAPILACAĞINI söyleyen bir anahtara çevriliyor: anahtar bildirim ayarlarındaki
            // "test" düğmesinde kullanıcıya, parola sıfırlamada ise günlüğe çıkar.
            throw new AppException(
                "notifications.smtp_sender_rejected",
                400,
                400,
                $"{settings.FromEmail} via {settings.SmtpHost}:{settings.SmtpPort} - {exception.Message}");
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
            // SMTP acceptance is determined by SendAsync, not QUIT. Cleanup must neither
            // turn an accepted message into a retryable failure nor replace a send error.
            // No QUIT round trip, and no canceled request token during socket cleanup.
            try
            {
                if (client.IsConnected)
                    await client.DisconnectAsync(false, CancellationToken.None);
            }
            catch (Exception) { /* Disposal below still releases transport resources. */ }
            finally
            {
                try { client.Dispose(); }
                catch (Exception) { /* Preserve the measured delivery outcome. */ }
                // MailKit normally owns the stream after ConnectAsync. Also close our
                // reference when connect/cancellation fails before ownership is transferred.
                try { transport?.Dispose(); }
                catch (Exception) { /* Cleanup cannot replace acceptance or the original error. */ }
            }
        }
    }

    /// <summary>
    /// SMTP hatası "gönderen adresi kabul edilmedi" mi?
    /// </summary>
    /// <remarks>
    /// <para>
    /// İki ölçüte birden bakılıyor çünkü tek başına ikisi de yetmiyor. MailKit
    /// <see cref="SmtpErrorCode.SenderNotAccepted"/>'ı yalnız MAIL FROM'a gelen yanıt için üretir
    /// — doğru sinyal budur. Ama gerçek sunucular aynı reddi başka bir aşamada da verebiliyor,
    /// bu yüzden gelişmiş durum kodu <c>5.7.1</c> (yetki reddi) de kabul ediliyor.
    /// </para>
    /// <para>
    /// <b>Neden metin eşleşmesi YOK:</b> ret metni sunucuya ve diline göre değişir; "not allowed"
    /// aramak bir sunucuda tutar, diğerinde sessizce ıskalardı — bu depoda ölçülmüş hata sınıfı.
    /// </para>
    /// </remarks>
    internal static bool IsSenderRejected(SmtpCommandException exception) =>
        exception.ErrorCode == SmtpErrorCode.SenderNotAccepted
        || exception.StatusCode is SmtpStatusCode.MailboxNameNotAllowed or SmtpStatusCode.MailboxUnavailable
            && exception.Message.Contains("5.7.1", StringComparison.Ordinal);

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
