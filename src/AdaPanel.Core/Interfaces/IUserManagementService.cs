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

    // --- Abonelik altı ek kullanıcılar (Plesk "additional user"): TOP-LEVEL müşteri kendisi yönetir ---

    /// <summary>Parent müşterinin altındaki ek kullanıcılar. Aktör top-level müşteri değilse users.sub_forbidden.</summary>
    Task<IReadOnlyCollection<SubUserDto>> GetSubUsersAsync(Guid parentUserId, CancellationToken cancellationToken = default);

    Task<SubUserDto> CreateSubUserAsync(Guid parentUserId, CreateSubUserRequest request, string? ipAddress, CancellationToken cancellationToken = default);

    Task<SubUserDto> UpdateSubUserAsync(Guid parentUserId, Guid subUserId, UpdateSubUserRequest request, string? ipAddress, CancellationToken cancellationToken = default);

    Task DeleteSubUserAsync(Guid parentUserId, Guid subUserId, string? ipAddress, CancellationToken cancellationToken = default);
}
