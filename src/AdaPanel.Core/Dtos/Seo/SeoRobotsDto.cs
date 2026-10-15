namespace AdaPanel.Core.Dtos.Seo;

/// <summary><c>robots.txt</c> denetiminin sonucu.</summary>
public sealed class SeoRobotsDto
{
    public IReadOnlyList<SeoBotAccessDto> BotAccess { get; init; } = [];

    /// <summary>Dosya 200 ile döndü mü.</summary>
    public bool Found { get; init; }

    public int StatusCode { get; init; }

    /// <summary>
    /// Wildcard group's root restriction. Agent-specific overrides are reported in BotAccess.
    /// </summary>
    public bool DisallowsAll { get; init; }

    /// <summary><c>Sitemap:</c> satırlarında bildirilen adresler (ham hâliyle).</summary>
    public IReadOnlyCollection<string> SitemapUrls { get; init; } = [];

    /// <summary>Okunamadıysa sebebi (dile bağımsız).</summary>
    public string Error { get; init; } = string.Empty;
}
