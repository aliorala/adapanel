namespace AdaPanel.Core.Dtos.Licensing;

/// <summary>
/// Lisansın panele ve arayüze gösterilen tam durumu.
/// </summary>
/// <remarks>
/// Anahtarın KENDİSİ burada ASLA yer almaz; yalnızca son dört karakteri (<see cref="KeySuffix"/>)
/// döner. Aksi hâlde ekranı gören/loglayan herkes anahtarı kopyalayabilirdi.
/// </remarks>
public sealed class LicenseStatusDto
{
    /// <summary>Kararın kendisi.</summary>
    public LicenseState State { get; init; } = LicenseState.NotConfigured;

    /// <summary>Arayüzün çevireceği mesaj anahtarı (<c>license.*</c>).</summary>
    public string MessageKey { get; init; } = string.Empty;

    /// <summary>
    /// Lisanslama bu kurulumda AÇIK mı? (doğrulama ucu yapılandırıldıysa true).
    /// false ise panel hiçbir yere istek atmaz ve hiçbir şey kısıtlanmaz.
    /// </summary>
    public bool IsConfigured { get; init; }

    /// <summary>YENİ kaynak oluşturma kısıtlı mı? Mevcut kaynaklar her hâlükârda çalışmaya devam eder.</summary>
    public bool RestrictsNewResources { get; init; }

    /// <summary>Panelde saklı bir anahtar var mı?</summary>
    public bool HasKey { get; init; }

    /// <summary>Anahtarın yalnız son dört karakteri (gösterim için); yoksa boş.</summary>
    public string KeySuffix { get; init; } = string.Empty;

    /// <summary>Sunucunun bildirdiği ürün kademesi (SKU); bilinmiyorsa boş.</summary>
    public string Sku { get; init; } = string.Empty;

    /// <summary>SKU'nun izin verdiği en fazla abonelik sayısı (0 = sınırsız).</summary>
    public int MaxSubscriptions { get; init; }

    /// <summary>Lisansın bitiş tarihi (UTC); süresizse null.</summary>
    public DateTime? ExpiresAtUtc { get; init; }

    /// <summary>Doğrulama sunucusuyla en son BAŞARILI temas (UTC); hiç olmadıysa null.</summary>
    public DateTime? LastVerifiedAtUtc { get; init; }

    /// <summary>Sunucuya ulaşılamazsa panelin kısıtsız çalışmayı sürdüreceği son an (UTC).</summary>
    public DateTime? OfflineGraceEndsAtUtc { get; init; }

    /// <summary>Bu makinenin kararlı parmak izi (SHA-256, 64 karakter onaltılık).</summary>
    public string Fingerprint { get; init; } = string.Empty;

    /// <summary>Parmak izinin hangi kaynaklardan türetildiği (şeffaflık + destek teşhisi için).</summary>
    public string FingerprintSource { get; init; } = string.Empty;

    /// <summary>Son doğrulama denemesindeki hata (varsa); teşhis için, çeviri anahtarı DEĞİL.</summary>
    public string LastError { get; init; } = string.Empty;
}
