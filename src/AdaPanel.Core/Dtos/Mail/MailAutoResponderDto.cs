using System.ComponentModel.DataAnnotations;

namespace AdaPanel.Core.Dtos.Mail;

/// <summary>Bir posta kutusunun otomatik yanıt (autoresponder) durumu.</summary>
public sealed class MailAutoResponderDto
{
    public string Address { get; set; } = string.Empty;

    public bool Enabled { get; set; }

    public string Subject { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;
}

public sealed class SetMailAutoResponderRequest
{
    [Required]
    [EmailAddress]
    [StringLength(256)]
    public string Address { get; set; } = string.Empty;

    public bool Enabled { get; set; }

    [StringLength(256)]
    public string Subject { get; set; } = string.Empty;

    [StringLength(4000)]
    public string Message { get; set; } = string.Empty;
}
