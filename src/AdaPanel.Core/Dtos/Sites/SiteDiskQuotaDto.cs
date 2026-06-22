namespace AdaPanel.Core.Dtos.Sites;

/// <summary>
/// Bir sitenin uygulama kökü için Windows FSRM disk kotası durumu.
/// </summary>
public sealed class SiteDiskQuotaDto
{
    public string SiteName { get; set; } = string.Empty;

    public string Path { get; set; } = string.Empty;

    /// <summary>FSRM rolü/cmdlet'leri sunucuda mevcut mu.</summary>
    public bool FsrmAvailable { get; set; }

    public bool QuotaEnabled { get; set; }

    public long LimitBytes { get; set; }

    public long UsedBytes { get; set; }

    /// <summary>Yumuşak kota yalnızca raporlar; sert kota yazmayı engeller.</summary>
    public bool SoftLimit { get; set; }
}
