namespace AdaPanel.Core.Dtos.SiteBuilder;

/// <summary>
/// Kurucunun sunduğu seçenekler: şablonlar, renk paletleri, yazı tipleri, bölüm tipleri.
/// </summary>
/// <remarks>
/// Katalog SUNUCUDAN gelir, arayüzde elle yazılmaz. Sebep: üretilen HTML/CSS sunucuda kuruluyor;
/// listeler iki yerde tutulsaydı arayüzde görünen bir şablon sunucuda karşılıksız kalabilir ve
/// kullanıcı "seçtim ama olmadı" derdi. (Bu depoda "ad/yol elle listelenmez" kuralı zaten var.)
/// </remarks>
public sealed class SiteBuilderCatalogDto
{
    public List<SiteBuilderOptionDto> Templates { get; set; } = [];

    public List<SiteBuilderPaletteDto> Palettes { get; set; } = [];

    public List<SiteBuilderOptionDto> Fonts { get; set; } = [];

    /// <summary>Desteklenen bölüm tipleri (sırası, sayfadaki varsayılan sırayı verir).</summary>
    public List<string> SectionTypes { get; set; } = [];

    /// <summary>Görsel yüklemede kabul edilen en büyük boyut (bayt).</summary>
    public long MaxImageBytes { get; set; }

    /// <summary>Kabul edilen görsel uzantıları (bilgi amaçlı; asıl karar dosya İÇERİĞİNE bakılarak verilir).</summary>
    public List<string> AllowedImageExtensions { get; set; } = [];
}

/// <summary>Kimlik + insan tarafından okunur ad taşıyan basit seçenek.</summary>
public sealed class SiteBuilderOptionDto
{
    public string Id { get; set; } = string.Empty;

    /// <summary>Arayüzde gösterilecek çeviri anahtarı.</summary>
    public string LabelKey { get; set; } = string.Empty;
}

/// <summary>Renk paleti: arayüzde önizleme noktaları için renkleri de taşır.</summary>
public sealed class SiteBuilderPaletteDto
{
    public string Id { get; set; } = string.Empty;

    public string LabelKey { get; set; } = string.Empty;

    public string Accent { get; set; } = string.Empty;

    public string Surface { get; set; } = string.Empty;

    public string Text { get; set; } = string.Empty;
}
