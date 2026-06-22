using AdaPanel.Core.Dtos.Ftp;

namespace AdaPanel.Core.Interfaces;

public interface IFtpService
{
    Task<FtpServerStatusDto> EnsureServerAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyCollection<FtpUserDto>> GetFtpUsersAsync(CancellationToken cancellationToken = default);

    Task<FtpUserDto> CreateFtpUserAsync(CreateFtpUserRequest request, CancellationToken cancellationToken = default);

    Task SetFtpPasswordAsync(SetFtpPasswordRequest request, CancellationToken cancellationToken = default);

    Task DeleteFtpUserAsync(string userName, CancellationToken cancellationToken = default);
}
