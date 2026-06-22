namespace AdaPanel.Core.Dtos.Scheduler;

public sealed class CreateScheduledTaskRequest
{
    public string Name { get; set; } = string.Empty;

    public string Command { get; set; } = string.Empty;

    public string Arguments { get; set; } = string.Empty;

    public string WorkingDirectory { get; set; } = string.Empty;

    public string ScheduleType { get; set; } = "hourly";

    public int Interval { get; set; } = 1;

    public string TimeOfDay { get; set; } = "00:00";
}
