using AdaPanel.Core.Dtos.Docker;

namespace AdaPanel.Core.Interfaces;

/// <summary>
/// Docker konteyner/imaj yonetimi (docker CLI uzerinden).
/// </summary>
public interface IDockerService
{
    Task<DockerWorkspaceDto> GetWorkspaceAsync(CancellationToken cancellationToken = default);

    Task<DockerWorkspaceDto> StartContainerAsync(string id, CancellationToken cancellationToken = default);

    Task<DockerWorkspaceDto> StopContainerAsync(string id, CancellationToken cancellationToken = default);

    Task<DockerWorkspaceDto> RestartContainerAsync(string id, CancellationToken cancellationToken = default);

    Task<DockerWorkspaceDto> RemoveContainerAsync(string id, CancellationToken cancellationToken = default);

    Task<DockerLogsDto> GetLogsAsync(string id, int tail, CancellationToken cancellationToken = default);

    Task<DockerWorkspaceDto> PullImageAsync(PullImageRequest request, CancellationToken cancellationToken = default);

    Task<DockerWorkspaceDto> RemoveImageAsync(string id, CancellationToken cancellationToken = default);

    Task<DockerWorkspaceDto> RunContainerAsync(RunContainerRequest request, CancellationToken cancellationToken = default);
}
