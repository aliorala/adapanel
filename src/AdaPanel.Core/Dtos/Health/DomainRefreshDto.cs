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

    /// <summary>Bu alan adında bulunan sorunlar: düzeltilenler, onay bekleyenler ve bildirilenler.</summary>
    public IReadOnlyList<RepairFindingDto> Findings { get; set; } = [];

    /// <summary>Bu alan adı için işlem yapılamadıysa sebebi; boşsa sorun yok.</summary>
    public string Error { get; set; } = string.Empty;

    /// <summary>Hiçbir eksik bulunmadıysa true — "zaten güncel" demenin dürüst yolu.</summary>
    public bool AlreadyCurrent => Error.Length == 0
        && AddedDnsRecords.Count == 0
        && AppliedMailRecords == 0
        && !MailCertificateRefreshed
        && Findings.Count == 0;
}


/// <summary>
/// Onarmanın tek bir bulgusu.
/// <para>
/// Neden ayrı bir liste: onarma artık üç sınıf iş yapıyor — kendiliğinden düzelttikleri,
/// düzeltebileceği ama ONAY beklediği, ve yalnız bildirdikleri. Hepsini tek bir "güncellendi"
/// sayısına indirmek, kullanıcının hangi işi kendisinin yapması gerektiğini gizlerdi.
/// </para>
/// </summary>
public sealed class RepairFindingDto
{
    /// <summary>Çeviri anahtarı (ör. "repair.site_stopped"). Metin arayüzde üretilir.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>"warn" (çalışıyor ama eksik) veya "fail" (bozuk).</summary>
    public string Severity { get; set; } = "warn";

    /// <summary>Onarma bunu GERÇEKTEN düzelttiyse true.</summary>
    public bool Repaired { get; set; }

    /// <summary>
    /// Panel bunu düzeltebilir ama KENDİLİĞİNDEN yapmaz; kullanıcı onayı ister.
    /// <para>
    /// Örnek: durmuş bir siteyi başlatmak. Kullanıcı siteyi bilerek durdurmuş olabilir; onarma
    /// bunu sessizce geri almamalı. Aynı şekilde servis yeniden başlatmak açık oturumları koparır.
    /// </para>
    /// </summary>
    public bool NeedsConfirmation { get; set; }

    /// <summary>Teknik ayrıntı (hangi port, hangi tarih, hangi hata).</summary>
    public string Detail { get; set; } = string.Empty;
}

/// <summary>Sunucudaki TÜM alan adları için tazeleme sonucu.</summary>
public sealed class DomainRefreshSummaryDto
{
    public IReadOnlyList<DomainRefreshResultDto> Results { get; set; } = [];

    public int TotalSites { get; set; }

    public int UpdatedSites { get; set; }

    public int FailedSites { get; set; }

    /// <summary>
    /// Tek bir alan adına değil SUNUCUYA ait bulgular (güvenlik duvarı, servisler).
    /// <para>
    /// Ayrı tutuluyor çünkü site başına tekrarlanmaları anlamsız olurdu: kapalı bir güvenlik
    /// duvarı portu 40 sitede 40 kez raporlanırsa rapor okunamaz hâle gelir.
    /// </para>
    /// </summary>
    public IReadOnlyList<RepairFindingDto> ServerFindings { get; set; } = [];
}
