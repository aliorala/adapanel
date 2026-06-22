namespace AdaPanel.Core.Dtos.Ssl;

public sealed class ImportCertificateRequest
{
    public string SiteName { get; set; } = string.Empty;

    public string HostName { get; set; } = string.Empty;

    public string FileName { get; set; } = string.Empty;

    public string Base64Content { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    public int Port { get; set; } = 443;
}
