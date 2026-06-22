namespace AdaPanel.Core.Dtos.Ssl;

public sealed class SslWorkspaceDto
{
    public string SiteName { get; set; } = string.Empty;

    public string[] HostNames { get; set; } = [];

    public SslCertificateDto[] Certificates { get; set; } = [];

    /// <summary>HTTP→HTTPS kalıcı yönlendirme (Force HTTPS) bu sitede etkin mi.</summary>
    public bool HttpsRedirectEnabled { get; set; }
}
