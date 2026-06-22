namespace AdaPanel.Core.Dtos.Mail;

/// <summary>Bir posta kutusunun otomatik yanıt (autoresponder) durumu.</summary>
public sealed class MailAutoResponderDto
{
    public string Address { get; set; } = string.Empty;

    public bool Enabled { get; set; }

    public string Message { get; set; } = string.Empty;
}

public sealed class SetMailAutoResponderRequest
{
    public string Address { get; set; } = string.Empty;

    public bool Enabled { get; set; }

    public string Message { get; set; } = string.Empty;
}
