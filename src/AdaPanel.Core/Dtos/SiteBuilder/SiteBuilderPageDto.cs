namespace AdaPanel.Core.Dtos.SiteBuilder;

/// <summary>
/// Ana sayfanın YANINDAKİ ek bir sayfa (ör. <c>/hakkimizda/</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden ana sayfa bu listede DEĞİL:</b> ana sayfanın içeriği zaten
/// <see cref="SiteBuilderProjectDto.Sections"/> alanında duruyor ve kayıtlı tasarımların hepsi o
/// biçimde yazılmış. Ana sayfayı da listeye taşımak bir GÖÇ işlemi gerektirirdi ve göç sırasında
/// bir hata, kullanıcının kayıtlı içeriğini kaybettirirdi. Burada iki kaynak yok: ana sayfa
/// <c>Sections</c>, ek sayfalar <c>Pages</c>. Her ikisi de aynı bölüm modelini kullanır, yani
/// oluşturucu tek kod yoluyla çalışır.
/// </para>
/// <para>
/// <b>Neden <c>{slug}/index.html</c>, <c>{slug}.html</c> değil:</b> klasör + <c>index.html</c>,
/// IIS'in varsayılan belgesiyle temiz adres (<c>/hakkimizda/</c>) verir. <c>.html</c> uzantılı
/// biçim temiz adres için URL Rewrite kuralı isterdi; o kural sitenin <c>web.config</c>'ine
/// yazmak demektir ve <c>web.config</c> bu kurucunun ASLA dokunmadığı dosyalardan biridir
/// (<c>SiteBuilderLayout.NeverTouch</c>). Yani bu seçim, dokunulmazlık kuralını korumak için.
/// </para>
/// </remarks>
public sealed class SiteBuilderPageDto
{
    /// <summary>
    /// Adres parçası: <c>/{slug}/</c>. Yalnız küçük harf, rakam ve tire.
    /// </summary>
    /// <remarks>
    /// Bu değer bir KLASÖR ADINA ve bir <c>href</c> özniteliğine giriyor; ikisi de kullanıcı
    /// metninin en tehlikeli iki hedefi. Kalıp <c>SiteBuilderPageSlug</c> içinde tek yerde
    /// uygulanır ve kalıba uymayan sayfa kaydedilirken DÜŞÜRÜLÜR — "en iyi çaba ile düzelt"
    /// yoluna gidilmez, çünkü düzeltilmiş bir adres kullanıcının beklemediği bir yola yazardı.
    /// </remarks>
    public string Slug { get; set; } = string.Empty;

    /// <summary>Sayfanın başlığı: tarayıcı sekmesinde ve gezinme çubuğunda görünür.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Bu sayfaya özel arama motoru açıklaması. Boşsa sitenin genel açıklaması kullanılır.</summary>
    public string MetaDescription { get; set; } = string.Empty;

    /// <summary>
    /// Gezinme çubuğunda görünsün mü.
    /// </summary>
    /// <remarks>
    /// Kapatmak sayfayı SİLMEZ, yalnız menüden çıkarır: adresi bilen ziyaretçi sayfaya yine
    /// ulaşır. Bunu "gizli sayfa" sanmasın diye arayüzde açıkça yazılı.
    /// </remarks>
    public bool ShowInNav { get; set; } = true;

    public List<SiteBuilderSectionDto> Sections { get; set; } = [];
}
