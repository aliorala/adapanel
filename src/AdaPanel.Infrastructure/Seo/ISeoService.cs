using AdaPanel.Core.Dtos.Seo;

namespace AdaPanel.Infrastructure.Seo;

/// <summary>
/// SEO araç kutusunun denetleyiciye bakan yüzü.
/// </summary>
/// <remarks>
/// <b>Neden bu arayüz <c>AdaPanel.Core/Interfaces</c> içinde değil:</b> bu modülün tamamı
/// (DTO'lar hariç) Infrastructure'a ait tek bir klasörde tutuluyor. Api katmanı zaten
/// Infrastructure'a referans veriyor (bkz. <c>AdaPanel.Api.csproj</c>), bu yüzden ayrıca Core'a
/// dokunmak gerekmedi.
/// </remarks>
public interface ISeoService
{
    /// <summary>Ekranın tek okuma ucu: canlı durum + son rapor + geçmiş özetleri.</summary>
    Task<SeoOverviewDto> GetOverviewAsync(string siteName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Taramayı KUYRUĞA ALIR ve hemen döner. Hedef, site adından türetilir; istek gövdesi hedefi
    /// belirlemez (bkz. <see cref="SeoTargetResolver"/>).
    /// </summary>
    Task<SeoScanStateDto> StartScanAsync(
        string siteName,
        StartSeoScanRequest request,
        Guid? requestedBy,
        CancellationToken cancellationToken = default);

    /// <summary>Süren ya da sırada bekleyen taramayı iptal eder.</summary>
    SeoScanStateDto CancelScan(string siteName);

    /// <summary>Geçmişten tek bir raporun tamamı.</summary>
    Task<SeoScanReportDto> GetReportAsync(string siteName, string reportId, CancellationToken cancellationToken = default);
}
