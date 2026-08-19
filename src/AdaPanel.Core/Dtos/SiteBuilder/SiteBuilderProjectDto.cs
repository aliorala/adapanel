namespace AdaPanel.Core.Dtos.SiteBuilder;

/// <summary>
/// Bir sitenin "site kurucu" tasarımı: şablon + renk/yazı tipi seçimi + bölüm içerikleri.
/// </summary>
/// <remarks>
/// <para>
/// Bu nesne HEM kaydedilen durum HEM de üretilecek HTML'in tek kaynağıdır. Yayınlanan HTML
/// ayrıca saklanmaz; her yayında bu tasarımdan YENİDEN üretilir. Sebep: iki kaynak olsaydı
/// (kayıtlı tasarım + diskteki HTML) biri düzenlenip diğeri güncellenmediğinde kullanıcı
/// panelde bir şey, sitesinde başka bir şey görürdü — ve hangisinin doğru olduğu bilinemezdi.
/// </para>
/// <para>
/// Tasarım panelin veri kökünde tutulur, müşterinin site klasöründe DEĞİL. Böylece kullanıcı
/// "Yayınla" demeden müşterinin dosyalarına hiçbir şey yazılmaz.
/// </para>
/// </remarks>
public sealed class SiteBuilderProjectDto
{
    public string SiteName { get; set; } = string.Empty;

    /// <summary>Şablon kimliği (katalogdan). Bilinmeyen değer varsayılana düşer.</summary>
    public string TemplateId { get; set; } = string.Empty;

    /// <summary>Renk paleti kimliği (katalogdan).</summary>
    public string PaletteId { get; set; } = string.Empty;

    /// <summary>Yazı tipi kimliği (katalogdan).</summary>
    public string FontId { get; set; } = string.Empty;

    /// <summary>Tarayıcı sekmesinde ve üst başlıkta görünen ad.</summary>
    public string SiteTitle { get; set; } = string.Empty;

    /// <summary>Başlığın altındaki kısa slogan.</summary>
    public string Tagline { get; set; } = string.Empty;

    /// <summary>Arama motorları için kısa açıklama (meta description).</summary>
    public string MetaDescription { get; set; } = string.Empty;

    /// <summary>
    /// Sayfanın dili (<c>tr</c> / <c>en</c>) — üretilen HTML'in <c>lang</c> özniteliği.
    /// </summary>
    /// <remarks>
    /// Ekran okuyucular telaffuzu, tarayıcılar çeviri önerisini ve arama motorları hedef kitleyi
    /// buradan okur. Panelin dilinden ayrı bir alan: müşteri panelini Türkçe kullanıp sitesini
    /// İngilizce yayınlayabilir.
    /// </remarks>
    public string Language { get; set; } = "tr";

    public List<SiteBuilderSectionDto> Sections { get; set; } = [];

    public DateTime? UpdatedAtUtc { get; set; }

    /// <summary>Daha önce en az bir kez yayınlandı mı.</summary>
    public bool Published { get; set; }

    public DateTime? PublishedAtUtc { get; set; }

    /// <summary>Son yayının yazıldığı klasör (bilgi amaçlı; yayın anında yeniden çözülür).</summary>
    public string PublishedPath { get; set; } = string.Empty;

    /// <summary>
    /// Son yayında KURUCUNUN yazdığı dosyaların site köküne göreli yolları.
    /// </summary>
    /// <remarks>
    /// Üzerine yazma uyarısının doğru çalışması buna bağlıdır: kendi ürettiğimiz bir dosyayı
    /// "müşterinin dosyası" sanıp her yayında onay istersek uyarı gürültüye döner ve kullanıcı
    /// onu okumadan onaylamayı öğrenir — yani uyarı, korumak istediği anda işe yaramaz.
    /// </remarks>
    public List<string> ManagedFiles { get; set; } = [];
}
