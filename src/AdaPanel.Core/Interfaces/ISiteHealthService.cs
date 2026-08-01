using AdaPanel.Core.Dtos.Health;

namespace AdaPanel.Core.Interfaces;

/// <summary>
/// Sitenin DIŞARIDAN görünen sağlığını ölçer. "Servis çalışıyor" sinyallerine bakmaz; halka açık
/// çözümleyicilerden DNS sorar, siteye gerçek HTTP isteği atar, 443'te sunulan sertifikayı okur ve
/// mail kayıtlarını canlı sorgular. Her kontrol izoledir; biri patlarsa diğerleri raporlanır.
/// </summary>
public interface ISiteHealthService
{
    Task<SiteHealthDto> GetSiteHealthAsync(string siteName, CancellationToken cancellationToken = default);
}
