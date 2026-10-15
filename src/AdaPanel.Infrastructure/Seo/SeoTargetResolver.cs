using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Common;

namespace AdaPanel.Infrastructure.Seo;

/// <param name="Target">Taranabilecek hedef; çözülemediyse null.</param>
/// <param name="Reason">Çözülemediyse sebebin anahtarı (<c>no_host</c>, <c>no_web_binding</c>).</param>
public readonly record struct SeoTargetResolution(SeoCrawlTarget? Target, string Reason);

/// <summary>
/// Bir IIS sitesinden tarama hedefini türetir. SAF: yalnız site tanımına bakar.
/// </summary>
/// <remarks>
/// <para>
/// <b>Bu, SSRF korumasının ilk halkasıdır:</b> taranacak konak adı YALNIZCA sitenin kendi IIS
/// bağlamalarından gelir. Kullanıcının gönderdiği hiçbir alan (istek gövdesi, sorgu dizesi) hedefi
/// belirlemez. Site adı ise denetleyicide <c>CustomerAccess.EnsureSiteAsync</c> ile kapsam
/// doğrulamasından geçer — yani kiracı, yalnız kendi sitesinin bağlamalarını taratabilir.
/// </para>
/// <para>
/// <b>Neden https tercih edilir:</b> arama motorları da öyle yapar ve site http'yi genellikle
/// https'e yönlendirir. http'den başlamak her sayfada bir yönlendirme atlaması ekler ve rapor
/// gerçekte var olmayan "yönlendirme zinciri" bulgularıyla dolar.
/// </para>
/// </remarks>
public static class SeoTargetResolver
{
    public static SeoTargetResolution Resolve(SiteDto site)
    {
        var bindings = site.Bindings ?? [];

        // Konak kümesi TÜM bağlamalardan toplanır (port farkı gözetmeksizin): site kendi adına
        // 8080'den de bağlanmış olabilir ve o adres yine SİTENİN KENDİSİDİR, dış bağlantı değil.
        var allowedHosts = SeoLinkPolicy.BuildAllowedHosts(bindings.Select(binding => binding.HostName));
        if (allowedHosts.Count == 0)
        {
            // Konak adı olmayan bir bağlama (yalnız IP/port) taranamaz: Host başlığı olmadan IIS
            // hangi siteyi döneceğini bilemez, ölçüm başka bir sitenin sayfasını ölçebilirdi.
            return new SeoTargetResolution(null, "no_host");
        }

        var primary = SiteDomainSelection.WebDomain(site);
        var secildi = bindings.Where(binding => binding.Port is 80 or 443 && !string.IsNullOrWhiteSpace(binding.HostName))
            .OrderBy(binding => binding.HostName.Trim().TrimEnd('.').Equals(primary, StringComparison.OrdinalIgnoreCase) ? 0
                : binding.HostName.Trim().TrimEnd('.').Equals("www." + primary, StringComparison.OrdinalIgnoreCase) ? 1
                : SiteDomainSelection.IsRealDomain(binding.HostName.Trim().TrimEnd('.')) ? 2 : 3)
            .ThenBy(binding => binding.Port == 443 ? 0 : 1).FirstOrDefault();
        if (secildi is null)
        {
            // Yalnız standart olmayan portta yayındaki site taranmaz (bkz. SeoLinkPolicy.IsAllowedPort).
            return new SeoTargetResolution(null, "no_web_binding");
        }

        var host = secildi.HostName.Trim().TrimEnd('.').ToLowerInvariant();
        return new SeoTargetResolution(
            new SeoCrawlTarget
            {
                Host = host,
                Port = secildi.Port,
                UseTls = secildi.Port == 443,
                AllowedHosts = allowedHosts
            },
            string.Empty);
    }
}
