using AdaPanel.Core.Dtos.Audit;

namespace AdaPanel.Core.Interfaces;

public interface IAuditService
{
    Task WriteAsync(
        string action,
        string resource,
        string details,
        Guid? userId,
        string? ipAddress,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyCollection<AuditLogDto>> GetRecentAsync(int take, CancellationToken cancellationToken = default);
}
