using System.ComponentModel.DataAnnotations;

namespace AdaPanel.Core.Dtos.AppRuntimes;

public sealed class HostNodeAppRequest
{
    [Required]
    [StringLength(260, MinimumLength = 2)]
    public string StartupFile { get; init; } = "server.js";

    [StringLength(1024)]
    public string WorkingDirectory { get; init; } = ".";

    [StringLength(2048)]
    public string Arguments { get; init; } = string.Empty;

    [StringLength(128)]
    public string ComponentId { get; init; } = "nodejs-lts";

    public string[] EnvironmentVariables { get; init; } = [];
}
