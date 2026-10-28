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

    /// <summary>Inspect the selected domain, without substituting another domain's saved certificate.</summary>
    Task<MailCertificateDto> GetCertificateStatusForDomainAsync(string domain, CancellationToken cancellationToken = default)
        => GetCertificateStatusAsync(cancellationToken);

    Task SyncWebCertificateAsync(string hostName, Func<CancellationToken, Task>? beforeMutation = null,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <summary>Mevcut güvenilir sertifikayı kullanır veya Let's Encrypt alır; Stalwart'a yükleyip SMTP TLS'i doğrular.</summary>
    Task<MailCertificateDto> IssueCertificateAsync(IssueMailCertificateRequest request, CancellationToken cancellationToken = default);

    /// <summary>win-acme yenilemesi sonrası PEM dosyaları değiştiyse sertifikayı Stalwart'a yeniden yükler.</summary>
    Task<MailCertificateDto?> SyncCertificateAsync(CancellationToken cancellationToken = default);

    async Task<MailCertificateDto?> SyncCertificateAsync(Func<CancellationToken, Task> beforeMutation,
        CancellationToken cancellationToken = default)
    {
        await beforeMutation(cancellationToken);
        return await SyncCertificateAsync(cancellationToken);
    }

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
    /// <param name="applyChanges">
    /// false ise HİÇBİR ŞEY YAZILMAZ, yalnızca "değişmesi gerekiyor mu" ölçülür. Denetim bunu
    /// kullanır: eksikliği görmeyen bir denetim, onarma düğmesinin hiç çıkmamasına ve dolayısıyla
    /// sorunun kalıcı olmasına yol açar.
    /// </param>
    Task<bool> EnsureServerHostnameAsync(bool applyChanges = true, CancellationToken cancellationToken = default);

    Task<MailServerIdentityDto> GetServerIdentityAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(MailServerIdentityDto.Unsupported());

    Task<MailServerIdentityDto> SetServerIdentityAsync(SetMailServerIdentityRequest request, CancellationToken cancellationToken = default)
        => Task.FromResult(MailServerIdentityDto.Unsupported());

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

    /// <summary>
    /// Dağıtım listesine EK ADRES (alias) ekler: o adrese gelen posta da aynı abonelere dağıtılır.
    /// </summary>
    /// <remarks>
    /// <b>NEDEN ARAYÜZDE:</b> ilk sürümde bu iki uç <c>MailController</c>'dan somut
    /// <c>MailService</c>'e gidiyordu — özellik Stalwart'a özgü olduğu için "geçici" sayılmıştı.
    /// Bedeli ölçülebilirdi: denetleyici bir ALTYAPI sınıfına bağlanıyor, MailEnable kurulu
    /// sunucuda <c>MailServiceRouter</c> devre dışı kalıyor ve "desteklenmiyor" cevabını
    /// denetleyicinin kendisi, statik bir <c>MailService.IsInstalled()</c> çağrısıyla üretmek
    /// zorunda kalıyordu. Diğer bütün liste uçları (<c>CreateMailListAsync</c> …) zaten buradan
    /// geçiyor; ek adresler onlardan ayrı durmak için hiçbir sebep taşımıyor.
    /// <para>
    /// Desteklenmeyen posta sunucusunda uygulama <c>mail.lists_unsupported</c> (409) atar —
    /// diğer liste metotlarıyla AYNI sözleşme.
    /// </para>
    /// </remarks>
    Task<MailWorkspaceDto> AddMailListAliasAsync(MailListAliasRequest request, CancellationToken cancellationToken = default);

    /// <summary>Dağıtım listesinden bir ek adresi (alias) kaldırır.</summary>
    Task<MailWorkspaceDto> RemoveMailListAliasAsync(MailListAliasRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Mail sunucusunun ağ dinleyicileri (hangi adres:port'larda dinliyor) — salt-okunur.
    /// Dönüş, listeyi VE listenin ölçülüp ölçülmediğini taşır: boş liste tek başına
    /// "dinleyici yok" demek DEĞİLDİR (bkz. <see cref="MailListenerSnapshotDto"/>).
    /// </summary>
    Task<MailListenerSnapshotDto> GetListenersAsync(CancellationToken cancellationToken = default);

    Task<MailWorkspaceDto> SetDomainQuotaAsync(SetMailDomainQuotaRequest request, CancellationToken cancellationToken = default);

    /// <summary>Bir alan adının GİDEN posta limitini okur (0 = limitsiz).</summary>
    Task<MailOutboundLimitDto> GetOutboundLimitAsync(string domain, CancellationToken cancellationToken = default);

    /// <summary>
    /// Bir alan adına saatlik GİDEN posta limiti koyar; 0 verilirse limit kaldırılır.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Neden gerekli:</b> hacklenen ya da kötüye kullanan TEK bir müşteri, sunucunun IP'sini
    /// kara listeye düşürür ve o sunucudaki HERKESİN postası teslim edilmez olur. Panelde giden
    /// posta için hiçbir sınır yoktu.
    /// </para>
    /// <para>
    /// Uygulama mail sunucusunun kendi hız sınırlayıcısıyla yapılır (<c>MtaOutboundThrottle</c>,
    /// <c>senderDomain</c> anahtarı) — yani sınır gerçekten TESLİMATTA uygulanır, panel sonradan
    /// sayıp uyarmakla yetinmez. Şema canlı sunucudan ölçülerek doğrulandı.
    /// </para>
    /// </remarks>
    Task<MailOutboundLimitDto> SetOutboundLimitAsync(string domain, int messagesPerHour, CancellationToken cancellationToken = default);

    Task<MailWorkspaceDto> SetAccountQuotaAsync(SetMailAccountQuotaRequest request, CancellationToken cancellationToken = default);

    /// <summary>Bir posta kutusunun otomatik yanıt (autoresponder) durumunu okur.</summary>
    Task<MailAutoResponderDto> GetAutoResponderAsync(string address, CancellationToken cancellationToken = default);

    /// <summary>Bir posta kutusunun otomatik yanıtını açar/kapatır ve mesajını ayarlar.</summary>
    Task<MailAutoResponderDto> SetAutoResponderAsync(SetMailAutoResponderRequest request, CancellationToken cancellationToken = default);

    Task<MailSpamSettingsDto> GetSpamSettingsAsync(CancellationToken cancellationToken = default);

    Task<MailSpamSettingsDto> UpdateSpamSettingsAsync(UpdateMailSpamSettingsRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Giden posta kuyruğunu listeler (en yeniden eskiye).
    /// </summary>
    /// <param name="limit">Listeye alınacak azami ileti sayısı; toplam sayı ayrıca bildirilir.</param>
    Task<MailQueueDto> GetQueueAsync(int limit, CancellationToken cancellationToken = default);

    /// <summary>Tek bir kuyruk iletisinin ayrıntısı; ileti yoksa <c>null</c>.</summary>
    Task<MailQueueMessageDto?> GetQueueMessageAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Kuyruktaki iletiyi siler (teslim edilmez). Silinecek ileti yoksa <c>false</c>.</summary>
    Task<bool> DeleteQueueMessageAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Giden posta rölesi (smarthost) ayarını okur. Parola ASLA dönmez.</summary>
    Task<MailRelayDto> GetRelayAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Giden posta rölesini kurar ya da kaldırır. Parola panelde DPAPI ile şifreli saklanır,
    /// yanıtta dönmez. Değişikliğin sunucuda gerçekten uygulandığı geri okunarak DOĞRULANIR.
    /// </summary>
    Task<MailRelayDto> SetRelayAsync(SetMailRelayRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Bir alan adının posta istemcisi ayarları (autodiscover/autoconfig kaynağı).
    /// Alan adı panelde tanımlı değilse ya da ayarlar ölçülemiyorsa <c>null</c> döner —
    /// böylece uç, uydurma değer yayınlamak yerine 404 verebilir.
    /// </summary>
    Task<MailClientSettingsDto?> GetClientSettingsAsync(string domain, CancellationToken cancellationToken = default);
}
