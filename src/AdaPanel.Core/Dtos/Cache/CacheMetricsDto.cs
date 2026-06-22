namespace AdaPanel.Core.Dtos.Cache;

/// <summary>Redis canlı metrikleri (RESP INFO'dan; salt-okunur).</summary>
public sealed class CacheMetricsDto
{
    public string Version { get; set; } = string.Empty;

    public string UsedMemory { get; set; } = string.Empty;

    public long Keys { get; set; }

    public int ConnectedClients { get; set; }

    public long Hits { get; set; }

    public long Misses { get; set; }

    public long UptimeSeconds { get; set; }
}
