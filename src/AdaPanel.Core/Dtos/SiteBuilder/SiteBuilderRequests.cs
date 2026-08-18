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

    public List<SiteBuilderSectionDto> Sections { get; set; } = [];
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
