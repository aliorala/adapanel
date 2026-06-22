using AdaPanel.Core.Enums;

namespace AdaPanel.Core.Entities;

public sealed class PanelUser
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string UserName { get; set; } = string.Empty;

    public string Email { get; set; } = string.Empty;

    public string PasswordHash { get; set; } = string.Empty;

    public UserRole Role { get; set; } = UserRole.Operator;

    public string PreferredLanguage { get; set; } = "tr";

    public bool IsActive { get; set; } = true;

    public bool IsTwoFactorEnabled { get; set; }

    public string? TwoFactorSecretProtected { get; set; }

    public string? PendingTwoFactorSecretProtected { get; set; }

    public int FailedLoginCount { get; set; }

    public DateTime? LockedUntilUtc { get; set; }

    public DateTime? LastLoginAtUtc { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
