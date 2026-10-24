namespace AdaPanel.Core.Dtos.Mail;

/// <summary>
/// Mail (hMailServer) yönetim çalışma alanı: kurulum/yapılandırma durumu,
/// alan adları ve seçili alan için hesap/alias listesi.
/// </summary>
public sealed class MailWorkspaceDto
{
    public string Provider { get; set; } = string.Empty;
    /// <summary>hMailServer COM bileşeni bu sunucuda mevcut mu.</summary>
    public bool Installed { get; set; }

    /// <summary>Panel hMailServer yönetici parolasıyla kimlik doğrulayabiliyor mu.</summary>
    public bool Configured { get; set; }

    public string SelectedDomain { get; set; } = string.Empty;

    public IReadOnlyCollection<MailDomainDto> Domains { get; set; } = [];

    public IReadOnlyCollection<MailAccountDto> Accounts { get; set; } = [];

    /// <summary>
    /// <see cref="Accounts"/> GERÇEKTEN ÖLÇÜLDÜ MÜ. Boş liste tek başına "kutu yok" demek
    /// değildir: sorgu hiç koşmamış da olabilir.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Kota dayatan iki yer bu ayrımı yapmak ZORUNDADIR, çünkü ölçülemeyen bir listeyi "0 kutu"
    /// saymak kotayı sessizce KALDIRIR: <c>MailController.EnsureMailboxQuotaAsync</c> (kutu
    /// sayısı) ve <c>MailService.EnsureDomainAllocationAsync</c> (tahsis edilen disk toplamı).
    /// Karar tek yerde: <see cref="MailQuotaMeasurement"/>.
    /// </para>
    /// <para>
    /// <b>Varsayılan <c>false</c>'tur ve öyle kalmalıdır:</b> alan adı seçilmemiş ya da posta
    /// yapılandırılmamış her yolda hesap listesi doldurulmaz; bayrağın kendiliğinden "ölçüldü"
    /// demesi, ölçmediğimiz bir şeye güvenmek olurdu.
    /// </para>
    /// </remarks>
    public bool AccountsMeasured { get; set; }

    public IReadOnlyCollection<MailAliasDto> Aliases { get; set; } = [];

    /// <summary>Seçili alan adına ait dağıtım listeleri (Stalwart mailing-list).</summary>
    public IReadOnlyCollection<MailListDto> Lists { get; set; } = [];

    /// <summary>False for partial mutation responses that measured mailboxes but not lists.</summary>
    public bool ListsMeasured { get; set; } = true;

    /// <summary>Bir sorun varsa (örn. COM erişilemedi) kullanıcıya gösterilecek tanı mesajı.</summary>
    public string StatusMessage { get; set; } = string.Empty;
}
