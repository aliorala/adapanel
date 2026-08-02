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

    /// <summary>
    /// Eksik varsayılan kayıtları EKLER; var olan hiçbir kaydı silmez veya değiştirmez.
    /// <para>
    /// <see cref="ResetDefaultsAsync"/>'ten farkı budur: "sıfırla" yönetilen kayıtları silip yeniden
    /// kurar ve kullanıcının elle düzelttiği bir MX/SPF değerini geri alır. Tazeleme işlemi eski alan
    /// adlarında toplu çalıştığı için yıkıcı olmamak ZORUNDA.
    /// </para>
    /// </summary>
    /// <param name="applyChanges">
    /// false ise HİÇBİR ŞEY YAZILMAZ, yalnızca eksik olanlar hesaplanır. Kuru çalıştırma şart:
    /// panelin "şu alan adları eski kalmış" uyarısını kendiliğinden gösterebilmesi için durumu
    /// DEĞİŞTİRMEDEN ölçebilmesi gerekir.
    /// </param>
    /// <returns>Eklenen (ya da eklenecek) kayıtlar, "TÜR konak" biçiminde; boş liste = zaten güncel.</returns>
    Task<IReadOnlyList<string>> RepairDefaultsAsync(string siteName, bool applyChanges = true, CancellationToken cancellationToken = default);

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

    /// <summary>
    /// Windows DNS Server'ın 53 portunda FİİLEN hangi adreslerde dinlediğini ölçer. Servis
    /// "Running" olsa bile genel IP'ye bind olmamış olabilir (reboot yarış durumu) — o durumda
    /// alan adları dışarıdan hiç çözülmez. <see cref="DnsServerHealthDto.NeedsRepair"/> bunu bildirir.
    /// </summary>
    Task<DnsServerHealthDto> GetServerHealthAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// DNS Server servisini yeniden başlatır (genel IP'ye yeniden bind olması için) ve istenirse
    /// başlangıç türünü gecikmeli otomatiğe alır — reboot'ta ağ arayüzü hazır olmadan başlayıp
    /// yalnız loopback'e bind kalma yarışını önler. Yeniden ölçülmüş sağlık durumunu döner.
    /// </summary>
    Task<DnsServerHealthDto> RepairServerAsync(bool applyDelayedAutoStart, CancellationToken cancellationToken = default);

    /// <summary>
    /// Otomatik nöbetçi: DNS genel IP'de dinlemiyorsa (ve panelde bölge varsa) servisi kendiliğinden
    /// yeniden başlatır. Arka plan otomasyonu tarafından periyodik çağrılır; kendi soğuma süresini
    /// (bir saat) uygular ki bozuk bir kurulumda sonsuz yeniden başlatma döngüsü oluşmasın.
    /// Ölçüm aralığı (beş dakika) henüz dolmadıysa hiçbir şey yapmadan <c>null</c> döner.
    /// </summary>
    Task<DnsServerHealthDto?> RunBindWatchdogAsync(CancellationToken cancellationToken = default);

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
