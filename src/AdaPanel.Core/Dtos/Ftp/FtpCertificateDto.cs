namespace AdaPanel.Core.Dtos.Ftp;

public sealed class ApplyFtpCertificateRequest
{
    public string HostName { get; set; } = string.Empty;
}

public sealed class FtpCertificateDto
{
    public string? HostName { get; set; }
    public string? CertificateThumbprint { get; set; }
    public string? CertificateSubject { get; set; }
    public DateTime? CertificateNotAfterUtc { get; set; }
    public string? CertificateStoreName { get; set; }
    public bool? CertificateVerified { get; set; }
    public string? CertificateDetail { get; set; }
}
