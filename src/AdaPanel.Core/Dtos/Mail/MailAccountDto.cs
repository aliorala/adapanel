namespace AdaPanel.Core.Dtos.Mail;

public sealed class MailAccountDto
{
    public string Address { get; set; } = string.Empty;

    public bool Active { get; set; }

    public int MaxSizeMB { get; set; }

    /// <summary>Posta kutusunun mevcut kullanimi (MB).</summary>
    public int CurrentSizeMB { get; set; }
}
