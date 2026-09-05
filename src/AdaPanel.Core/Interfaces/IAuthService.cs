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

    /// <summary>
    /// Yönetici (ya da bayi, YALNIZ kendi müşterisi için) BAŞKA bir kullanıcının parolasını belirler.
    /// Mevcut parola İSTENMEZ; yetki aktörün rolü ve kapsamıyla kanıtlanır (<c>ManagedUserGuard</c>).
    /// Aktör kendi kimliğini veremez — kendi parolası için <see cref="ChangePasswordAsync"/> vardır.
    /// </summary>
    /// <remarks>
    /// Parola değişince hedefin açık ÇEREZ oturumları (kimlik damgası paroladan türer) ve API
    /// anahtarları düşer; kaç anahtarın düştüğü yanıtta bildirilir.
    /// </remarks>
    Task<SetUserPasswordResultDto> SetPasswordForUserAsync(
        Guid targetUserId,
        SetUserPasswordRequest request,
        Guid actorUserId,
        string? ipAddress,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Hedef kullanıcı için TEK KULLANIMLIK, kısa ömürlü bir giriş bağlantısı üretir (faturalama
    /// sisteminin "Panele git" düğmesi). Yetki denetimi parola belirlemeyle AYNI kuraldan geçer.
    /// </summary>
    /// <remarks>
    /// Bağlantının ana adı YALNIZCA panelin yapılandırılmış adresinden kurulur. Burada eskiden
    /// isteğin <c>Host</c> başlığını taşıyan bir <c>requestHost</c> yedeği vardı; başlık istemcinin
    /// serbest metni olduğu için giriş belirtecini yabancı bir ana ada götürebiliyordu ve ölçüldüğünde
    /// yedeğin hiç devreye giremediği görüldü (bkz. <c>PanelAddress</c>).
    /// </remarks>
    Task<SsoLinkDto> CreateSsoLinkAsync(
        Guid targetUserId,
        Guid actorUserId,
        string? ipAddress,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Giriş belirtecini TÜKETİR ve geçerliyse kullanıcıyı döner; geçersiz/süresi geçmiş/ikinci kez
    /// kullanılmış ya da hesap pasifse <c>null</c> döner.
    /// </summary>
    /// <remarks>
    /// Neden istisna değil <c>null</c>: bu belirteci TARAYICI taşır ve ucun yanıtı bir SAYFADIR,
    /// JSON zarfı değil. İstisna fırlatılsaydı hata ara katmanı müşteriye ham bir JSON hata
    /// gövdesi gösterirdi. Reddin sebebi denetim kaydına yazılır, kullanıcıya değil — geçersiz
    /// belirtecin NEDEN geçersiz olduğunu söylemek, deneme yapan birine bilgi vermekten başka işe
    /// yaramaz.
    /// </remarks>
    Task<CurrentUserDto?> TryRedeemSsoTokenAsync(
        string token,
        string? ipAddress,
        CancellationToken cancellationToken = default);
}

