using System.ComponentModel.DataAnnotations;

namespace AdaPanel.Core.Dtos.Users;

/// <summary>Müşterinin kendi yönettiği abonelik altı ek kullanıcı (Plesk "additional user") kaydı.</summary>
public sealed class SubUserDto
{
    public Guid Id { get; init; }

    public string UserName { get; init; } = string.Empty;

    public string Email { get; init; } = string.Empty;

    public bool IsActive { get; init; }

    public DateTime? LastLoginAtUtc { get; init; }

    public DateTime CreatedAtUtc { get; init; }

    /// <summary>İzinli panel bölümleri; null = kısıtsız (tüm müşteri bölümleri).</summary>
    public string[]? AllowedSections { get; init; }
}

public sealed class CreateSubUserRequest
{
    [Required]
    [StringLength(32, MinimumLength = 3)]
    public string UserName { get; init; } = string.Empty;

    [Required]
    [EmailAddress]
    [StringLength(256)]
    public string Email { get; init; } = string.Empty;

    [Required]
    [StringLength(128, MinimumLength = 12)]
    public string Password { get; init; } = string.Empty;

    /// <summary>İzinli bölümler; null/tam liste = kısıtsız. Bilinmeyen anahtarlar elenir.</summary>
    public string[]? AllowedSections { get; init; }
}

public sealed class UpdateSubUserRequest
{
    [Required]
    [EmailAddress]
    [StringLength(256)]
    public string Email { get; init; } = string.Empty;

    /// <summary>Doluysa parola sıfırlanır; boş bırakılırsa değişmez.</summary>
    [StringLength(128)]
    public string? Password { get; init; }

    public bool IsActive { get; init; } = true;

    /// <summary>İzinli bölümler; null/tam liste = kısıtsız.</summary>
    public string[]? AllowedSections { get; init; }
}
