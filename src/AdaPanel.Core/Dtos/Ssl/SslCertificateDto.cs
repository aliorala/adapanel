namespace AdaPanel.Core.Dtos.Ssl;

public sealed class SslCertificateDto
{
    public string SiteName { get; set; } = string.Empty;

    public string HostName { get; set; } = string.Empty;

    public string Thumbprint { get; set; } = string.Empty;

    public string FriendlyName { get; set; } = string.Empty;

    public string Subject { get; set; } = string.Empty;

    public DateTime NotAfterUtc { get; set; }

    /// <summary>
    /// "letsencrypt", "self-signed" ya da "manual" (elle içe aktarılmış).
    /// <para>
    /// Sertifikanın KENDİSİNDEN türetilir (veren = konu ise kendinden imzalı). Bir zamanlar
    /// FriendlyName içinde "Let's Encrypt" aranıyordu; o adı win-acme yazar ve biçimi
    /// "[win-acme] example.com @ ..." olduğu için koşul HİÇBİR ZAMAN doğru olmazdı — gerçek bir
    /// Let's Encrypt sertifikası panelde "self-signed" görünüyor, kullanıcı sertifikanın
    /// alınmadığını sanıp tekrar deniyor ve Let's Encrypt'in haftalık limitini yakıyordu.
    /// </para>
    /// </summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>
    /// Sitede gerçekten bir HTTPS bağlaması var mı. <c>null</c> = ÖLÇÜLEMEDİ.
    /// <para>
    /// Eskiden sabit <c>true</c> yazılıyordu, yani hiç ölçülmüyordu. Site sağlığı ekranı 443
    /// el sıkışması başarısızken bile "https binding=True" diyerek kullanıcıyı yanlış yöne
    /// gönderiyordu. "Ölçemedik" ile "var" aynı şey değildir.
    /// </para>
    /// </summary>
    public bool? IsHttpsBindingPresent { get; set; }

    public int Port { get; set; }

    /// <summary>
    /// Let's Encrypt sertifikası alınamadığı için kendinden imzalıya düşüldüyse SEBEBİ.
    /// <para>
    /// Boş değilse: ekranda yeşil bir "SSL kuruldu" satırı gösterilmemeli. Sessiz düşüş,
    /// kullanıcının tarayıcıda "bu bağlantı güvenli değil" uyarısını panelde hiçbir açıklama
    /// olmadan görmesi demekti.
    /// </para>
    /// </summary>
    public string FallbackReason { get; set; } = string.Empty;
}
