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
}
