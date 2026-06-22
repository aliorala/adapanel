using AdaPanel.Core.Dtos.Update;

namespace AdaPanel.Core.Interfaces;

public interface ISelfUpdateService
{
    Task<UpdateStatusDto> CheckForUpdateAsync(CancellationToken cancellationToken = default);

    Task<UpdateStatusDto> ApplyUpdateAsync(ApplyUpdateRequest request, CancellationToken cancellationToken = default);

    Task<UpdateStatusDto> InstallPreparedUpdateAsync(CancellationToken cancellationToken = default);
}
