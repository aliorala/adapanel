namespace AdaPanel.Core.Dtos.Ssl;

public sealed class IssueLetsEncryptRequest
{
    public string SiteName { get; set; } = string.Empty;

    public string HostName { get; set; } = string.Empty;

    public string ContactEmail { get; set; } = string.Empty;

    public bool UseStaging { get; set; }
}
