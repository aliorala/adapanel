namespace AdaPanel.Core.Entities;

public sealed class AuditLog
{
    public long Id { get; set; }

    public Guid? UserId { get; set; }

    public string Action { get; set; } = string.Empty;

    public string Resource { get; set; } = string.Empty;

    public string Details { get; set; } = string.Empty;

    public string? IpAddress { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

