using System.ComponentModel.DataAnnotations;

namespace AdaPanel.Core.Dtos.SiteBuilder;

/// <summary>Tasarımı kaydetme isteği. Tasarımın tamamı gönderilir (kısmi yama yok).</summary>
public sealed class SaveSiteBuilderProjectRequest
{
    [StringLength(64)]
    public string TemplateId { get; set; } = string.Empty;

    [StringLength(64)]
    public string PaletteId { get; set; } = string.Empty;

    [StringLength(64)]
    public string FontId { get; set; } = string.Empty;

    [StringLength(120)]
    public string SiteTitle { get; set; } = string.Empty;

    [StringLength(200)]
    public string Tagline { get; set; } = string.Empty;

    [StringLength(300)]
    public string MetaDescription { get; set; } = string.Empty;

    /// <summary>Sayfanın dili: <c>tr</c> ya da <c>en</c>. Başka değer <c>tr</c>'ye düşer.</summary>
    [StringLength(8)]
    public string Language { get; set; } = "tr";

    /// <summary>ANA SAYFANIN bölümleri.</summary>
    public List<SiteBuilderSectionDto> Sections { get; set; } = [];

    /// <summary>
    /// Ek sayfalar. Listeden ÇIKARILAN bir sayfa tasarımdan düşer; diskteki dosyası SİLİNMEZ.
    /// </summary>
    /// <remarks>
    /// Silmemek bilinçli. "Artık üretilmeyen sayfayı sil" adımı, bu depoda müşteri sitesini yok
    /// edebilecek hata sınıfının (özyinelemeli silme + klasör bağlantısı) tam ortasındadır.
    /// Bunun yerine yayın planı ve yayın sonucu ÖKSÜZ dosyaları ADIYLA listeler; kullanıcı
    /// dosya yöneticisinden bilerek siler. Sessizce silmek yerine görünür kılmak, yanlış dosyayı
    /// silme riskini sıfırlar ve kararı dosyanın sahibine bırakır.
    /// </remarks>
    public List<SiteBuilderPageDto> Pages { get; set; } = [];
}

/// <summary>
/// Yayın isteği.
/// </summary>
public sealed class PublishSiteBuilderRequest
{
    /// <summary>
    /// Kurucunun üretmediği dosyaların üzerine yazılmasına açık onay.
    /// </summary>
    /// <remarks>
    /// Varsayılanı <c>false</c> olması bilinçli: onay alanı unutulduğunda ya da eski bir istemci
    /// bu alanı hiç göndermediğinde davranış "yaz" değil "reddet" olur. Güvenli varsayılan,
    /// hatırlanması gereken bir bayraktan daha dayanıklıdır.
    /// </remarks>
    public bool OverwriteExistingFiles { get; set; }
}
