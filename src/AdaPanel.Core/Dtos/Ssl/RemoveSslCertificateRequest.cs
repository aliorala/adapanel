namespace AdaPanel.Core.Dtos.Ssl;

/// <summary>Bir sitenin HTTPS binding'ini (ve panel kaydını) kaldırır; sonradan yeniden kurulabilir.</summary>
public sealed class RemoveSslCertificateRequest
{
    public string SiteName { get; set; } = string.Empty;

    public string HostName { get; set; } = string.Empty;

    public int Port { get; set; } = 443;
}
