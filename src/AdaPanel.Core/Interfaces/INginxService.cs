using AdaPanel.Core.Dtos.Nginx;

namespace AdaPanel.Core.Interfaces;

public interface INginxService
{
    Task<NginxStatusDto> GetStatusAsync(CancellationToken cancellationToken = default);

    Task<NginxStatusDto> ConfigureAsync(
        NginxConfigurationRequest request,
        Guid? actorUserId,
        string? ipAddress,
        CancellationToken cancellationToken = default);

    Task<NginxStatusDto> StartAsync(
        Guid? actorUserId,
        string? ipAddress,
        CancellationToken cancellationToken = default);

    Task<NginxStatusDto> StopAsync(
        Guid? actorUserId,
        string? ipAddress,
        CancellationToken cancellationToken = default);

    Task<NginxStatusDto> ReloadAsync(
        Guid? actorUserId,
        string? ipAddress,
        CancellationToken cancellationToken = default);

    Task<NginxStatusDto> TestConfigurationAsync(CancellationToken cancellationToken = default);
}
