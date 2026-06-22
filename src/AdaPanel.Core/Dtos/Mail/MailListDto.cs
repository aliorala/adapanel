namespace AdaPanel.Core.Dtos.Mail;

/// <summary>
/// Dağıtım listesi (Stalwart mailing-list principal'ı): tek bir adrese (name@domain) gelen
/// posta, recipients listesindeki tüm alıcılara dağıtılır.
/// </summary>
public sealed class MailListDto
{
    public string Address { get; set; } = string.Empty;

    public IReadOnlyCollection<string> Recipients { get; set; } = [];
}
