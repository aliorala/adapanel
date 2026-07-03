using AdaPanel.Core.Dtos.Advisor;

namespace AdaPanel.Core.Interfaces;

/// <summary>
/// Plesk "Advisor" karşılığı: panelin mevcut sinyallerini (güncelleme, SSL bitişi, disk/bellek,
/// otomatik yedek/yenileme, güvenlik ayarları) tarayıp önerilere dönüştürür.
/// </summary>
public interface IAdvisorService
{
    Task<AdvisorReportDto> GetReportAsync(CancellationToken cancellationToken = default);
}
