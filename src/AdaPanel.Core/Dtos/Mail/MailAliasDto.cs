namespace AdaPanel.Core.Dtos.Mail;

public sealed class MailAliasDto
{
    public string Address { get; set; } = string.Empty;

    public string Target { get; set; } = string.Empty;

    public bool Active { get; set; }
}
