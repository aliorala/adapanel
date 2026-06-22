namespace AdaPanel.Core.Dtos.Mail;

/// <summary>Tek tık webmail (Roundcube) açma sonucu.</summary>
public sealed class WebmailOpenDto
{
    /// <summary>Tarayıcıda açılacak webmail adresi.</summary>
    public string Url { get; set; } = string.Empty;
}
