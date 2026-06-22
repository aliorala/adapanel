namespace AdaPanel.Infrastructure.Support;

internal sealed class CommandResult
{
    public int ExitCode { get; init; }

    public string StandardOutput { get; init; } = string.Empty;

    public string StandardError { get; init; } = string.Empty;

    public bool Success => ExitCode == 0;
}
