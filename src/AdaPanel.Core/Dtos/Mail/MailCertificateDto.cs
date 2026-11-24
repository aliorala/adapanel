using System.ComponentModel.DataAnnotations;

namespace AdaPanel.Core.Dtos.Mail;

/// <summary>Mail sunucusuna (Stalwart) yüklenen TLS sertifikasının durumu.</summary>
public sealed class MailCertificateDto
{
    public bool Active { get; set; }

    public string Domain { get; set; } = string.Empty;

    public string HostName { get; set; } = string.Empty;

    /// <summary>windows-store | letsencrypt | self-signed (legacy)</summary>
    public string Source { get; set; } = string.Empty;

    public DateTime? NotAfterUtc { get; set; }

    /// <summary>
    /// Daha önce kaydedilmiş Let's Encrypt iletişim e-postası. UI bunu otomatik doldurur
    /// ve bilindiğinde sertifikayı elle uğraştırmadan otomatik almak için kullanır.
    /// </summary>
    public string SuggestedContactEmail { get; set; } = string.Empty;

    public string CertificateId { get; set; } = string.Empty;
    public string DefaultCertificateId { get; set; } = string.Empty;
    public string ConfigurationStatus { get; set; } = "unknown";
    public MailTlsEndpointDto[] Endpoints { get; set; } = [];
    public MailTlsEndpointDto? WebCertificate { get; set; }
}

public sealed class MailTlsEndpointDto
{
    public int Port { get; set; }
    public string Protocol { get; set; } = string.Empty;
    public string Status { get; set; } = "unknown";
    public string Subject { get; set; } = string.Empty;
    public string Issuer { get; set; } = string.Empty;
    public string[] SubjectAlternativeNames { get; set; } = [];
    public DateTime? NotBeforeUtc { get; set; }
    public string[] ChainStatus { get; set; } = [];
    public string Thumbprint { get; set; } = string.Empty;
    public DateTime? NotAfterUtc { get; set; }
    public string Detail { get; set; } = string.Empty;
    public DateTime CheckedAtUtc { get; set; }
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

    public bool ReuseExistingOnly { get; init; }
}
