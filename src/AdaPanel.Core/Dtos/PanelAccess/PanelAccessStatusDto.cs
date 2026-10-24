namespace AdaPanel.Core.Dtos.PanelAccess;

/// <summary>
/// Panelin KENDİ alan adıyla dışarıya açılma durumu.
/// <para>
/// Panel varsayılan olarak kendinden imzalı bir sertifikayla çalışır. Tarayıcı bunu "devam et"
/// ile geçebilir; yapay zeka istemcileri (MCP üzerinden bağlananlar) GEÇEMEZ — bağlantı sessizce
/// reddedilir. Bu yüzden panelin geçerli (Let's Encrypt) bir sertifikayla, yöneticinin kendi
/// belirlediği bir alan adı üzerinden yayınlanması gerekir.
/// </para>
/// </summary>
public sealed class PanelAccessStatusDto
{
    /// <summary>Yöneticinin belirlediği panel alan adı. BOŞ = özellik pasif (hiçbir yerde varsayılan alan adı yoktur).</summary>
    public string HostName { get; set; } = string.Empty;

    /// <summary>Alan adı ayarlı mı. false ise diğer alanlar anlamsızdır, arayüz yalnız kurulum formunu gösterir.</summary>
    public bool IsConfigured { get; set; }

    /// <summary>Kurulum tamamsa panelin dış adresi (ör. https://&lt;alan adı&gt;/). Aksi halde boş.</summary>
    public string PanelUrl { get; set; } = string.Empty;

    /// <summary>Yapay zeka istemcisine verilecek MCP uç noktası (panel adresi + /mcp). Aksi halde boş.</summary>
    public string McpEndpoint { get; set; } = string.Empty;

    /// <summary>Ters vekil sitesinin IIS'teki adı (panel tarafından yönetilen sabit ad).</summary>
    public string SiteName { get; set; } = string.Empty;

    /// <summary>Ters vekilin ilettiği yerel panel adresi (ör. https://localhost:8890).</summary>
    public string BackendUrl { get; set; } = string.Empty;

    /// <summary>443'te FİİLEN sunulan sertifikanın kaynağı: letsencrypt | manual | self-signed | none.</summary>
    public string CertificateSource { get; set; } = "none";

    /// <summary>Sunulan sertifika kendinden imzalı mı — yapay zeka istemcilerinin reddettiği durum.</summary>
    public bool CertificateIsSelfSigned { get; set; }

    /// <summary>Sertifikanın bitişine kalan gün (ölçülemediyse null). Negatif = süresi dolmuş.</summary>
    public int? CertificateDaysLeft { get; set; }

    /// <summary>Adımların birleşik sonucu: ok | warn | fail | unknown.</summary>
    public string OverallStatus { get; set; } = "unknown";

    public DateTime CheckedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Sırayla: dns, site, certificate, proxy, renewal, external.</summary>
    public IReadOnlyList<PanelAccessStepDto> Steps { get; set; } = [];
}
