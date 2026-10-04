using System.Text.Json.Serialization;

namespace AdaPanel.Core.Dtos.Auth;

public sealed class CurrentUserDto
{
    // Internal handoff from accepted authentication to cookie publication. Never accept
    // this value from an API body or expose it in a JSON response.
    [JsonIgnore]
    public string AuthenticationEvidence { get; init; } = string.Empty;

    public Guid Id { get; init; }

    public string UserName { get; init; } = string.Empty;

    public string Email { get; init; } = string.Empty;

    public string Role { get; init; } = string.Empty;

    public string PreferredLanguage { get; init; } = "tr";

    public bool IsTwoFactorEnabled { get; init; }

    public bool IsActive { get; init; }

    public DateTime? LastLoginAtUtc { get; init; }
}
