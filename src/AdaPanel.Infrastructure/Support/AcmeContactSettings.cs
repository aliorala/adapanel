namespace AdaPanel.Infrastructure.Support;

/// <summary>
/// Let's Encrypt iletişim e-postasını panel genelinde paylaşmak için kalıcı ayar.
/// Site SSL'i alınırken (WinAcmeSslService) kaydedilir; mail sertifikası otomatik
/// issuance'ta (MailService) tekrar kullanılır — böylece kullanıcı e-postayı bir kez
/// girer, sonraki sertifikalar elle uğraştırmadan otomatik alınır.
/// </summary>
public sealed class AcmeContactSettings
{
    public const string Path = "config/acme-contact.json";

    public string Email { get; set; } = string.Empty;
}
