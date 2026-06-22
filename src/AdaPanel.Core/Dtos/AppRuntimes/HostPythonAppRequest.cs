using System.ComponentModel.DataAnnotations;

namespace AdaPanel.Core.Dtos.AppRuntimes;

public sealed class HostPythonAppRequest
{
    [Required]
    [StringLength(260, MinimumLength = 2)]
    public string StartupFile { get; init; } = "app.py";

    [StringLength(1024)]
    public string WorkingDirectory { get; init; } = ".";

    [StringLength(2048)]
    public string Arguments { get; init; } = string.Empty;

    [StringLength(128)]
    public string ComponentId { get; init; } = "python-3";

    public string[] EnvironmentVariables { get; init; } = [];
}
