using AdaPanel.Core.Dtos.SiteBuilder;

namespace AdaPanel.Infrastructure.SiteBuilder;

/// <summary>
/// Kurucunun sunduğu SABİT seçenekler: şablonlar, renk paletleri, yazı tipleri, bölüm tipleri.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden sabit ve neden sunucuda:</b> üretilen CSS'in içine giren her değer (renk kodu, yazı
/// tipi yığını, kenar boşluğu) buradan gelir; hiçbiri kullanıcı girdisinden türemez. Bu, CSS
/// enjeksiyonu diye bir soruyu tümüyle ortadan kaldırır — kullanıcı metni yalnızca HTML'in metin
/// düğümlerine girer ve orada kaçırılır. Kullanıcıya serbest renk kodu yazdırsaydık,
/// <c>red;}body{background:url(javascript:…)</c> gibi bir değerin kaçırılmasını ayrıca doğru
/// yapmak zorunda kalırdık.
/// </para>
/// <para>
/// <b>Neden web fontu yok:</b> yazı tipleri işletim sisteminde bulunan yığınlardan seçilir. Dışarıdan
/// font çekmek, müşterinin ziyaretçilerini üçüncü bir sunucuya bağlar (gizlilik) ve o sunucu
/// erişilemezse sayfa geç açılır. Panelin kendisi de dış kaynak kullanmıyor.
/// </para>
/// </remarks>
public static class SiteBuilderCatalog
{
    /// <summary>Görsel yükleme üst sınırı. Bir tanıtım sayfası için 5 MB fazlasıyla yeterli.</summary>
    public const long MaxImageBytes = 5L * 1024L * 1024L;

    /// <summary>Sayfadaki bölüm sırası. Kullanıcı bölüm ekleyip çıkaramaz, yalnız açıp kapatır.</summary>
    public static readonly IReadOnlyList<string> SectionTypes =
    [
        "hero",
        "about",
        "services",
        "gallery",
        "contact"
    ];

    public static readonly IReadOnlyList<SiteBuilderTemplate> Templates =
    [
        // Sade: tek sütun, ortalanmış, gezinme çubuğu yok. Tek sayfalık tanıtım için en az gürültü.
        new("sade", ShowNav: false, HeroStyle: "plain", CardStyle: "flat"),
        // Vitrin: görseli öne çıkaran şerit + gölgeli kartlar. Fotoğrafı olan işler için.
        new("vitrin", ShowNav: false, HeroStyle: "banner", CardStyle: "raised"),
        // Kurumsal: üstte bölüm bağlantıları, iki sütunlu "hakkında", çerçeveli bölümler.
        new("kurumsal", ShowNav: true, HeroStyle: "split", CardStyle: "bordered")
    ];

    public static readonly IReadOnlyList<SiteBuilderPalette> Palettes =
    [
        new("okyanus", Accent: "#2563eb", AccentDark: "#1d4ed8", Surface: "#ffffff", SurfaceAlt: "#f1f5f9", Text: "#0f172a", TextMuted: "#475569", OnAccent: "#ffffff", Border: "#e2e8f0"),
        new("orman", Accent: "#15803d", AccentDark: "#166534", Surface: "#ffffff", SurfaceAlt: "#f0fdf4", Text: "#14251a", TextMuted: "#4b5563", OnAccent: "#ffffff", Border: "#dcfce7"),
        new("gunbatimi", Accent: "#c2410c", AccentDark: "#9a3412", Surface: "#fffbf7", SurfaceAlt: "#ffedd5", Text: "#2b1608", TextMuted: "#7c5a45", OnAccent: "#ffffff", Border: "#fed7aa"),
        new("mor", Accent: "#7c3aed", AccentDark: "#6d28d9", Surface: "#ffffff", SurfaceAlt: "#f5f3ff", Text: "#1e1b2e", TextMuted: "#544f6b", OnAccent: "#ffffff", Border: "#e9e2ff"),
        new("gece", Accent: "#60a5fa", AccentDark: "#3b82f6", Surface: "#0f172a", SurfaceAlt: "#16233c", Text: "#e2e8f0", TextMuted: "#9aa9bf", OnAccent: "#0b1220", Border: "#243046")
    ];

    public static readonly IReadOnlyList<SiteBuilderFont> Fonts =
    [
        new("sistem", "system-ui, -apple-system, 'Segoe UI', Roboto, Arial, sans-serif"),
        new("zarif", "Georgia, 'Times New Roman', Times, serif"),
        new("yumusak", "'Trebuchet MS', 'Segoe UI', Verdana, Geneva, sans-serif")
    ];

    /// <summary>Bilinmeyen kimlik varsayılana düşer — kayıtlı tasarım hiçbir zaman "açılmaz" hâle gelmez.</summary>
    public static SiteBuilderTemplate ResolveTemplate(string? id) =>
        Templates.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase)) ?? Templates[0];

    public static SiteBuilderPalette ResolvePalette(string? id) =>
        Palettes.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase)) ?? Palettes[0];

    public static SiteBuilderFont ResolveFont(string? id) =>
        Fonts.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase)) ?? Fonts[0];

    public static bool IsKnownSectionType(string? type) =>
        SectionTypes.Any(item => string.Equals(item, type, StringComparison.OrdinalIgnoreCase));

    /// <summary>Arayüzün göreceği katalog.</summary>
    public static SiteBuilderCatalogDto ToDto() => new()
    {
        Templates = Templates
            .Select(item => new SiteBuilderOptionDto { Id = item.Id, LabelKey = $"panel.siteBuilder.template.{item.Id}" })
            .ToList(),
        Palettes = Palettes
            .Select(item => new SiteBuilderPaletteDto
            {
                Id = item.Id,
                LabelKey = $"panel.siteBuilder.palette.{item.Id}",
                Accent = item.Accent,
                Surface = item.Surface,
                Text = item.Text
            })
            .ToList(),
        Fonts = Fonts
            .Select(item => new SiteBuilderOptionDto { Id = item.Id, LabelKey = $"panel.siteBuilder.font.{item.Id}" })
            .ToList(),
        SectionTypes = SectionTypes.ToList(),
        MaxImageBytes = MaxImageBytes,
        AllowedImageExtensions = SiteBuilderImageValidator.AllowedExtensions.ToList()
    };
}

/// <summary>Şablonun düzen kararları. Şablonlar yalnız CSS değil, İŞARETLEME de değiştirir.</summary>
public sealed record SiteBuilderTemplate(string Id, bool ShowNav, string HeroStyle, string CardStyle);

public sealed record SiteBuilderPalette(
    string Id,
    string Accent,
    string AccentDark,
    string Surface,
    string SurfaceAlt,
    string Text,
    string TextMuted,
    string OnAccent,
    string Border);

public sealed record SiteBuilderFont(string Id, string Stack);
