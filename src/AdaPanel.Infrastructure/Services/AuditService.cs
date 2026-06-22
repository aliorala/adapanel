using AdaPanel.Core.Dtos.Audit;
using AdaPanel.Core.Entities;
using AdaPanel.Core.Interfaces;
using AdaPanel.Data.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AdaPanel.Infrastructure.Services;

public sealed class AuditService(AdaPanelDbContext dbContext) : IAuditService
{
    public async Task WriteAsync(
        string action,
        string resource,
        string details,
        Guid? userId,
        string? ipAddress,
        CancellationToken cancellationToken = default)
    {
        dbContext.AuditLogs.Add(new AuditLog
        {
            UserId = userId,
            Action = action,
            Resource = resource,
            Details = details,
            IpAddress = ipAddress,
            CreatedAtUtc = DateTime.UtcNow
        });

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyCollection<AuditLogDto>> GetRecentAsync(int take, CancellationToken cancellationToken = default)
    {
        var cappedTake = Math.Clamp(take, 1, 100);

        var users = await dbContext.Users
            .Select(user => new { user.Id, user.UserName })
            .ToDictionaryAsync(user => user.Id, user => user.UserName, cancellationToken);

        var items = await dbContext.AuditLogs
            .OrderByDescending(log => log.CreatedAtUtc)
            .Take(cappedTake)
            .ToListAsync(cancellationToken);

        return items.Select(log => new AuditLogDto
        {
            Id = log.Id,
            UserId = log.UserId,
            UserName = log.UserId.HasValue && users.TryGetValue(log.UserId.Value, out var userName)
                ? userName
                : "system",
            Action = log.Action,
            Resource = log.Resource,
            Details = log.Details,
            IpAddress = log.IpAddress ?? string.Empty,
            CreatedAtUtc = log.CreatedAtUtc
        }).ToArray();
    }
}
