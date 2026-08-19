namespace AdaPanel.Core.Dtos.Sites;

public sealed class SiteHostingSettingsDto
{
    public string SiteName { get; set; } = string.Empty;

    public long MaximumBandwidthBytesPerSecond { get; set; }

    public long MaximumConnections { get; set; }

    public string[] DeniedIpAddresses { get; set; } = [];

    public string RedirectUrl { get; set; } = string.Empty;

    public bool DirectoryBrowsingEnabled { get; set; }

    public string[] DefaultDocuments { get; set; } = ["index.html", "index.php", "Default.aspx", "default.asp"];

    public bool DetailedErrorsEnabled { get; set; }

    public bool StaticCompressionEnabled { get; set; } = true;

    public bool DynamicCompressionEnabled { get; set; }

    public long MaximumUploadBytes { get; set; } = 31_457_280;

    public bool HotlinkProtectionEnabled { get; set; }

    public string[] AllowedReferrerHosts { get; set; } = [];

    public string[] ProtectedExtensions { get; set; } = [];

    /// <summary>Durum koduna göre sitenin kendi hata belgeleri.</summary>
    public SiteErrorPageDto[] CustomErrorPages { get; set; } = [];

    /// <summary>Site bazlı uzantı → MIME türü eşlemeleri.</summary>
    public SiteMimeTypeDto[] MimeTypes { get; set; } = [];

    public bool HstsEnabled { get; set; }

    /// <summary>
    /// <c>max-age</c> saniyesi. Varsayılan 1 yıl (HSTS preload listesinin alt sınırı).
    /// </summary>
    public int HstsMaxAgeSeconds { get; set; } = 31_536_000;

    public bool HstsIncludeSubDomains { get; set; }

    public bool HstsPreload { get; set; }
}
