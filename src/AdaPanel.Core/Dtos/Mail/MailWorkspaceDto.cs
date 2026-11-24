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

    /// <summary>
    /// Alan adi olusturma sirasinda tamamlanamayan YAN adimlar (<see cref="MailDomainWarnings"/>).
    /// Bos = her sey yapildi. Alan adi olusmus olsa bile doludur: "olustu" ile "eksiksiz
    /// olustu" ayni sey degildir.
    /// </summary>
    public IReadOnlyList<string> Warnings { get; set; } = [];

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

/// <summary>Alan adi olusturmanin uyari kodlari (arayuz bu kodlari ceviriyle gosterir).</summary>
public static class MailDomainWarnings
{
    /// <summary>MX/SPF/DKIM/DMARC kayitlari panel DNS bolgesine yazilamadi; elle eklenmeli.</summary>
    public const string DnsNotPublished = "dns_not_published";

    /// <summary>Gonderen sahteciligi kurali yazilamadi ya da sunucuya yuklenmedi.</summary>
    public const string SenderPolicyNotApplied = "sender_policy_not_applied";

    /// <summary>Silme tamamlandi ama otomatik yanit/liste politikasi kayitlari temizlenemedi.</summary>
    public const string StateCleanupFailed = "state_cleanup_failed";
}
