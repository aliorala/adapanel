using System.ComponentModel.DataAnnotations;

namespace AdaPanel.Core.Dtos.Mail;

public sealed class MailServerIdentityDto
{
    public bool Supported { get; set; }
    public string ConfiguredHostName { get; set; } = string.Empty;
    public string CurrentHostName { get; set; } = string.Empty;
    public string Domain { get; set; } = string.Empty;
    public string ContactEmail { get; set; } = string.Empty;
    public bool Pinned { get; set; }
    public bool Applied { get; set; }
    public List<MailServerIdentityStepDto> Steps { get; set; } = [];
    public string PtrRequest { get; set; } = string.Empty;

    public static MailServerIdentityDto Unsupported() => new()
    {
        Steps = [new() { Key = "provider", Status = "unsupported", Message = "Bu posta sağlayıcısında kalıcı sunucu kimliği değişikliği desteklenmiyor." }]
    };
}

public sealed class MailServerIdentityStepDto
{
    public string Key { get; set; } = string.Empty;
    public string Status { get; set; } = "unknown";
    public string Message { get; set; } = string.Empty;
}

public sealed class SetMailServerIdentityRequest
{
    [Required, StringLength(253)]
    public string Domain { get; init; } = string.Empty;
    [Required, StringLength(253)]
    public string HostName { get; init; } = string.Empty;
    [StringLength(255)]
    public string ContactEmail { get; init; } = string.Empty;
}
