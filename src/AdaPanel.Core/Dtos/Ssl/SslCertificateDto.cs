namespace AdaPanel.Core.Dtos.Ssl;

public sealed class SslCertificateDto
{
    public string SiteName { get; set; } = string.Empty;

    public string HostName { get; set; } = string.Empty;

    public string Thumbprint { get; set; } = string.Empty;

    public string FriendlyName { get; set; } = string.Empty;

    public string Subject { get; set; } = string.Empty;

    public DateTime NotAfterUtc { get; set; }

    public string Source { get; set; } = string.Empty;

    public bool IsHttpsBindingPresent { get; set; }

    public int Port { get; set; }
}
