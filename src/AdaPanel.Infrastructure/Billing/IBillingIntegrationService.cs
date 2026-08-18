using AdaPanel.Core.Dtos.Billing;

namespace AdaPanel.Infrastructure.Billing;

/// <summary>
/// Faturalama sistemi (WHMCS) entegrasyonunun panel tarafındaki tek giriş noktası.
/// </summary>
/// <remarks>
/// <para>
/// Panelde abonelik altyapısı ZATEN VAR (servis planları, kotalar, askıya alma). Bu servis yeni bir
/// abonelik modeli KURMAZ; var olanı faturalama sisteminin okuyabileceği biçime çevirir ve
/// entegrasyona özgü iki şeyi ekler: toplu kullanım raporu ve silme öncesi güvenlik denetimi.
/// </para>
/// </remarks>
public interface IBillingIntegrationService
{
    /// <summary>Entegrasyon ayarı ve son çağrıların izi (ayar ekranı bunu okur).</summary>
    Task<BillingSettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default);

    /// <summary>Entegrasyonu açar/kapatır.</summary>
    Task<BillingSettingsDto> UpdateSettingsAsync(bool enabled, CancellationToken cancellationToken = default);

    /// <summary>
    /// Bütün aboneliklerin kullanımını WHMCS birimlerinde (MB) döner.
    /// Entegrasyon kapalıysa <c>billing.disabled</c> ile 403 fırlatır.
    /// </summary>
    Task<BillingUsageReportDto> BuildUsageReportAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Bir hesabın faturalama isteğiyle silinmesinin güvenli olup olmadığına karar verir.
    /// Entegrasyon kapalıysa <c>billing.disabled</c> ile 403 fırlatır.
    /// </summary>
    Task<BillingTerminationCheckDto> CheckTerminationAsync(
        string? userName,
        string? domain,
        CancellationToken cancellationToken = default);
}
