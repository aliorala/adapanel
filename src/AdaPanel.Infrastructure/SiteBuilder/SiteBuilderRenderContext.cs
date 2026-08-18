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
    private SiteBuilderRenderContext(string imageUrlPrefix, bool inlineCss)
    {
        ImageUrlPrefix = imageUrlPrefix;
        InlineCss = inlineCss;
    }

    /// <summary>Görsel adreslerinin öneki. Sayfaya ÖZNİTELİK olarak girer.</summary>
    public string ImageUrlPrefix { get; }

    /// <summary>Stil sayfaya satır içi mi gömülecek (önizleme) yoksa bağlanacak mı (yayın).</summary>
    public bool InlineCss { get; }

    /// <summary>Diske yazılan sürüm: göreli adresler + ayrı CSS dosyası.</summary>
    public static readonly SiteBuilderRenderContext Published = new(SiteBuilderLayout.ImageFolderUrl, false);

    /// <summary>
    /// Panel önizlemesi: görseller panel API'sinden okunur, stil satır içi gömülür.
    /// </summary>
    /// <remarks>
    /// Önek panelin ÜRETTİĞİ bir adrestir, kullanıcı girdisi değil. Yine de burada doğrulanır:
    /// bu dizge bir <c>src="…"</c> özniteliğinin içine girer ve içinde tırnak/boşluk bulunması
    /// öznitelikten çıkmanın en kısa yolu olurdu. Kural, önekin nereden geldiğini hatırlamaya
    /// bırakılmaz — çağıran değişse bile burada durur.
    /// </remarks>
    public static SiteBuilderRenderContext ForPreview(string imageUrlPrefix)
    {
        if (string.IsNullOrWhiteSpace(imageUrlPrefix)
            || imageUrlPrefix.Any(character => char.IsWhiteSpace(character) || character is '"' or '\'' or '<' or '>' or '&'))
        {
            throw new AppException("sitebuilder.preview_prefix_invalid", 500, 500);
        }

        return new SiteBuilderRenderContext(imageUrlPrefix, true);
    }
}
