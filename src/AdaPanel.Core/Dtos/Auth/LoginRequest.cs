using System.ComponentModel.DataAnnotations;

namespace AdaPanel.Core.Dtos.Auth;

public sealed class LoginRequest
{
    [Required]
    [StringLength(256, MinimumLength = 3)]
    public string UserNameOrEmail { get; init; } = string.Empty;

    [Required]
    [StringLength(128, MinimumLength = 12)]
    public string Password { get; init; } = string.Empty;

    /// <summary>
    /// TOTP kodu VEYA kurtarma kodu.
    /// </summary>
    /// <remarks>
    /// DESEN "^[0-9]{6,16}$" IDI VE KURTARMA KODU OZELLIGINI FIILEN OLDURUYORDU.
    /// <para>
    /// <c>AuthService.GenerateRecoveryCode</c> kodlari <c>ABCDEFGHJKLMNPQRSTUVWXYZ23456789</c>
    /// alfabesinden <c>XXXXX-XXXXX</c> biciminde uretir (11 karakter, harf ve tire icerir).
    /// Yalnizca rakam kabul eden desen bu kodlari HER ZAMAN reddediyordu: istek modele takiliyor,
    /// 400 <c>validation.failed</c> donuyor ve <c>TryConsumeRecoveryCodeAsync</c> HIC
    /// CAGRILMIYORDU. Yani kimlik dogrulayicisini kaybeden musteri panele giremiyordu -- kurtarma
    /// kodlari tam da bunun icin var.
    /// </para>
    /// <para>
    /// Sinirlar OLCULDU: TOTP 6 hane; kurtarma kodu tireli 11, tiresiz yazilirsa 10 karakter
    /// (<c>NormalizeRecoveryCode</c> harf/rakam disini zaten atiyor). 6..16 araligi ikisini de
    /// kapsar. Desen hala DAR: yalnizca harf, rakam ve tire; bosluk/isaret gecmez.
    /// </para>
    /// </remarks>
    [RegularExpression("^[A-Za-z0-9-]{6,16}$")]
    public string? TwoFactorCode { get; init; }

    public bool RememberMe { get; init; }
}
