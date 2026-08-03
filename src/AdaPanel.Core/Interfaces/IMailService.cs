using AdaPanel.Core.Dtos.Mail;

namespace AdaPanel.Core.Interfaces;

/// <summary>
/// Stalwart tabanlı e-posta barındırma yönetimi: alan adı, hesap, alias, kota
/// ve domain DNS kayıtları. stalwart-cli üzerinden çalışır.
/// </summary>
public interface IMailService
{
    /// <summary>Domain için gerekli DNS kayıtlarını (MX/SPF/DKIM/DMARC/A) üretir.</summary>
    Task<MailDnsDto> GetDnsRecordsAsync(string domain, CancellationToken cancellationToken = default);

    /// <summary>
    /// Domainin yayınlanmış MX/A/SPF/DKIM/DMARC kayıtlarını ve PTR'yi canlı sorgulayıp
    /// panelin beklediğiyle karşılaştırır (ok/fail/unknown). Eksikleri kullanıcıya gösterir.
    /// </summary>
    Task<MailDiagnosticsDto> GetDiagnosticsAsync(string domain, CancellationToken cancellationToken = default);

    /// <summary>Mail sunucusundaki TLS sertifikası durumunu döner.</summary>
    Task<MailCertificateDto> GetCertificateStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>Mail hostname'i için Let's Encrypt (yoksa self-signed) sertifika alır ve Stalwart'a yükler.</summary>
    Task<MailCertificateDto> IssueCertificateAsync(IssueMailCertificateRequest request, CancellationToken cancellationToken = default);

    /// <summary>win-acme yenilemesi sonrası PEM dosyaları değiştiyse sertifikayı Stalwart'a yeniden yükler.</summary>
    Task<MailCertificateDto?> SyncCertificateAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Posta sunucusunun SMTP karşılamasında kullandığı ana adı, sertifikanın adıyla AYNI değere
    /// getirir. Bir değişiklik yapıldıysa true döner.
    /// <para>
    /// Neden önemli: ayarlanmadığında Stalwart makine adına düşer (ör. "vm-185-7-242-225") ve bu
    /// bir FQDN bile değildir. Gmail ve Outlook, karşılama adının çözülmesini ve ters DNS ile
    /// tutarlı olmasını bekler; tutmuyorsa posta spam'e düşer ya da reddedilir. Ana adın
    /// SERTİFİKAYLA aynı olması şart değil ama doğrusu budur: karşılama, sertifika ve PTR aynı adı
    /// göstermelidir.
    /// </para>
    /// </summary>
    Task<bool> EnsureServerHostnameAsync(CancellationToken cancellationToken = default);

    Task<MailWorkspaceDto> GetWorkspaceAsync(string? domain, CancellationToken cancellationToken = default);

    Task<MailWorkspaceDto> ConfigureAsync(ConfigureMailRequest request, CancellationToken cancellationToken = default);

    Task<MailWorkspaceDto> CreateDomainAsync(CreateMailDomainRequest request, CancellationToken cancellationToken = default);

    Task<MailWorkspaceDto> DeleteDomainAsync(string domainName, CancellationToken cancellationToken = default);

    Task<MailWorkspaceDto> CreateAccountAsync(CreateMailAccountRequest request, CancellationToken cancellationToken = default);

    Task<MailWorkspaceDto> DeleteAccountAsync(string address, CancellationToken cancellationToken = default);

    Task<MailWorkspaceDto> SetAccountPasswordAsync(SetMailAccountPasswordRequest request, CancellationToken cancellationToken = default);

    Task<MailWorkspaceDto> CreateAliasAsync(CreateMailAliasRequest request, CancellationToken cancellationToken = default);

    Task<MailWorkspaceDto> DeleteAliasAsync(string address, CancellationToken cancellationToken = default);

    /// <summary>Dağıtım listesi (mailing-list) oluşturur.</summary>
    Task<MailWorkspaceDto> CreateMailListAsync(CreateMailListRequest request, CancellationToken cancellationToken = default);

    /// <summary>Dağıtım listesinin alıcı listesini günceller.</summary>
    Task<MailWorkspaceDto> SetMailListRecipientsAsync(SetMailListRecipientsRequest request, CancellationToken cancellationToken = default);

    /// <summary>Dağıtım listesini siler.</summary>
    Task<MailWorkspaceDto> DeleteMailListAsync(string address, CancellationToken cancellationToken = default);

    /// <summary>Mail sunucusunun ağ dinleyicileri (hangi adres:port'larda dinliyor) — salt-okunur.</summary>
    Task<IReadOnlyCollection<MailListenerDto>> GetListenersAsync(CancellationToken cancellationToken = default);

    Task<MailWorkspaceDto> SetDomainQuotaAsync(SetMailDomainQuotaRequest request, CancellationToken cancellationToken = default);

    Task<MailWorkspaceDto> SetAccountQuotaAsync(SetMailAccountQuotaRequest request, CancellationToken cancellationToken = default);

    /// <summary>Bir posta kutusunun otomatik yanıt (autoresponder) durumunu okur.</summary>
    Task<MailAutoResponderDto> GetAutoResponderAsync(string address, CancellationToken cancellationToken = default);

    /// <summary>Bir posta kutusunun otomatik yanıtını açar/kapatır ve mesajını ayarlar.</summary>
    Task<MailAutoResponderDto> SetAutoResponderAsync(SetMailAutoResponderRequest request, CancellationToken cancellationToken = default);

    Task<MailSpamSettingsDto> GetSpamSettingsAsync(CancellationToken cancellationToken = default);

    Task<MailSpamSettingsDto> UpdateSpamSettingsAsync(UpdateMailSpamSettingsRequest request, CancellationToken cancellationToken = default);
}
