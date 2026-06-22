namespace AdaPanel.Core.Dtos.Auth;

public sealed class CurrentSessionDto
{
    public bool IsAuthenticated { get; init; }

    public bool RequiresSetupWizard { get; init; }

    public CurrentUserDto? User { get; init; }

    /// <summary>Yönetici başka bir kullanıcıyı taklit ediyor mu (Plesk "log in as customer").</summary>
    public bool IsImpersonating { get; init; }

    /// <summary>Taklit eden asıl yöneticinin adı (geri dönüş için).</summary>
    public string? ImpersonatorName { get; init; }
}

