namespace AdaPanel.Core.Dtos.Php;

/// <summary>Configuration alone is not proof of an IIS PHP request writing a log.</summary>
public sealed class PhpErrorLoggingDto
{
    public string Status { get; set; } = "unverified";
    public string LogPath { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
    public int CacheTtlSeconds { get; set; }
    public DateTime? CheckedAtUtc { get; set; }
    public bool Managed { get; set; }
    public long RollAtBytes { get; set; } = 50L * 1024 * 1024;
    public string Retention { get; set; } = "indefinite";
}
