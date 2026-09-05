namespace AdaPanel.Core.Dtos.Sites;

/// <summary>
/// Bir sitenin ALAN ADI TAKMA ADI: aynı içeriği ikinci bir alan adından da yayınlar.
/// </summary>
/// <remarks>
/// <para>
/// En sık gelen talep <c>.com</c> ve <c>.com.tr</c> çiftini aynı siteye yöneltmektir. Panelde bu
/// kavram hiç yoktu; kullanıcı ikinci bir site açmak zorunda kalıyor, sonra iki kopyayı ayrı ayrı
/// güncellemeye çalışıyordu.
/// </para>
/// <para>
/// Teknik karşılığı IIS'te aynı siteye ikinci bir bağlamadır. Panelin kattığı değer, bunu
/// yönetilebilir bir kavram hâline getirmesi: sahiplik denetimi, <c>www</c> çifti, isteğe bağlı
/// kanonik yönlendirme ve silerken artık bırakmama.
/// </para>
/// </remarks>
public sealed class SiteAliasDto
{
    /// <summary>Takma alan adı (ör. ornek.com.tr).</summary>
    public string HostName { get; set; } = string.Empty;

    /// <summary>
    /// true ise ziyaretçi ANA alan adına 301 ile yönlendirilir; false ise aynı içerik bu adresten
    /// de servis edilir.
    /// <para>
    /// Varsayılan olarak yönlendirme ÖNERİLİR: iki alan adından aynı içeriği sunmak arama
    /// motorlarında yinelenen içerik sayılır ve ikisi de zayıflar.
    /// </para>
    /// </summary>
    public bool RedirectToPrimary { get; set; }

    /// <summary>Bu takma ad için HTTPS bağlaması var mı (sertifika alınmış mı).</summary>
    public bool HasHttps { get; set; }

    /// <summary><c>www.</c> önekli çifti de bağlandı mı.</summary>
    public bool IncludesWww { get; set; }
}

/// <summary>Sitenin ana alan adı + takma adları.</summary>
public sealed class SiteAliasWorkspaceDto
{
    public string SiteName { get; set; } = string.Empty;

    /// <summary>Yönlendirmenin hedefi olan ana alan adı; boşsa sitenin hiç host adı yok demektir.</summary>
    public string PrimaryHostName { get; set; } = string.Empty;

    public IReadOnlyCollection<SiteAliasDto> Aliases { get; set; } = [];

    /// <summary>
    /// Kanonik yonlendirme kuralinin web.config'e GERCEKTEN yazildiginin olcumu. Yalniz YAZMA
    /// yollarinda (ekle/sil) doldurulur; salt okuma yolunda OLCULMEDI kalir -- gecmis bir
    /// olcumu bugunun gercegi gibi gostermeyiz.
    /// </summary>
    public AliasRedirectRuleMeasurement RedirectRule { get; set; } = AliasRedirectRuleMeasurement.Olculemedi();

    /// <summary>Bos ise her sey olculdu ve yolunda; dolu ise gosterilecek uyari anahtari.</summary>
    public string RedirectRuleNoticeKey => RedirectRule.NoticeKey;
}

/// <summary>Takma ad ekleme isteği.</summary>
public sealed class AddSiteAliasRequest
{
    public string HostName { get; set; } = string.Empty;

    /// <summary>Ana alan adına 301 yönlendir (önerilen).</summary>
    public bool RedirectToPrimary { get; set; } = true;

    /// <summary><c>www.</c> önekli çifti de bağla.</summary>
    public bool IncludeWww { get; set; } = true;
}
