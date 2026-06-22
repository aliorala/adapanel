using AdaPanel.Core.Dtos.Security;

namespace AdaPanel.Core.Interfaces;

public interface IPanelSecurityService
{
    Task<SecuritySettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default);

    Task<SecuritySettingsDto> UpdateSettingsAsync(
        UpdateSecuritySettingsRequest request,
        Guid actorUserId,
        string? ipAddress,
        CancellationToken cancellationToken = default);

    Task<string> GetSecureEntryPathAsync(CancellationToken cancellationToken = default);

    Task<bool> IsIpAllowedAsync(string? ipAddress, CancellationToken cancellationToken = default);
}

