using System.ComponentModel.DataAnnotations;

namespace AdaPanel.Core.Dtos.Settings;

/// <summary>
/// Marka METİNLERİNİ günceller (görseller ayrı uçlardan yüklenir).
/// </summary>
/// <remarks>
/// <b>KAPSAM ALANI BİLEREK YOKTUR.</b> "Hangi markayı düzenliyorum" sorusu isteğin gövdesinden
/// DEĞİL, oturumun kimliğinden cevaplanır (<c>BrandingController.ResolveScope</c>). Gövdeye bir
/// <c>resellerId</c> alanı konsaydı her uçta ayrıca "bu kişi o bayi mi" denetimi yazmak gerekirdi
/// ve unutulan tek yer, bir bayinin başka bir bayinin markasını değiştirmesi demek olurdu.
/// Alan olmayınca o hata YAZILAMAZ.
/// </remarks>
public sealed class UpdateBrandingRequest
{
    /// <summary>Boş bırakılabilir: marka adı kaldırılır ve panel varsayılana döner.</summary>
    [StringLength(64)]
    public string PanelName { get; init; } = string.Empty;

    /// <summary>
    /// <c>#rrggbb</c> ya da boş. Desen SUNUCUDA da uygulanır (bkz. BrandingTextRules): bu değer
    /// tarayıcıda bir CSS özel değişkenine yazılıyor, yani serbest metin kabul etmek stil enjeksiyonu olurdu.
    /// </summary>
    [StringLength(7)]
    public string AccentColor { get; init; } = string.Empty;

    /// <summary>Yalnız http/https. Serbest bırakılsaydı <c>javascript:</c> adresi logo bağlantısına konabilirdi.</summary>
    [StringLength(255)]
    public string LogoLinkUrl { get; init; } = string.Empty;
}
