namespace AdaPanel.Core.Dtos.Auth;

public sealed class CurrentUserDto
{
    public Guid Id { get; init; }

    public string UserName { get; init; } = string.Empty;

    public string Email { get; init; } = string.Empty;

    public string Role { get; init; } = string.Empty;

    public string PreferredLanguage { get; init; } = "tr";

    public bool IsTwoFactorEnabled { get; init; }

    public bool IsActive { get; init; }

    public DateTime? LastLoginAtUtc { get; init; }
}

