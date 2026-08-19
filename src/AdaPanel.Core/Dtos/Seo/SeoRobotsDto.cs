namespace AdaPanel.Core.Dtos.Seo;

/// <summary><c>robots.txt</c> denetiminin sonucu.</summary>
public sealed class SeoRobotsDto
{
    /// <summary>Dosya 200 ile döndü mü.</summary>
    public bool Found { get; init; }

    public int StatusCode { get; init; }

    /// <summary>
    /// Tüm robotlara kök yasağı (<c>User-agent: *</c> + <c>Disallow: /</c>) var mı — site arama
    /// motorlarına KAPALI demektir.
    /// </summary>
    public bool DisallowsAll { get; init; }

    /// <summary><c>Sitemap:</c> satırlarında bildirilen adresler (ham hâliyle).</summary>
    public IReadOnlyCollection<string> SitemapUrls { get; init; } = [];

    /// <summary>Okunamadıysa sebebi (dile bağımsız).</summary>
    public string Error { get; init; } = string.Empty;
}
