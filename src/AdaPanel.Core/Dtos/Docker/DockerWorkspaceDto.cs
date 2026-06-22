namespace AdaPanel.Core.Dtos.Docker;

public sealed class DockerContainerDto
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Image { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Ports { get; set; } = string.Empty;
}

public sealed class DockerImageDto
{
    public string Id { get; set; } = string.Empty;
    public string Repository { get; set; } = string.Empty;
    public string Tag { get; set; } = string.Empty;
    public string Size { get; set; } = string.Empty;
}

public sealed class DockerWorkspaceDto
{
    /// <summary>docker CLI bu sunucuda mevcut mu.</summary>
    public bool Installed { get; set; }

    /// <summary>docker daemon erisilebilir mi (docker info basarili).</summary>
    public bool DaemonRunning { get; set; }

    public string Version { get; set; } = string.Empty;

    public IReadOnlyCollection<DockerContainerDto> Containers { get; set; } = [];

    public IReadOnlyCollection<DockerImageDto> Images { get; set; } = [];
}
