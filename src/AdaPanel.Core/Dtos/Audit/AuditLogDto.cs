namespace AdaPanel.Core.Dtos.Audit;

public sealed class AuditLogDto
{
    public long Id { get; init; }

    public Guid? UserId { get; init; }

    public string UserName { get; init; } = string.Empty;

    public string Action { get; init; } = string.Empty;

    public string Resource { get; init; } = string.Empty;

    public string Details { get; init; } = string.Empty;

    public string IpAddress { get; init; } = string.Empty;

    public DateTime CreatedAtUtc { get; init; }
}

