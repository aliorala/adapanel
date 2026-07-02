using AdaPanel.Core.Dtos.Sites;

namespace AdaPanel.Core.Interfaces;

public interface IDnsService
{
    Task<SiteDnsZoneDto> GetZoneAsync(string siteName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Windows DNS Server rolünü (Windows Server'da) kurar ve site bölgesini hemen Windows
    /// DNS'e yayınlar. Böylece VPS gerçek yetkili ns1/ns2 olarak yanıt verir.
    /// </summary>
    Task<SiteDnsZoneDto> InstallWindowsDnsAsync(string siteName, CancellationToken cancellationToken = default);

    Task<SiteDnsZoneDto> UpdateZoneAsync(string siteName, UpdateDnsZoneRequest request, CancellationToken cancellationToken = default);

    Task<SiteDnsZoneDto> ResetDefaultsAsync(string siteName, CancellationToken cancellationToken = default);

    Task<SiteDnsZoneDto> UpsertRecordAsync(string siteName, UpsertDnsRecordRequest request, CancellationToken cancellationToken = default);

    Task<SiteDnsZoneDto> DeleteRecordAsync(string siteName, string recordId, CancellationToken cancellationToken = default);

    /// <summary>Site silindiğinde panel DNS bölgesini (config kaydı) tamamen kaldırır. IIS sitesi
    /// artık mevcut olmasa bile çalışır; idempotenttir.</summary>
    Task DeleteZoneAsync(string siteName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Site bölgesini (varsayılan kayıtlarıyla) Windows DNS Server'a yayınlar. Site oluşturulurken
    /// tam otomatik çalışması için çağrılır; Windows DNS yoksa veya yazılamazsa sessizce geçer
    /// (site oluşturmayı asla bozmaz).
    /// </summary>
    Task EnsureZonePublishedAsync(string siteName, CancellationToken cancellationToken = default);

    /// <summary>Sunucu-geneli DNS şablonunu döner (kayıtlı yoksa yerleşik varsayılan şablon).</summary>
    Task<DnsTemplateDto> GetTemplateAsync(CancellationToken cancellationToken = default);

    /// <summary>Sunucu-geneli DNS şablonunu kaydeder; bundan sonra yeni bölgeler bu şablonla seed edilir.</summary>
    Task<DnsTemplateDto> UpdateTemplateAsync(UpdateDnsTemplateRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Bölge için Windows DNS Server DNSSEC'i açar (Invoke-DnsServerZoneSign) ya da kapatır (Unsign). Plesk
    /// DNSSEC eşdeğeri; Windows DNS Server gerektirir. Açıkken DTO'da registrar'a verilecek DS kayıtları döner.
    /// </summary>
    Task<SiteDnsZoneDto> SetDnssecAsync(string siteName, bool enabled, CancellationToken cancellationToken = default);
}
