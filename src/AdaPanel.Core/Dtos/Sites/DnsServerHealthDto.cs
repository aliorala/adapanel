namespace AdaPanel.Core.Dtos.Sites;

/// <summary>
/// Windows DNS Server'ın SERVİS DURUMU değil, FİİLEN DIŞARIYA YANIT VEREBİLİRLİĞİ.
/// Gerçek olay (2026-07-23 reboot): servis "Running" görünüyordu, yapılandırmada genel IP
/// dinleme listesindeydi, ama ağ arayüzü hazır olmadan başladığı için yalnız 127.0.0.1/::1
/// üzerinde bind kalmıştı. Sonuç: 6 gün boyunca internetten hiçbir DNS sorgusu yanıtlanmadı
/// (siteler açılmadı, Let's Encrypt DNS-01 doğrulaması zaman aşımına uğradı). Servis durumuna
/// bakan bir kontrol bunu ASLA yakalayamaz; bu yüzden 53'ün fiili dinleyicileri ölçülür.
/// </summary>
public sealed class DnsServerHealthDto
{
    /// <summary>Windows DNS Server rolü kurulu mu (DnsServer modülü mevcut mu).</summary>
    public bool Installed { get; init; }

    /// <summary>Running / Stopped / NotInstalled / Unknown.</summary>
    public string ServiceStatus { get; init; } = "Unknown";

    /// <summary>Auto / Manual / Disabled / Unknown (Win32_Service StartMode).</summary>
    public string StartMode { get; init; } = "Unknown";

    /// <summary>Gecikmeli otomatik başlangıç açık mı (reboot yarış durumuna karşı).</summary>
    public bool DelayedAutoStart { get; init; }

    /// <summary>53 portunda FİİLEN dinlenen adresler (DNS sürecine ait).</summary>
    public string[] ListeningAddresses { get; init; } = [];

    /// <summary>Dinlenmesi beklenen genel adresler (sunucunun loopback olmayan IPv4'leri + panel sunucu adresi).</summary>
    public string[] ExpectedAddresses { get; init; } = [];

    /// <summary>Beklenip de dinlenmeyen adresler.</summary>
    public string[] MissingAddresses { get; init; } = [];

    /// <summary>En az bir genel adreste dinliyor mu (0.0.0.0 tümünü kapsar).</summary>
    public bool PubliclyBound { get; init; }

    /// <summary>Panelde barındırılan birincil bölge sayısı (0 ise DNS zaten kullanılmıyor demektir).</summary>
    public int ZoneCount { get; init; }

    /// <summary>Sorun var mı: rol kurulu + bölge var, ama genel adreste dinlemiyor.</summary>
    public bool NeedsRepair => Installed && ZoneCount > 0 && !PubliclyBound;

    /// <summary>Otomatik nöbetçinin son onarım zamanı (hiç onarmadıysa null).</summary>
    public DateTime? LastAutoRepairUtc { get; init; }

    /// <summary>Otomatik nöbetçinin son onarım sonucu (ok / failed + kısa açıklama).</summary>
    public string LastAutoRepairStatus { get; init; } = string.Empty;

    /// <summary>Teşhis/ham çıktı notları (UI ayrıntı satırı).</summary>
    public string Details { get; init; } = string.Empty;
}
