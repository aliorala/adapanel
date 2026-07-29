using AdaPanel.Core.Dtos.Users;

namespace AdaPanel.Core.Interfaces;

public interface IUserManagementService
{
    /// <summary>
    /// Kullanıcı listesi. <paramref name="actorUserId"/> bir Bayi (Reseller) ise yalnızca o bayinin
    /// sahip olduğu müşteriler döner; Administrator için tüm kullanıcılar.
    /// </summary>
    Task<IReadOnlyCollection<UserSummaryDto>> GetUsersAsync(Guid actorUserId, CancellationToken cancellationToken = default);

    Task<UserSummaryDto> CreateUserAsync(CreateUserRequest request, Guid actorUserId, string? ipAddress, CancellationToken cancellationToken = default);

    Task<UserSummaryDto> UpdateUserAsync(Guid userId, UpdateUserRequest request, Guid actorUserId, string? ipAddress, CancellationToken cancellationToken = default);

    /// <summary>
    /// Kullanıcıyı siler (Plesk paritesi). Korumalar: kendini silemez, son aktif yönetici silinemez,
    /// bayi yalnız kendi müşterisini silebilir. Top-level müşteri silinirken alt-kullanıcıları da silinir;
    /// panel-tarafı kayıtlar (kapsam, cron görevleri, DB sahiplikleri) temizlenir.
    /// <paramref name="removeSites"/> true ise (yalnız admin) atanan siteler dosya+eşleşen DB'lerle cascade silinir;
    /// false ise siteler/veriler SUNUCUDA KALIR (yönetime devredilmiş olur).
    /// </summary>
    Task DeleteUserAsync(Guid userId, Guid actorUserId, bool removeSites, string? ipAddress, CancellationToken cancellationToken = default);

    /// <summary>
    /// Hatalı giriş denemeleri yüzünden kilitlenmiş kullanıcının kilidini açar (LockedUntilUtc +
    /// FailedLoginCount sıfırlanır). Bayi yalnız kendi müşterisinin kilidini açabilir.
    /// Kimse giremiyorsa sunucu tarafı çıkış yolu: veri kökünde config\recover-access.flag dosyası.
    /// </summary>
    Task<UserSummaryDto> UnlockUserAsync(Guid userId, Guid actorUserId, string? ipAddress, CancellationToken cancellationToken = default);

    // --- Abonelik altı ek kullanıcılar (Plesk "additional user"): TOP-LEVEL müşteri kendisi yönetir ---

    /// <summary>Parent müşterinin altındaki ek kullanıcılar. Aktör top-level müşteri değilse users.sub_forbidden.</summary>
    Task<IReadOnlyCollection<SubUserDto>> GetSubUsersAsync(Guid parentUserId, CancellationToken cancellationToken = default);

    Task<SubUserDto> CreateSubUserAsync(Guid parentUserId, CreateSubUserRequest request, string? ipAddress, CancellationToken cancellationToken = default);

    Task<SubUserDto> UpdateSubUserAsync(Guid parentUserId, Guid subUserId, UpdateSubUserRequest request, string? ipAddress, CancellationToken cancellationToken = default);

    Task DeleteSubUserAsync(Guid parentUserId, Guid subUserId, string? ipAddress, CancellationToken cancellationToken = default);
}
