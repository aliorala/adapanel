namespace AdaPanel.Core.Dtos.Mail;

public sealed class MailDomainDto
{
    public string Name { get; set; } = string.Empty;

    public bool Active { get; set; }

    public int AccountCount { get; set; }

    /// <summary>Domain icin azami toplam boyut (MB). 0 = sinirsiz. Panel-tarafi tahsis kotasi (mail.json'da tutulur).</summary>
    public int MaxSizeMB { get; set; }

    /// <summary>Bu domaindeki posta kutularina tahsis edilen toplam kota (MB) — panel domain kotasini buna gore dayatir.</summary>
    public int AllocatedMB { get; set; }
}
