using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Setup;

namespace AdaPanel.Core.Interfaces;

public interface ISetupService
{
    Task<SetupStatusDto> GetStatusAsync(CancellationToken cancellationToken = default);

    Task<SetupResultDto> InitializeAsync(InitialSetupRequest request, string? remoteIpAddress, CancellationToken cancellationToken = default);

    /// <summary>İlk kurulumda arka planda süren önerilen yazılım kurulumunun canlı ilerlemesi.</summary>
    Task<OperationProgressDto> GetInstallProgressAsync(CancellationToken cancellationToken = default);
}

