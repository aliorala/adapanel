namespace AdaPanel.Core.Dtos.Seo;

/// <summary>
/// Tarayıcının gezdiği TEK bir sayfanın ölçümü. Bulgular bu listeden türetilir
/// (bkz. <c>SeoFindingRules</c>), yani ham ölçüm ile yorum ayrı durur.
/// </summary>
/// <remarks>
/// Ham ölçümün ayrı saklanmasının sebebi: kullanıcı "neden bu bulgu çıktı" diye sorduğunda
/// yanıtı gösterebilmek. Yalnız bulgu saklansaydı rapor, kendi kanıtını taşımayan bir iddia olurdu.
/// </remarks>
public sealed class SeoPageDto
{
    /// <summary>Null for historical reports that did not measure these signals.</summary>
    public SeoContentSignalsDto? ContentSignals { get; init; }

    /// <summary>Site kökünden yol (<c>/</c>, <c>/hakkimizda</c>). Tam adres DEĞİL — konak adı sitenin kendisidir.</summary>
    public string Path { get; init; } = string.Empty;

    public int StatusCode { get; init; }

    /// <summary>İlk baytın gelmesi dahil toplam yanıt süresi (ms).</summary>
    public long ResponseMs { get; init; }

    public string ContentType { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    public string MetaDescription { get; init; } = string.Empty;

    public int H1Count { get; init; }

    /// <summary>Sayfadaki toplam görsel sayısı.</summary>
    public int ImageCount { get; init; }

    /// <summary><c>alt</c> metni boş/eksik olan görsel sayısı.</summary>
    public int ImagesWithoutAlt { get; init; }

    /// <summary>Bu sayfaya kaç yönlendirme atlanarak ulaşıldı (0 = doğrudan).</summary>
    public int RedirectDepth { get; init; }

    /// <summary>Yönlendirme varsa son hedef yol; yoksa boş.</summary>
    public string RedirectTo { get; init; } = string.Empty;

    /// <summary><c>&lt;meta name="robots" content="noindex"&gt;</c> var mı.</summary>
    public bool NoIndex { get; init; }

    /// <summary>Bu sayfaya ilk kez hangi sayfadan bağlantı verildi (kırık bağlantıyı bulmak için).</summary>
    public string LinkedFrom { get; init; } = string.Empty;

    /// <summary>İstek hiç tamamlanamadıysa hata metni (zaman aşımı, bağlantı reddi). Doluysa ölçüm YOKTUR.</summary>
    public string Error { get; init; } = string.Empty;
}
