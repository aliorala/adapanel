using AdaPanel.Core.Dtos.Ftp;

namespace AdaPanel.Core.Interfaces;

public interface IFtpService
{
    Task<FtpServerStatusDto> EnsureServerAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// FTP'nin O ANKİ gerçek durumunu ÖLÇER; hiçbir şeyi kurmaz/değiştirmez.
    /// </summary>
    /// <remarks>
    /// <see cref="EnsureServerAsync"/> ağır ve MUTASYONLU bir işlemdir (IIS sitesi kurar, FTPSVC'yi
    /// yeniden başlatır) — "durum ne" sorusunu sormanın bedeli bu olmamalı. Ayrı bir okuma yolu
    /// olmadığı için panel durumu ancak kurulum yaparak öğrenebiliyordu.
    /// </remarks>
    Task<FtpServerStatusDto> GetServerStatusAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyCollection<FtpUserDto>> GetFtpUsersAsync(CancellationToken cancellationToken = default);

    Task<FtpUserDto> CreateFtpUserAsync(CreateFtpUserRequest request, CancellationToken cancellationToken = default);

    Task SetFtpPasswordAsync(SetFtpPasswordRequest request, CancellationToken cancellationToken = default);

    Task DeleteFtpUserAsync(string userName, CancellationToken cancellationToken = default);

    /// <summary>FTPS zorunlu mu (panel "yalnız güvenli FTP"). Varsayılan false.</summary>
    Task<bool> GetRequireSslAsync(CancellationToken cancellationToken = default);

    /// <summary>FTPS zorunluluğunu aç/kapat ve IIS FTP politikasını (SslAllow/SslRequire) hemen uygula.</summary>
    Task<bool> SetRequireSslAsync(bool requireSsl, CancellationToken cancellationToken = default);
}
