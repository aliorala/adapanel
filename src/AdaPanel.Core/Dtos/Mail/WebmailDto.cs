namespace AdaPanel.Core.Dtos.Mail;

/// <summary>Tek tık webmail (Roundcube) açma sonucu.</summary>
public sealed class WebmailOpenDto
{
    /// <summary>Tarayıcıda açılacak webmail adresi.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>One-use opaque handoff, submitted only as an HTTPS form body.</summary>
    public string? HandoffToken { get; set; }
}

public sealed class WebmailOpenRequest
{
    public string? Domain { get; set; }
    public string? Address { get; set; }
}
