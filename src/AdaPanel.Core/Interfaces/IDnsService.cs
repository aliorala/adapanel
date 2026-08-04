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

    /// <summary>
    /// Windows DNS'te GERÇEKTEN yayınlanmış kayıtların "TÜR|konak" kimliklerini döner.
    /// <para>
    /// Neden panelin kendi dosyası yetmiyor: panel kaydı önce durum dosyasına yazar, sonra Windows
    /// DNS'e basar. Basma başarısız olursa dosyada kayıt VARDIR ama bölgede YOKTUR. Denetim dosyaya
    /// bakarsa "eksik yok" der ve panel, kayıt yayınlanmamışken susar — kullanıcı sorunu ancak
    /// postaları spam'e düşmeye başlayınca fark eder. Doğruyu söylemek için kaynağa sormak gerekir.
    /// </para>
    /// <para>Windows DNS yoksa ya da bölge okunamıyorsa null döner (ölçemedik ≠ eksik yok).</para>
    /// </summary>
    Task<IReadOnlyCollection<string>?> GetPublishedRecordKeysAsync(string siteName, CancellationToken cancellationToken = default);

    Task<SiteDnsZoneDto> UpsertRecordAsync(string siteName, UpsertDnsRecordRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Birden çok kaydı TEK seferde yazar ve bölgeyi BİR KEZ senkronize eder.
    /// <para>
    /// Neden gerekli: kayıtları tek tek yazmak her seferinde bölgenin tamamını yeniden yazan bir
    /// senkron başlatır. Bu hem çok yavaştır (8 posta kaydı = 8 tam senkron) hem de bölgeyi
    /// kilitler; peş peşe gelen senkronlar birbirinin kilidine çarpar ve kayıtlar "zaten var,
    /// silinemedi, eklenemedi" durumunda kalır. Canlıda tam olarak bu oldu.
    /// </para>
    /// </summary>
    Task<SiteDnsZoneDto> UpsertRecordsAsync(string siteName, IReadOnlyList<UpsertDnsRecordRequest> requests, CancellationToken cancellationToken = default);

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
