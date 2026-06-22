namespace AdaPanel.Core.Dtos.Auth;

public sealed class LoginResultDto
{
    public bool IsAuthenticated { get; init; }

    public bool RequiresTwoFactor { get; init; }

    public CurrentUserDto? User { get; init; }
}

