using AdaPanel.Core.Dtos.Ftp;

namespace AdaPanel.Core.Interfaces;

public interface IFtpService
{
    Task<FtpServerStatusDto> EnsureServerAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyCollection<FtpUserDto>> GetFtpUsersAsync(CancellationToken cancellationToken = default);

    Task<FtpUserDto> CreateFtpUserAsync(CreateFtpUserRequest request, CancellationToken cancellationToken = default);

    Task SetFtpPasswordAsync(SetFtpPasswordRequest request, CancellationToken cancellationToken = default);

    Task DeleteFtpUserAsync(string userName, CancellationToken cancellationToken = default);

    /// <summary>FTPS zorunlu mu (panel "yalnız güvenli FTP"). Varsayılan false.</summary>
    Task<bool> GetRequireSslAsync(CancellationToken cancellationToken = default);

    /// <summary>FTPS zorunluluğunu aç/kapat ve IIS FTP politikasını (SslAllow/SslRequire) hemen uygula.</summary>
    Task<bool> SetRequireSslAsync(bool requireSsl, CancellationToken cancellationToken = default);
}
