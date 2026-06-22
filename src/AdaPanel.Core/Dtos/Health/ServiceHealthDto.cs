namespace AdaPanel.Core.Dtos.Health;

/// <summary>Bir Windows servisinin sağlık durumu (Plesk Server Health / Repair Kit benzeri).</summary>
public sealed class ServiceHealthDto
{
    public string Name { get; set; } = string.Empty;

    public string Label { get; set; } = string.Empty;

    /// <summary>Running | Stopped | NotInstalled | Unknown</summary>
    public string Status { get; set; } = string.Empty;
}
