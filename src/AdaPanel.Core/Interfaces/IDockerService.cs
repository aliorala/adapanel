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

    /// <summary>Çalışan konteynerin CPU/RAM sınırını günceller (<c>docker update</c>).</summary>
    Task<DockerWorkspaceDto> UpdateContainerLimitsAsync(string id, UpdateContainerLimitsRequest request, CancellationToken cancellationToken = default);

    Task<DockerContainerLimitsDto> GetContainerLimitsAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Siteyle ilişkili (etiketli) bütün konteynerleri durdurup kaldırır; ölçülmüş sonucu döner.</summary>
    Task<DockerSiteCleanupResult> RemoveSiteContainersAsync(string siteName, CancellationToken cancellationToken = default);
}
