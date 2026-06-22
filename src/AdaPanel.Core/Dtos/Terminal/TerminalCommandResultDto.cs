namespace AdaPanel.Core.Dtos.Terminal;

public sealed class TerminalCommandResultDto
{
    public int ExitCode { get; init; }

    public string StandardOutput { get; init; } = string.Empty;

    public string StandardError { get; init; } = string.Empty;

    public string WorkingDirectory { get; init; } = string.Empty;

    public long DurationMilliseconds { get; init; }

    public bool TimedOut { get; init; }
}
