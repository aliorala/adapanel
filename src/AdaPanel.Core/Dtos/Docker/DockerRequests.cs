using System.ComponentModel.DataAnnotations;

namespace AdaPanel.Core.Dtos.Docker;

public sealed class PullImageRequest
{
    [Required]
    [StringLength(256, MinimumLength = 1)]
    public string Image { get; set; } = string.Empty;
}

public sealed class RunContainerRequest
{
    [Required]
    [StringLength(256, MinimumLength = 1)]
    public string Image { get; set; } = string.Empty;

    [StringLength(128)]
    public string Name { get; set; } = string.Empty;

    /// <summary>Port eslemeleri, virgulle: "8080:80,8443:443".</summary>
    public string Ports { get; set; } = string.Empty;

    /// <summary>Ortam degiskenleri, satir/virgulle: "KEY=VALUE".</summary>
    public string Environment { get; set; } = string.Empty;

    /// <summary>--restart unless-stopped uygulansin mi.</summary>
    public bool AutoRestart { get; set; } = true;
}

public sealed class DockerLogsDto
{
    public string ContainerId { get; set; } = string.Empty;
    public string Output { get; set; } = string.Empty;
}
