using System.ComponentModel.DataAnnotations;

namespace AdaPanel.Core.Dtos.Auth;

public sealed class ForgotPasswordRequest
{
    [Required]
    [StringLength(256)]
    public string Email { get; init; } = string.Empty;
}

public sealed class ResetPasswordRequest
{
    [Required]
    [StringLength(128)]
    public string Token { get; init; } = string.Empty;

    [Required]
    [StringLength(128, MinimumLength = 12)]
    public string NewPassword { get; init; } = string.Empty;
}

/// <summary>
/// Oturumu açık kullanıcının KENDİ parolasını değiştirmesi.
/// <para>
/// Sıfırlama akışından farkı: e-posta gerekmez, ama mevcut parola İSTENİR. Bu olmadan, çalınmış bir
/// oturum çerezi tek başına hesabı kalıcı olarak devralmaya yeterdi.
/// </para>
/// </summary>
public sealed class ChangePasswordRequest
{
    [Required]
    [StringLength(128)]
    public string CurrentPassword { get; init; } = string.Empty;

    [Required]
    [StringLength(128, MinimumLength = 12)]
    public string NewPassword { get; init; } = string.Empty;
}

/// <summary>Parola değişiminin sonucu. İptal edilen anahtar sayısı kullanıcıya BİLDİRİLİR.</summary>
public sealed class ChangePasswordResultDto
{
    /// <summary>Bu değişiklik yüzünden geçersiz kılınan API anahtarı sayısı.</summary>
    public int RevokedApiTokens { get; init; }
}
