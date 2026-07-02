using AdaPanel.Core.Dtos.Update;

namespace AdaPanel.Core.Interfaces;

public interface ISelfUpdateService
{
    Task<UpdateStatusDto> CheckForUpdateAsync(CancellationToken cancellationToken = default);

    /// <summary>Güncellemelerin çekileceği GitHub deposunu (owner/repo) ayarlar ve güncel durumu döner. Boş = GitHub kapalı.</summary>
    Task<UpdateStatusDto> SetGithubRepoAsync(string repo, CancellationToken cancellationToken = default);

    Task<UpdateStatusDto> ApplyUpdateAsync(ApplyUpdateRequest request, CancellationToken cancellationToken = default);

    Task<UpdateStatusDto> InstallPreparedUpdateAsync(CancellationToken cancellationToken = default);
}
