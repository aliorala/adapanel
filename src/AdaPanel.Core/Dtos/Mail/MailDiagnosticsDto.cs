namespace AdaPanel.Core.Dtos.Mail;

/// <summary>
/// Mail teşhisi: panel, domainin yayınlanmış DNS kayıtlarını (MX/A/SPF/DKIM/DMARC) ve PTR'yi
/// canlı sorgulayıp beklenenle karşılaştırır. Kullanıcı neyin eksik olduğunu kendisi aramak
/// zorunda kalmaz — panel söyler.
/// </summary>
public sealed class MailDiagnosticsDto
{
    public string Domain { get; set; } = string.Empty;

    public string MailHost { get; set; } = string.Empty;

    public string ServerAddress { get; set; } = string.Empty;

    public MailDiagnosticCheckDto[] Checks { get; set; } = [];
}

public sealed class MailDiagnosticCheckDto
{
    /// <summary>mx | a | spf | dkim | dmarc | ptr</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>ok | fail | unknown</summary>
    public string Status { get; set; } = "unknown";

    /// <summary>Olması gereken (panelin ürettiği) değer.</summary>
    public string Expected { get; set; } = string.Empty;

    /// <summary>DNS'te bulunan gerçek değer (boşsa kayıt yok).</summary>
    public string Found { get; set; } = string.Empty;
}
