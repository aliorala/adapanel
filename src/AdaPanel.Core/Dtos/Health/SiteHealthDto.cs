namespace AdaPanel.Core.Dtos.Health;

/// <summary>
/// Tek bir dış-gözlem kontrolünün sonucu. Panelin "servis çalışıyor" bilgisiyle DEĞİL,
/// dışarıdan gözlenen davranışla doldurulur.
/// </summary>
public sealed class SiteHealthCheckDto
{
    /// <summary>dnsResolves | dnsMatchesServer | http | https | mail | mail.mx | mail.spf | mail.dkim | mail.dmarc</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>ok | warn | fail | unknown — "unknown" ÖLÇEMEDİK demektir, "sorun yok" DEMEK DEĞİLDİR.</summary>
    public string Status { get; set; } = "unknown";

    /// <summary>Rozette gösterilecek kısa değer (çözülen IP, "HTTP 200", "42 gün", "yok").</summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>Dile bağımsız teknik ayrıntı (hata metni, sorgulanan çözümleyici, ham kayıt).</summary>
    public string Detail { get; set; } = string.Empty;
}

/// <summary>
/// Bir sitenin DIŞARIDAN görünen sağlığı.
/// <para>
/// Tasarımın sebebi (2026-07-23 gerçek olayı): DNS servisi reboot sonrası yalnız loopback'e bind
/// kalmıştı; panel her şeyi yeşil gösteriyordu (servis Running, site Started, sertifika geçerli) ama
/// dünya alan adını çözemiyordu ve bu ALTI GÜN fark edilmedi. Bu yüzden buradaki her kontrol
/// sunucunun kendi iç durumunu değil, dışarıdan gözlenen sonucu ölçer.
/// </para>
/// </summary>
public sealed class SiteHealthDto
{
    public string SiteName { get; set; } = string.Empty;

    /// <summary>Ölçülen alan adı (IIS binding'inden çözülür). Boşsa site'ta alan adı yok demektir.</summary>
    public string Domain { get; set; } = string.Empty;

    /// <summary>Panelin bildiği sunucu adresi — dnsMatchesServer bununla karşılaştırır.</summary>
    public string ServerAddress { get; set; } = string.Empty;

    /// <summary>Tüm kontrollerin birleşimi: herhangi biri fail ise fail, yoksa warn, hepsi ölçülemediyse unknown.</summary>
    public string OverallStatus { get; set; } = "unknown";

    public DateTime CheckedAtUtc { get; set; }

    /// <summary>Ölçümün toplam süresi (ms) — yavaşlayan bir kontrolü fark etmek için.</summary>
    public long DurationMs { get; set; }

    /// <summary>Alan adı halka açık çözümleyicilerden (1.1.1.1 / 8.8.8.8) çözülüyor mu.</summary>
    public SiteHealthCheckDto DnsResolves { get; set; } = new() { Key = "dnsResolves" };

    /// <summary>Çözülen IP bu sunucunun adresi mi (başka sunucuya bakıyorsa site burada değil).</summary>
    public SiteHealthCheckDto DnsMatchesServer { get; set; } = new() { Key = "dnsMatchesServer" };

    /// <summary>Sitenin HTTP yanıtı — DNS'e GÜVENMEDEN, sunucunun IP'sine Host başlığıyla ölçülür.</summary>
    public SiteHealthCheckDto Http { get; set; } = new() { Key = "http" };

    /// <summary>443'te sunulan sertifika: geçerli mi, alan adıyla eşleşiyor mu, ne kadar kaldı.</summary>
    public SiteHealthCheckDto Https { get; set; } = new() { Key = "https" };

    /// <summary>Sertifikanın bitişine kalan gün (ölçülemediyse null). Negatif = süresi dolmuş.</summary>
    public int? CertificateDaysLeft { get; set; }

    /// <summary>MX/SPF/DKIM/DMARC kayıtlarının toplu durumu.</summary>
    public SiteHealthCheckDto Mail { get; set; } = new() { Key = "mail" };

    /// <summary>Mail kayıtlarının tek tek durumu (mail.mx, mail.spf, mail.dkim, mail.dmarc).</summary>
    public IReadOnlyList<SiteHealthCheckDto> MailRecords { get; set; } = [];
}
