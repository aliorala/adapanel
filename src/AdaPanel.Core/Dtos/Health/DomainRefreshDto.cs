namespace AdaPanel.Core.Dtos.Health;

/// <summary>
/// Tek bir alan adının "tazeleme" sonucu: panelin yeni sürümleriyle gelen ve o alan adında EKSİK
/// kalmış ayarlar tamamlandığında ne eklendiğinin dökümü.
/// <para>
/// Neden gerekli: yeni varsayılanlar (posta MX/SPF/DMARC, DKIM anahtarları vb.) yalnızca YENİ
/// oluşturulan alan adlarına uygulanır. Panel güncellendiğinde eskiden eklenmiş alan adları bu
/// ayarlardan habersiz kalır ve fark, sorun çıkana kadar (giden posta spam'e düşene kadar) görünmez.
/// </para>
/// </summary>
public sealed class DomainRefreshResultDto
{
    public string SiteName { get; set; } = string.Empty;

    public string ZoneName { get; set; } = string.Empty;

    /// <summary>Eklenen DNS kayıtları, "TÜR konak" biçiminde (ör. "TXT _dmarc").</summary>
    public IReadOnlyList<string> AddedDnsRecords { get; set; } = [];

    /// <summary>Posta sunucusundan alınıp zone'a yazılan kayıt sayısı (DKIM dahil). 0 = posta alan adı yok.</summary>
    public int AppliedMailRecords { get; set; }

    /// <summary>
    /// Posta sunucusunun TLS sertifikası yeniden gönderildiyse true.
    /// <para>
    /// Neden onarma kapsamında: sertifika Stalwart'a yalnız YENİLENİRKEN gönderilir. Panelin
    /// sertifika birleştirmesinde bir kusur olsaydı (canlıda oldu: ara sertifika düşüyordu), mevcut
    /// sunucular güncelleme sonrası bile bozuk zinciri taşımaya devam ederdi -- ta ki üç ay sonraki
    /// yenilemeye kadar. Onarma bunu beklemez.
    /// </para>
    /// </summary>
    public bool MailCertificateRefreshed { get; set; }

    /// <summary>Bu alan adı için işlem yapılamadıysa sebebi; boşsa sorun yok.</summary>
    public string Error { get; set; } = string.Empty;

    /// <summary>Hiçbir eksik bulunmadıysa true — "zaten güncel" demenin dürüst yolu.</summary>
    public bool AlreadyCurrent => Error.Length == 0 && AddedDnsRecords.Count == 0 && AppliedMailRecords == 0 && !MailCertificateRefreshed;
}

/// <summary>Sunucudaki TÜM alan adları için tazeleme sonucu.</summary>
public sealed class DomainRefreshSummaryDto
{
    public IReadOnlyList<DomainRefreshResultDto> Results { get; set; } = [];

    public int TotalSites { get; set; }

    public int UpdatedSites { get; set; }

    public int FailedSites { get; set; }
}
