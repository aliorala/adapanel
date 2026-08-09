namespace AdaPanel.Core.Dtos.Ssl;

/// <summary>Bir sitenin HTTPS binding'ini (ve panel kaydını) kaldırır; sonradan yeniden kurulabilir.</summary>
public sealed class RemoveSslCertificateRequest
{
    public string SiteName { get; set; } = string.Empty;

    /// <summary>
    /// Kaldırılacak HTTPS bağlamasının host başlığı. Boş bırakılırsa YALNIZCA sitede o portta tek
    /// bir HTTPS bağlaması varsa o seçilir; birden fazlaysa istek <c>ssl.host_name_required</c> ile
    /// reddedilir (yanlış alan adının HTTPS'ini kapatmak geri alınması zor bir hatadır).
    /// </summary>
    public string HostName { get; set; } = string.Empty;

    public int Port { get; set; } = 443;

    /// <summary>
    /// Sertifika Windows sertifika deposundan da SİLİNSİN mi (varsayılan: hayır).
    /// <para>
    /// Varsayılanın "hayır" olmasının sebebi: bağlamayı kaldırmak geri alınabilir bir işlemdir
    /// (sertifika depoda durur, tek tıkla yeniden bağlanır), sertifikayı silmek ise değildir —
    /// özel anahtar gider ve Let's Encrypt'te yeniden alım haftalık limite takılabilir.
    /// </para>
    /// <para>
    /// <b>true</b> verilse bile silme YALNIZCA ölçümle güvenli bulunursa yapılır: aynı sertifikayı
    /// kullanan başka bir IIS bağlaması varsa sertifikaya DOKUNULMAZ.
    /// </para>
    /// </summary>
    public bool RemoveCertificateFromStore { get; set; }
}
