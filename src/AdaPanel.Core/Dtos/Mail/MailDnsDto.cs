namespace AdaPanel.Core.Dtos.Mail;

/// <summary>Bir mail domaini için dış DNS sağlayıcısına eklenmesi gereken kayıt.</summary>
public sealed class MailDnsRecordDto
{
    public string Type { get; set; } = "TXT";

    /// <summary>Zone'a göre host adı ("@", "mail", "_dmarc", "selector._domainkey" gibi).</summary>
    public string Host { get; set; } = "@";

    public string Value { get; set; } = string.Empty;

    public int? Priority { get; set; }

    /// <summary>Kaydın amacı: a | mx | spf | dkim | dmarc.</summary>
    public string Purpose { get; set; } = string.Empty;
}

public sealed class MailDnsDto
{
    public string Domain { get; set; } = string.Empty;

    public string MailHost { get; set; } = string.Empty;

    /// <summary>Panel ayarlarındaki sunucu adresi (boşsa A kaydı değeri kullanıcıdan beklenir).</summary>
    public string ServerAddress { get; set; } = string.Empty;

    public MailDnsRecordDto[] Records { get; set; } = [];
}

public sealed class ApplyMailDnsResultDto
{
    public string ZoneName { get; set; } = string.Empty;

    public int AppliedCount { get; set; }
}
