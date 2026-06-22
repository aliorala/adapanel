using AdaPanel.Core.Dtos.Statistics;

namespace AdaPanel.Core.Interfaces;

/// <summary>
/// Site IIS erişim loglarını ayrıştırarak trafik istatistikleri üretir.
/// </summary>
public interface IWebStatisticsService
{
    Task<WebStatisticsDto> GetSiteStatisticsAsync(string siteName, int days, CancellationToken cancellationToken = default);
}
