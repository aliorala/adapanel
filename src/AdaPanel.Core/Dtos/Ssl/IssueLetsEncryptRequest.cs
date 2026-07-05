namespace AdaPanel.Core.Dtos.Ssl;

public sealed class IssueLetsEncryptRequest
{
    public string SiteName { get; set; } = string.Empty;

    public string HostName { get; set; } = string.Empty;

    public string ContactEmail { get; set; } = string.Empty;

    public bool UseStaging { get; set; }

    /// <summary>
    /// true ise *.domain + domain için wildcard sertifika istenir (DNS-01; alan adının DNS bölgesi
    /// panelin Windows DNS'inde barındırılıyor olmalı). Yalnız yönetici kullanabilir.
    /// </summary>
    public bool Wildcard { get; set; }
}
