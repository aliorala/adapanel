using AdaPanel.Core.Dtos.Licensing;

namespace AdaPanel.Core.Interfaces;

/// <summary>
/// Panelin lisans/aktivasyon durumunu yönetir ve YENİ kaynak açma kapısını tutar.
/// </summary>
/// <remarks>
/// <para>
/// <b>Varsayılan: KAPALI.</b> Doğrulama ucu yapılandırılmadıkça (<c>Panel:License:VerificationUrl</c>
/// boş) bu servis hiçbir yere istek atmaz, hiçbir şeyi kısıtlamaz ve panel bugünkü gibi çalışır.
/// Lisanslamayı "sessizce açık" varsaymak, kendi sunucusuna kurup kullanan herkesin panelini bir
/// ağ hatasında kilitlerdi.
/// </para>
/// <para>
/// <b>Çevrimdışı tolerans.</b> Doğrulama sunucusuna ulaşılamaması bir hata değil, bir BEKLENTİDİR:
/// sunucu bakımdadır, DNS bozulmuştur, güvenlik duvarı çıkışı kesmiştir. Panel bu durumda
/// yapılandırılan gün sayısı boyunca (varsayılan 14) kısıtsız çalışmaya devam eder. Ağ hatası
/// müşteriyi kendi sunucusundan etmemelidir.
/// </para>
/// <para>
/// <b>Kısıtlama YALNIZCA yeni kaynak açmayı durdurur.</b> Lisans geçersiz ya da süresi dolmuş olsa
/// bile mevcut siteler, posta kutuları ve veritabanları çalışmaya devam eder; yedek alınabilir,
/// geri yüklenebilir, dosyalara erişilebilir, müşteri panele girebilir. Üretimdeki siteleri kapatan
/// bir lisans mekanizması barındırma paneli değil fidye aracı olurdu.
/// </para>
/// </remarks>
public interface ILicenseService
{
    /// <summary>
    /// Güncel durumu döner. Doğrulama aralığı dolduysa arka planda YENİDEN doğrulamayı dener; bu
    /// deneme başarısız olursa durum çevrimdışı toleransa göre hesaplanır (çağrı PATLAMAZ).
    /// </summary>
    Task<LicenseStatusDto> GetStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Anahtarı panele tanıtır (DPAPI korumalı saklar) ve hemen doğrulamayı dener.
    /// </summary>
    /// <remarks>
    /// Doğrulama sunucusuna ULAŞILAMASA bile anahtar saklanır ve çevrimdışı tolerans o andan
    /// itibaren işlemeye başlar: kurulum anında ağ yoksa müşteri lisansını giremez hâle gelmemeli.
    /// Sunucu anahtarı açıkça REDDEDERSE anahtar saklanmaz.
    /// </remarks>
    Task<LicenseStatusDto> ActivateAsync(string licenseKey, CancellationToken cancellationToken = default);

    /// <summary>Saklı anahtarı ve önbelleklenmiş kararı siler (makine değiştirme / iade akışı).</summary>
    Task<LicenseStatusDto> DeactivateAsync(CancellationToken cancellationToken = default);

    /// <summary>Doğrulama aralığını beklemeden sunucuya sorar.</summary>
    Task<LicenseStatusDto> RefreshAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Bu makinenin kararlı parmak izi. Lisans sunucusuna gönderilen tek makine kimliğidir.
    /// </summary>
    string GetFingerprint();

    /// <summary>
    /// YENİ kaynak açılabilir mi? Açılamazsa NEDEN açılamadığını söyleyen
    /// <see cref="Core.Common.AppException"/> fırlatır (mesaj anahtarı <c>license.*</c>).
    /// </summary>
    /// <param name="resource">Açılmak istenen kaynak türü.</param>
    /// <param name="existingCount">
    /// SKU limiti olan kaynaklarda (şu an yalnız abonelik) MEVCUT sayı. Limiti olmayan kaynaklarda
    /// yok sayılır.
    /// </param>
    Task EnsureCanCreateAsync(LicensedResource resource, int existingCount = 0, CancellationToken cancellationToken = default);
}
