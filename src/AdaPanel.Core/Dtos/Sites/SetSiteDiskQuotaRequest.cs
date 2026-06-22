using System.ComponentModel.DataAnnotations;

namespace AdaPanel.Core.Dtos.Sites;

public sealed class SetSiteDiskQuotaRequest
{
    /// <summary>Kota üst sınırı (MB). En az 1 MB.</summary>
    [Range(1, 1024L * 1024L)]
    public long LimitMegabytes { get; set; } = 1024;

    /// <summary>true ise yalnızca raporlar, yazmayı engellemez.</summary>
    public bool SoftLimit { get; set; }
}
