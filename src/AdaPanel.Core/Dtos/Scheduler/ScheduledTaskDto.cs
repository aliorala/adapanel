namespace AdaPanel.Core.Dtos.Scheduler;

public sealed class ScheduledTaskDto
{
    public string Name { get; set; } = string.Empty;

    public string ScheduleType { get; set; } = string.Empty;

    public string State { get; set; } = string.Empty;

    /// <summary>Kayıtlı eylemden türetilir: "url" (curl), "php" (php.exe) ya da "command".</summary>
    public string TaskType { get; set; } = "command";

    /// <summary>url tipinde çağrılan adres (eylem argümanlarından çözülür).</summary>
    public string Url { get; set; } = string.Empty;

    public string Command { get; set; } = string.Empty;

    public string Arguments { get; set; } = string.Empty;

    public string WorkingDirectory { get; set; } = string.Empty;

    public DateTime? NextRunTimeUtc { get; set; }

    public DateTime? LastRunTimeUtc { get; set; }
}
