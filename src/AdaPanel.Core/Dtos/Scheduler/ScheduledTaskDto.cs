namespace AdaPanel.Core.Dtos.Scheduler;

public sealed class ScheduledTaskDto
{
    public string Name { get; set; } = string.Empty;

    public string ScheduleType { get; set; } = string.Empty;

    public string State { get; set; } = string.Empty;

    public string Command { get; set; } = string.Empty;

    public string Arguments { get; set; } = string.Empty;

    public string WorkingDirectory { get; set; } = string.Empty;

    public DateTime? NextRunTimeUtc { get; set; }

    public DateTime? LastRunTimeUtc { get; set; }
}
