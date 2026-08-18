using System.ComponentModel.DataAnnotations;

namespace AdaPanel.Core.Dtos.Logs;

/// <summary>
/// Politika güncelleme isteği.
/// </summary>
/// <remarks>
/// Sınırlar MODELDE duruyor, ekranda değil: ekran doğrulaması yalnızca tarayıcıyı bağlar, uç
/// doğrudan çağrılabilir. Üst sınırlar da gerçek: <see cref="KeepCount"/> için 3650, saklamanın
/// "sınırsız"ı 0 ile ifade edildiği için ayrıca büyük bir sayıya gerek bırakmaz.
/// </remarks>
public sealed class UpdateLogRotationSettingsRequest
{
    public bool Enabled { get; set; } = true;

    public bool Compress { get; set; } = true;

    [Range(0, 3650)]
    public int SiteLogMaxAgeDays { get; set; } = 30;

    [Range(0, 3650)]
    public int PanelLogMaxAgeDays { get; set; } = 2;

    [Range(0, 1024 * 1024)]
    public int MaxFileSizeMB { get; set; }

    [Range(0, 3650)]
    public int SiteKeepCount { get; set; }

    /// <summary>Panel günlüğü arşivleri; varsayılanı 14 tutmanın gerekçesi DTO'da yazılı.</summary>
    [Range(0, 3650)]
    public int PanelKeepCount { get; set; } = 14;

    [Range(0, 1024 * 1024)]
    public int MaxTotalSizeMB { get; set; }

    [Range(1, 24 * 30)]
    public int IntervalHours { get; set; } = 24;
}
