namespace AdaPanel.Core.Dtos.Sites;

/// <summary>
/// Domain yönlendirmeden siteyi test etme (Plesk "Preview" gibi): nip.io tabanlı tıklanır önizleme linki
/// + her durumda çalışan hosts dosyası alternatifi.
/// </summary>
public sealed class SitePreviewDto
{
    /// <summary>Tıklanır önizleme URL'i (nip.io ise domainsiz çalışır; sunucu adresi IP değilse gerçek host'a düşer).</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Önizleme host adı (nip.io) — eklenen IIS bağlaması bununla eşleşir.</summary>
    public string PreviewHost { get; set; } = string.Empty;

    /// <summary>"nipio" (tıklanır link hazır) veya "hosts" (sunucu adresi IP değil → yalnız hosts yöntemi).</summary>
    public string Method { get; set; } = string.Empty;

    /// <summary>Sitenin gerçek host adı (asıl domain).</summary>
    public string RealHost { get; set; } = string.Empty;

    /// <summary>Sunucu adresi (IP).</summary>
    public string ServerAddress { get; set; } = string.Empty;

    /// <summary>Elle test için hosts dosyasına eklenecek satır (her yöntemde çalışır, HTTPS dahil).</summary>
    public string HostsLine { get; set; } = string.Empty;

    /// <summary>Bu çağrıda önizleme bağlaması eklendi mi (zaten varsa false).</summary>
    public bool BindingAdded { get; set; }
}
