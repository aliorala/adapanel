namespace AdaPanel.Core.Dtos.Seo;

/// <summary>Geçmiş listesinde gösterilen ÖZET (tam rapor değil).</summary>
/// <remarks>
/// Geçmiş beş taramanın tamamını (sayfa listeleriyle birlikte) her yoklamada göndermek, ekranı
/// tazelemeyi megabaytlık bir işe çevirirdi. Tam rapor ayrı uçtan istenir.
/// </remarks>
public sealed class SeoScanSummaryDto
{
    public string Id { get; init; } = string.Empty;

    public DateTime CompletedAtUtc { get; init; }

    public string Status { get; init; } = string.Empty;

    public int PagesCrawled { get; init; }

    public int ErrorCount { get; init; }

    public int WarningCount { get; init; }

    public int InfoCount { get; init; }

    public int NewFindingCount { get; init; }

    public int ResolvedFindingCount { get; init; }
}

/// <summary>
/// SEO ekranının tek okuma ucu: canlı durum + son rapor + geçmiş özetleri.
/// </summary>
public sealed class SeoOverviewDto
{
    public string SiteName { get; init; } = string.Empty;

    /// <summary>
    /// Taranabilecek konak adı (IIS bağlamalarından). Boşsa sitede alan adı yoktur ve tarama
    /// BAŞLATILAMAZ — arayüz düğmeyi bu yüzden kapatır.
    /// </summary>
    public string Host { get; init; } = string.Empty;

    public SeoScanStateDto State { get; init; } = new();

    /// <summary>En son tamamlanan tarama (hiç taranmadıysa null).</summary>
    public SeoScanReportDto? Latest { get; init; }

    /// <summary>Yeniden eskiye doğru geçmiş özetleri (en fazla saklanan kadar).</summary>
    public IReadOnlyCollection<SeoScanSummaryDto> History { get; init; } = [];
}
