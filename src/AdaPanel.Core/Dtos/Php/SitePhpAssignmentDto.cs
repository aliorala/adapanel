using AdaPanel.Core.Dtos.Asp;

namespace AdaPanel.Core.Dtos.Php;

public sealed class SitePhpAssignmentDto
{
    public string SiteName { get; init; } = string.Empty;

    public string ComponentId { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public string Version { get; init; } = string.Empty;

    public string ExecutablePath { get; init; } = string.Empty;

    public string HandlerName { get; init; } = string.Empty;

    public bool IsFastCgiRegistered { get; init; }

    /// <summary>
    /// Handler commit'ten SONRA gercekten StaticFile'dan once mi? UC DEGERLI; varsayilan OLCULMEDI.
    /// </summary>
    public HandlerOrderMeasurement HandlerOrder { get; init; } = HandlerOrderMeasurement.Olculemedi();

    /// <summary>Her sey olculdu ve yolundaysa bos; aksi halde uyari anahtari.</summary>
    public string HandlerOrderNoticeKey => HandlerOrder.NoticeKey;
}
