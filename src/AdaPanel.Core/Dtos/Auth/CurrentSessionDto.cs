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

    /// <summary>
    /// Oturum sahibi SUNUCU SAHİBİ mi (terminal = SYSTEM kabuğu yalnız ona açık).
    /// </summary>
    /// <remarks>
    /// YALNIZ ARAYÜZ İÇİN: menüde Terminal sekmesinin görünüp görünmeyeceğini belirler. Asıl
    /// dayatma <c>TerminalController</c>'dadır; bu alan yanlış bile olsa uç reddeder. Aynı kalıp
    /// <see cref="IsSubUser"/> / <see cref="AllowedSections"/> için de geçerlidir.
    /// </remarks>
    public bool IsServerOwner { get; init; }
}

