using System.ComponentModel.DataAnnotations;

namespace AdaPanel.Core.Dtos.Mail;

/// <summary>Mail sunucusuna (Stalwart) yüklenen TLS sertifikasının durumu.</summary>
public sealed class MailCertificateDto
{
    public bool Active { get; set; }

    public string Domain { get; set; } = string.Empty;

    public string HostName { get; set; } = string.Empty;

    /// <summary>letsencrypt | self-signed</summary>
    public string Source { get; set; } = string.Empty;

    public DateTime? NotAfterUtc { get; set; }

    /// <summary>
    /// Daha önce kaydedilmiş Let's Encrypt iletişim e-postası. UI bunu otomatik doldurur
    /// ve bilindiğinde sertifikayı elle uğraştırmadan otomatik almak için kullanır.
    /// </summary>
    public string SuggestedContactEmail { get; set; } = string.Empty;
}

public sealed class IssueMailCertificateRequest
{
    [Required]
    [StringLength(255)]
    public string Domain { get; init; } = string.Empty;

    /// <summary>Boşsa mail.&lt;domain&gt; kullanılır.</summary>
    [StringLength(255)]
    public string HostName { get; init; } = string.Empty;

    [StringLength(255)]
    public string ContactEmail { get; init; } = string.Empty;

    public bool UseStaging { get; init; }
}
