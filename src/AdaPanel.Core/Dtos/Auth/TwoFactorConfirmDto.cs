namespace AdaPanel.Core.Dtos.Auth;

/// <summary>
/// 2FA etkinleştirme sonucu: güncel kullanıcı + bir defaya mahsus gösterilen
/// kurtarma kodları (authenticator kaybedilirse giriş için).
/// </summary>
public sealed class TwoFactorConfirmDto
{
    public CurrentUserDto User { get; set; } = new();

    public IReadOnlyCollection<string> RecoveryCodes { get; set; } = [];
}
