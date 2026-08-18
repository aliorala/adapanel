namespace AdaPanel.Core.Dtos.Seo;

/// <summary>
/// TEK bir taramanın tam raporu. Diske yazılır (<c>config/seo/&lt;site&gt;.json</c>) ve geçmiş
/// karşılaştırması bu kayıtlar üzerinden yapılır.
/// </summary>
public sealed class SeoScanReportDto
{
    /// <summary>Tarama kimliği (geçmiş listesinden tek raporu çekmek için).</summary>
    public string Id { get; init; } = string.Empty;

    public string SiteName { get; init; } = string.Empty;

    /// <summary>Taranan konak adı — sitenin IIS bağlamalarından çözülür, KULLANICI GİRDİSİ DEĞİL.</summary>
    public string Host { get; init; } = string.Empty;

    public DateTime StartedAtUtc { get; init; }

    public DateTime CompletedAtUtc { get; init; }

    public long DurationMs { get; init; }

    /// <summary><c>completed</c> | <c>failed</c> | <c>cancelled</c>.</summary>
    public string Status { get; init; } = string.Empty;

    /// <summary>Gerçekten indirilen sayfa sayısı.</summary>
    public int PagesCrawled { get; init; }

    /// <summary>Bu taramada uygulanan sayfa üst sınırı.</summary>
    public int PageLimit { get; init; }

    /// <summary>
    /// Sınıra dayanıldı mı. TRUE ise rapor SİTENİN TAMAMI DEĞİLDİR — bunu saklamak, eksik bir
    /// taramayı "temiz" gibi göstermek olurdu.
    /// </summary>
    public bool LimitReached { get; init; }

    public IReadOnlyCollection<SeoFindingDto> Findings { get; init; } = [];

    public IReadOnlyCollection<SeoPageDto> Pages { get; init; } = [];

    public SeoRobotsDto Robots { get; init; } = new();

    public SeoSitemapDto Sitemap { get; init; } = new();

    /// <summary>Tarama hiç yürütülemediyse sebebi (dile bağımsız).</summary>
    public string Error { get; init; } = string.Empty;

    /// <summary>Bir önceki taramaya göre YENİ çıkan bulgu sayısı (önceki yoksa 0).</summary>
    public int NewFindingCount { get; init; }

    /// <summary>Bir önceki taramada olup bu taramada GÖRÜLMEYEN bulgu sayısı.</summary>
    public int ResolvedFindingCount { get; init; }

    /// <summary>Karşılaştırmanın yapıldığı önceki taramanın kimliği; ilk taramada boş.</summary>
    public string ComparedToId { get; init; } = string.Empty;
}
