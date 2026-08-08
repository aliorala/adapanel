namespace AdaPanel.Core.Dtos.Statistics;

/// <summary>
/// Bir sitenin IIS (W3C) erişim loglarından üretilen trafik istatistikleri.
/// </summary>
public sealed class WebStatisticsDto
{
    public string SiteName { get; set; } = string.Empty;

    public int PeriodDays { get; set; }

    public DateTime GeneratedAtUtc { get; set; }

    public string LogPath { get; set; } = string.Empty;

    public bool HasData { get; set; }

    public long TotalRequests { get; set; }

    public long UniqueVisitors { get; set; }

    /// <summary>
    /// Ayrı ziyaretçi sayısı üst sınıra dayandı mı. true ise <see cref="UniqueVisitors"/> gerçek
    /// sayının ALTINDADIR ve ekranda "+" ile gösterilir; doymuş bir sayacı kesin sayı gibi sunmak
    /// yanlış bilgi olurdu.
    /// </summary>
    public bool UniqueVisitorsApproximate { get; set; }

    public long TotalBytes { get; set; }

    public long Status2xx { get; set; }

    public long Status3xx { get; set; }

    public long Status4xx { get; set; }

    public long Status5xx { get; set; }

    public IReadOnlyCollection<StatBucketDto> TopPages { get; set; } = [];

    public IReadOnlyCollection<StatBucketDto> TopReferrers { get; set; } = [];

    public IReadOnlyCollection<StatBucketDto> DailyHits { get; set; } = [];
}
