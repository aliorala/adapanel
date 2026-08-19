namespace AdaPanel.Core.Dtos.Seo;

/// <summary><c>sitemap.xml</c> denetiminin sonucu.</summary>
public sealed class SeoSitemapDto
{
    /// <summary>Denetlenen sitemap yolu (<c>/sitemap.xml</c> ya da robots.txt'in bildirdiği yol).</summary>
    public string Path { get; init; } = string.Empty;

    public bool Found { get; init; }

    public int StatusCode { get; init; }

    /// <summary>Sitemap içindeki toplam <c>&lt;loc&gt;</c> sayısı.</summary>
    public int UrlCount { get; init; }

    /// <summary>Gerçekten açılıp açılmadığı DENENEN adres sayısı (hepsi değil — kaynak sınırı).</summary>
    public int CheckedCount { get; init; }

    /// <summary>Denenip açılamayan adresler (yol + HTTP kodu).</summary>
    public IReadOnlyCollection<string> UnreachableUrls { get; init; } = [];

    /// <summary>
    /// Sitemap'te bu siteye AİT OLMAYAN adresler. Denenmezler (bkz. <c>SeoLinkPolicy</c>): sitemap
    /// içeriğini site sahibi yazar, panelin oradan keyfi adrese istek atması SSRF olurdu.
    /// </summary>
    public IReadOnlyCollection<string> ExternalUrls { get; init; } = [];

    public string Error { get; init; } = string.Empty;
}
