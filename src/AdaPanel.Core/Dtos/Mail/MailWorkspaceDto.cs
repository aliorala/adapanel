namespace AdaPanel.Core.Dtos.Mail;

/// <summary>
/// Mail (hMailServer) yönetim çalışma alanı: kurulum/yapılandırma durumu,
/// alan adları ve seçili alan için hesap/alias listesi.
/// </summary>
public sealed class MailWorkspaceDto
{
    /// <summary>hMailServer COM bileşeni bu sunucuda mevcut mu.</summary>
    public bool Installed { get; set; }

    /// <summary>Panel hMailServer yönetici parolasıyla kimlik doğrulayabiliyor mu.</summary>
    public bool Configured { get; set; }

    public string SelectedDomain { get; set; } = string.Empty;

    public IReadOnlyCollection<MailDomainDto> Domains { get; set; } = [];

    public IReadOnlyCollection<MailAccountDto> Accounts { get; set; } = [];

    public IReadOnlyCollection<MailAliasDto> Aliases { get; set; } = [];

    /// <summary>Seçili alan adına ait dağıtım listeleri (Stalwart mailing-list).</summary>
    public IReadOnlyCollection<MailListDto> Lists { get; set; } = [];

    /// <summary>Bir sorun varsa (örn. COM erişilemedi) kullanıcıya gösterilecek tanı mesajı.</summary>
    public string StatusMessage { get; set; } = string.Empty;
}
