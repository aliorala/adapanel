using System.Security.Claims;
using AdaPanel.Core.Dtos.Auth;

namespace AdaPanel.Core.Interfaces;

public interface IAuthService
{
    Task<LoginResultDto> LoginAsync(LoginRequest request, string? ipAddress, CancellationToken cancellationToken = default);

    Task<CurrentSessionDto> GetCurrentSessionAsync(ClaimsPrincipal principal, CancellationToken cancellationToken = default);

    /// <summary>Verilen kullanıcının oturum bilgisini döner (impersonation için).</summary>
    Task<CurrentUserDto> GetUserByIdAsync(Guid userId, CancellationToken cancellationToken = default);

    Task<TwoFactorSetupDto> BeginTwoFactorSetupAsync(Guid userId, CancellationToken cancellationToken = default);

    Task<TwoFactorConfirmDto> ConfirmTwoFactorSetupAsync(Guid userId, string code, string? ipAddress, CancellationToken cancellationToken = default);

    Task<CurrentUserDto> DisableTwoFactorAsync(Guid userId, string password, string? ipAddress, CancellationToken cancellationToken = default);

    /// <summary>Kurtarma kodlarını yeniden üretir (eskileri geçersiz olur).</summary>
    Task<RecoveryCodesDto> RegenerateRecoveryCodesAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Parola sıfırlama bağlantısı ister. Kullanıcı numaralandırmasını engellemek için
    /// e-posta bulunsa da bulunmasa da aynı şekilde döner; mevcutsa e-posta gönderilir.
    /// </summary>
    Task RequestPasswordResetAsync(string email, CancellationToken cancellationToken = default);

    /// <summary>Geçerli tek kullanımlık token ile yeni parola belirler.</summary>
    Task ResetPasswordAsync(string token, string newPassword, CancellationToken cancellationToken = default);

    /// <summary>
    /// Oturumu açık kullanıcının kendi parolasını değiştirir. Mevcut parolayı doğrular ve
    /// kullanıcının API anahtarlarını düşürür; iptal edilen anahtar sayısını döner.
    /// </summary>
    Task<ChangePasswordResultDto> ChangePasswordAsync(
        Guid userId,
        ChangePasswordRequest request,
        string? ipAddress,
        CancellationToken cancellationToken = default);
}

