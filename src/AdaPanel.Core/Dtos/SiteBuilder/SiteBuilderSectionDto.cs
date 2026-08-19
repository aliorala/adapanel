namespace AdaPanel.Core.Dtos.SiteBuilder;

/// <summary>
/// Sayfanın tek bir bölümü (giriş / hakkında / hizmetler / galeri / iletişim).
/// </summary>
/// <remarks>
/// Bölüm TİPLERİ sabittir ve katalogdan gelir; kullanıcı yeni tip uyduramaz. Serbest bir "blok"
/// modeli, üretilen HTML'in şeklini kullanıcının denetimine bırakırdı — oysa üretilen işaretleme
/// panelin sorumluluğu ve kaçırmanın (escape) güvenilir kalması buna bağlı.
/// </remarks>
public sealed class SiteBuilderSectionDto
{
    /// <summary>Bölüm tipi: <c>hero</c>, <c>about</c>, <c>services</c>, <c>gallery</c>, <c>contact</c>.</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>Kapalı bölüm üretilen sayfada hiç yer almaz (silmeye gerek yok, içerik korunur).</summary>
    public bool Enabled { get; set; } = true;

    public string Heading { get; set; } = string.Empty;

    /// <summary>Bölümün düz metin gövdesi. Boş satırlar paragraf ayırıcı sayılır.</summary>
    public string Body { get; set; } = string.Empty;

    /// <summary>Bölüm görselinin panel içindeki dosya adı (yüklenen görsel). Boş = görselsiz.</summary>
    public string ImageName { get; set; } = string.Empty;

    /// <summary>Hizmet kartları / galeri görselleri.</summary>
    public List<SiteBuilderItemDto> Items { get; set; } = [];

    /// <summary>İletişim bölümü: e-posta. Geçersiz biçim yayında sessizce ATILIR, uydurulmaz.</summary>
    public string Email { get; set; } = string.Empty;

    public string Phone { get; set; } = string.Empty;

    public string Address { get; set; } = string.Empty;
}
