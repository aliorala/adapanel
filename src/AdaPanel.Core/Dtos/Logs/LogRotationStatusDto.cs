namespace AdaPanel.Core.Dtos.Logs;

/// <summary>Politika + son turun sonucu; panelin "Log döndürme" ekranının tek veri kaynağı.</summary>
public sealed class LogRotationStatusDto
{
    public LogRotationSettingsDto Settings { get; set; } = new();

    public DateTime? LastRunUtc { get; set; }

    /// <summary>Politikaya göre bir sonraki turun en erken zamanı.</summary>
    public DateTime? NextRunUtc { get; set; }

    public IReadOnlyCollection<LogRotationTargetDto> Targets { get; set; } = [];

    /// <summary>Tüm hedeflerin son turlarında kazanılan toplam yer (bayt).</summary>
    public long TotalFreedBytes { get; set; }
}
