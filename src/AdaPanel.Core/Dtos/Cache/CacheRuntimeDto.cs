namespace AdaPanel.Core.Dtos.Cache;

public sealed class CacheRuntimeDto
{
    public string ComponentId { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string ServiceName { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public string InstallDir { get; set; } = string.Empty;

    public string ExecutablePath { get; set; } = string.Empty;

    public int DefaultPort { get; set; }

    public bool IsInstalled { get; set; }

    public bool IsServiceRegistered { get; set; }

    /// <summary>Yalnız Redis çalışıyorken doldurulur (RESP INFO); aksi halde null.</summary>
    public CacheMetricsDto? Metrics { get; set; }
}
