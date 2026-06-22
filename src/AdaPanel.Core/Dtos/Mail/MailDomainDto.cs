namespace AdaPanel.Core.Dtos.Mail;

public sealed class MailDomainDto
{
    public string Name { get; set; } = string.Empty;

    public bool Active { get; set; }

    public int AccountCount { get; set; }

    /// <summary>Domain icin azami toplam boyut (MB). 0 = sinirsiz.</summary>
    public int MaxSizeMB { get; set; }
}
