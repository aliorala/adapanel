namespace AdaPanel.Core.Dtos.Auth;

public sealed class CurrentSessionDto
{
    public bool IsAuthenticated { get; init; }

    public bool RequiresSetupWizard { get; init; }

    public CurrentUserDto? User { get; init; }

    /// <summary>Yönetici başka bir kullanıcıyı taklit ediyor mu (panel "log in as customer").</summary>
    public bool IsImpersonating { get; init; }

    /// <summary>Taklit eden asıl yöneticinin adı (geri dönüş için).</summary>
    public string? ImpersonatorName { get; init; }

    /// <summary>Oturum sahibi bir abonelik altı ek kullanıcı mı (Plesk "additional user").</summary>
    public bool IsSubUser { get; init; }

    /// <summary>Alt-kullanıcının erişebildiği panel bölümleri; null = kısıtsız (veya alt-kullanıcı değil).</summary>
    public string[]? AllowedSections { get; init; }
}

