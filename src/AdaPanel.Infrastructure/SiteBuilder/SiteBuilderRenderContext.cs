using AdaPanel.Core.Common;

namespace AdaPanel.Infrastructure.SiteBuilder;

/// <summary>
/// Aynı sayfanın iki bağlamı: diske YAZILAN sürüm ve panelde ÖNİZLENEN sürüm.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden tek oluşturucu, iki bağlam:</b> önizleme ayrı bir kod yoluyla üretilseydi, "önizlemede
/// gördüğüm sayfa" ile "yayınlanan sayfa" farklı olurdu — üstelik en tehlikeli farkla: kaçırma
/// (escape) mantığının iki kopyası olurdu ve biri geride kalırdı. Burada yalnız iki şey değişir:
/// görsellerin adresi ve stilin sayfaya nasıl bağlandığı. İşaretlemenin ve kaçırmanın tamamı ortak.
/// </para>
/// <para>
/// <b>Neden önizlemede stil satır içi:</b> önizleme panelde bir <c>iframe srcdoc</c> içinde
/// gösterilir; göreli bir <c>&lt;link&gt;</c> adresi orada çözülemez ve sayfa stilsiz görünürdü —
/// yani önizleme, yayınlanacak sayfayı temsil etmezdi.
/// </para>
/// </remarks>
public sealed record SiteBuilderRenderContext
{
    private SiteBuilderRenderContext(string imageUrlPrefix, bool inlineCss, string contactFormAction)
    {
        ImageUrlPrefix = imageUrlPrefix;
        InlineCss = inlineCss;
        ContactFormAction = contactFormAction;
    }

    /// <summary>Görsel adreslerinin öneki. Sayfaya ÖZNİTELİK olarak girer.</summary>
    public string ImageUrlPrefix { get; }

    /// <summary>Stil sayfaya satır içi mi gömülecek (önizleme) yoksa bağlanacak mı (yayın).</summary>
    public bool InlineCss { get; }

    /// <summary>
    /// İletişim formunun gönderim hedefi (panelin genel adresindeki kimliksiz uç). BOŞSA form
    /// HİÇ basılmaz.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Boş olması "form yok" demektir, "form var ama gitmiyor" değil. Bu ayrım özelliğin var olma
    /// sebebi: gönderilemeyen bir form, ziyaretçinin gönderdiğini sanıp yanıt beklemesi demektir.
    /// </para>
    /// <para>
    /// Adres MUTLAKTIR (<c>https://panel.ornek.com/…</c>) ve olmak zorundadır: form MÜŞTERİNİN
    /// alan adında yayınlanan bir sayfada duruyor, panel ise başka bir ana adda. Göreli bir adres
    /// müşterinin kendi sitesine POST ederdi ve orada böyle bir uç yok — form 404 alırdı.
    /// </para>
    /// </remarks>
    public string ContactFormAction { get; }

    /// <summary>Diske yazılan sürüm: göreli adresler + ayrı CSS dosyası, form YOK.</summary>
    /// <remarks>
    /// Form içeren yayın için <see cref="ForPublish"/> kullanılır. Varsayılanın formsuz olması
    /// bilinçli: form hedefi unutulduğunda ya da çözülemediğinde davranış "formu bas ve umut et"
    /// değil, "hiç basma" olur.
    /// </remarks>
    public static readonly SiteBuilderRenderContext Published = new(SiteBuilderLayout.ImageFolderUrl, false, string.Empty);

    /// <summary>Diske yazılan sürüm + çalışan bir iletişim formu hedefi.</summary>
    public static SiteBuilderRenderContext ForPublish(string? contactFormAction) =>
        new(SiteBuilderLayout.ImageFolderUrl, false, ValidateAction(contactFormAction));

    /// <summary>
    /// Panel önizlemesi: görseller panel API'sinden okunur, stil satır içi gömülür.
    /// </summary>
    /// <remarks>
    /// Önek panelin ÜRETTİĞİ bir adrestir, kullanıcı girdisi değil. Yine de burada doğrulanır:
    /// bu dizge bir <c>src="…"</c> özniteliğinin içine girer ve içinde tırnak/boşluk bulunması
    /// öznitelikten çıkmanın en kısa yolu olurdu. Kural, önekin nereden geldiğini hatırlamaya
    /// bırakılmaz — çağıran değişse bile burada durur.
    /// </remarks>
    public static SiteBuilderRenderContext ForPreview(string imageUrlPrefix, string? contactFormAction = null)
    {
        if (string.IsNullOrWhiteSpace(imageUrlPrefix)
            || imageUrlPrefix.Any(character => char.IsWhiteSpace(character) || character is '"' or '\'' or '<' or '>' or '&'))
        {
            throw new AppException("sitebuilder.preview_prefix_invalid", 500, 500);
        }

        return new SiteBuilderRenderContext(imageUrlPrefix, true, ValidateAction(contactFormAction));
    }

    /// <summary>
    /// Form hedefi bir <c>action="…"</c> özniteliğine girer; öznitelikten çıkmaya yarayacak
    /// hiçbir karakter taşıyamaz.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Bu adres panelin KENDİ ürettiği bir dizgedir (ana ad ayarından + sabit yol + site anahtarı).
    /// Doğrulama yine de burada duruyor: kaçırmanın doğruluğu, çağıranın bugün ne gönderdiğini
    /// hatırlamaya bırakılamaz. Aynı gerekçe önizleme öneki için de yazılı.
    /// </para>
    /// <para>
    /// Şema de zorlanır: <c>javascript:</c> gibi bir şemayla bir <c>action</c>, form gönderiminde
    /// kod çalıştırır. Yalnız <c>https://</c> kabul edilir — <c>http</c> bile değil, çünkü form
    /// ziyaretçinin adını, adresini ve mesajını taşır ve bunu şifresiz göndermek kullanıcıya
    /// haber vermeden alınmış bir karar olurdu.
    /// </para>
    /// </remarks>
    private static string ValidateAction(string? action)
    {
        var value = (action ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            return string.Empty;
        }

        if (!value.StartsWith("https://", StringComparison.Ordinal)
            || value.Any(character => char.IsWhiteSpace(character)
                || char.IsControl(character)
                || character is '"' or '\'' or '<' or '>' or '&'))
        {
            throw new AppException("sitebuilder.form_action_invalid", 500, 500);
        }

        return value;
    }
}
