using AdaPanel.Core.Dtos.Ftp;
using AdaPanel.Core.Dtos.Sites;

namespace AdaPanel.Core.Interfaces;

public interface IFtpService
{
    Task<FtpServerStatusDto> EnsureServerAsync(CancellationToken cancellationToken = default);

    Task<FtpServerStatusDto> EnsureServerAsync(Func<CancellationToken, Task> beforeMutation,
        CancellationToken cancellationToken) => throw new NotSupportedException();

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

    Task<FtpUserDto> CreateFtpUserAsync(CreateFtpUserRequest request,
        Func<SiteDto, CancellationToken, Task> beforeMutation, CancellationToken cancellationToken) => throw new NotSupportedException();

    Task SetFtpPasswordAsync(SetFtpPasswordRequest request, CancellationToken cancellationToken = default);

    Task SetFtpPasswordAsync(SetFtpPasswordRequest request,
        Func<FtpUserDto?, CancellationToken, Task> beforeMutation, CancellationToken cancellationToken) => throw new NotSupportedException();

    Task DeleteFtpUserAsync(string userName, CancellationToken cancellationToken = default);

    Task DeleteFtpUserAsync(string userName,
        Func<FtpUserDto?, CancellationToken, Task> beforeMutation, CancellationToken cancellationToken) => throw new NotSupportedException();

    /// <summary>FTPS zorunlu mu (panel "yalnız güvenli FTP"). Varsayılan false.</summary>
    Task<bool> GetRequireSslAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// FTPS zorunluluğunu aç/kapat, IIS FTP politikasını (SslAllow/SslRequire) hemen uygula ve
    /// UYGULANDIĞINI ÖLÇ.
    /// </summary>
    /// <remarks>
    /// Dönüş tipi <c>bool</c> DEĞİLDİR ve bu bilinçlidir: eski imza ISTENEN bayrağı döndürüyordu,
    /// uç de ondan beslenip kullanıcıya "FTPS artık zorunlu; düz FTP bağlantıları reddedilecek"
    /// diyordu. Bir güvenlik denetiminin yürürlükte olduğunu söylemek için ölçmek gerekir.
    /// </remarks>
    Task<FtpTlsMeasurement> SetRequireSslAsync(bool requireSsl, CancellationToken cancellationToken = default);

    Task<FtpTlsMeasurement> SetRequireSslAsync(bool requireSsl, Func<CancellationToken, Task> beforeMutation,
        CancellationToken cancellationToken) => throw new NotSupportedException();
}
