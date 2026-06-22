using System.ComponentModel.DataAnnotations;

namespace AdaPanel.Core.Dtos.Terminal;

public sealed class ExecuteTerminalCommandRequest
{
    [Required]
    [StringLength(8000, MinimumLength = 1)]
    public string Command { get; init; } = string.Empty;

    [StringLength(1024)]
    public string WorkingDirectory { get; init; } = string.Empty;

    [Range(1, 60)]
    public int TimeoutSeconds { get; init; } = 30;
}
