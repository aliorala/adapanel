using System.ComponentModel;
using System.Security.Claims;
using AdaPanel.Api.Security;
using AdaPanel.Core.Interfaces;
using ModelContextProtocol.Server;

namespace AdaPanel.Api.Mcp;

/// <summary>
/// Yapay zeka istemcisine (Claude vb.) açılan SALT-OKUR araçlar.
/// <para>
/// Bu sınıfa YALNIZCA okuma arayüzleri enjekte edilir. <c>ITerminalService</c>, <c>IFileService</c>,
/// <c>IDatabaseService</c>, <c>ISiteCascadeDeletionService</c> gibi yıkıcı yetenekler bilerek DIŞARIDA
/// bırakılmıştır: bağımlılık grafiğinde bağ yoksa çağrı da olamaz. Bu, "prompt'ta yasakladık"tan
/// çok daha güçlü bir garantidir ve kod incelemesinde tek bakışta doğrulanır.
/// </para>
/// <para>
/// Yıkıcı araçlar ayrı bir sınıfta olacak ve anahtarın <c>AllowDestructive</c> yetkisini arayacak.
/// </para>
/// </summary>
[McpServerToolType]
public sealed class AdaPanelReadTools(
    ICustomerScopeService customerScopes,
    IIisSiteService siteService,
    ISiteHealthService siteHealthService,
    ISystemMonitoringService systemMonitoringService,
    IHealthService healthService,
    IAdvisorService advisorService,
    IDnsService dnsService)
{
    [McpServerTool(Name = "sites_list")]
    [Description("Sunucudaki web sitelerini listeler: ad, durum, uygulama havuzu, fiziksel yol ve bağlamalar.")]
    public async Task<object> ListSitesAsync(ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        var sites = await siteService.GetSitesAsync(cancellationToken);

        // KAPSAM FİLTRESİ: SitesController bunu yapıyor, buranın da yapması ŞART. Yapılmazsa bir
        // müşteri anahtarı sunucudaki BÜTÜN siteleri görürdü — başka müşterilerinkiler dahil.
        var allowed = await CustomerAccess.GetEffectiveSitesAsync(customerScopes, user, cancellationToken);
        if (allowed is not null)
        {
            sites = sites.Where(site => allowed.Contains(site.Name, StringComparer.OrdinalIgnoreCase)).ToArray();
        }

        return sites.Select(site => new
        {
            site.Name,
            site.State,
            site.ApplicationPoolName,
            site.PhysicalPath,
            Bindings = site.Bindings.Select(binding => $"{binding.Protocol}://{binding.HostName}:{binding.Port}")
        });
    }

    [McpServerTool(Name = "site_health")]
    [Description("Bir sitenin DIŞARIDAN görünen sağlığı: DNS çözümlemesi, DNS'in sunucuyu gösterip göstermediği, HTTP yanıtı, TLS sertifikası ve kalan gün, posta kayıtları (MX/SPF/DKIM/DMARC).")]
    public async Task<object> GetSiteHealthAsync(
        [Description("Site adı, örneğin ornek.com")] string siteName,
        ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, user, siteName, cancellationToken);
        return await siteHealthService.GetSiteHealthAsync(siteName, cancellationToken);
    }

    [McpServerTool(Name = "system_overview")]
    [Description("Sunucunun anlık durumu: CPU, bellek, disk kullanımı ve çalışma süresi.")]
    public async Task<object> GetSystemOverviewAsync(CancellationToken cancellationToken)
    {
        return await systemMonitoringService.GetOverviewAsync(cancellationToken);
    }

    [McpServerTool(Name = "server_health")]
    [Description("Kritik Windows servislerinin durumu (IIS, DNS, veritabanı, posta vb.).")]
    public async Task<object> GetServerHealthAsync(ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        McpCapability.RequireAdministrator(user);
        return await healthService.GetHealthAsync(cancellationToken);
    }

    [McpServerTool(Name = "advisor_report")]
    [Description("Panelin kendi öneri raporu: SSL bitişi, disk/bellek doluluğu, güvenlik ayarları, bekleyen güncellemeler. Çalıştırılamayan kontroller ayrıca bildirilir.")]
    public async Task<object> GetAdvisorReportAsync(ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        McpCapability.RequireAdministrator(user);
        return await advisorService.GetReportAsync(cancellationToken);
    }

    [McpServerTool(Name = "dns_zone")]
    [Description("Bir sitenin DNS bölgesindeki kayıtları döner (A, MX, TXT/SPF, DKIM, DMARC, NS...).")]
    public async Task<object> GetDnsZoneAsync(
        [Description("Site adı, örneğin ornek.com")] string siteName,
        ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, user, siteName, cancellationToken);
        return await dnsService.GetZoneAsync(siteName, cancellationToken);
    }

    /// <summary>
    /// Bağlantının kimin adına çalıştığını söyler. İlk kurulumda "doğru hesapla mı bağlandım"
    /// sorusunu yanıtlamanın en hızlı yolu; ayrıca anahtarın yetkilerini de gösterir.
    /// </summary>
    [McpServerTool(Name = "whoami")]
    [Description("Bu bağlantının hangi panel kullanıcısı adına çalıştığını ve anahtarın yetkilerini gösterir.")]
    public static object WhoAmI(ClaimsPrincipal user) => new
    {
        UserName = user.Identity?.Name ?? string.Empty,
        Role = user.FindFirst(ClaimTypes.Role)?.Value ?? string.Empty,
        AllowDestructive = user.FindFirst(PanelTokenAuthenticationHandler.AllowDestructiveClaim)?.Value == "true",
        AllowTerminal = user.FindFirst(PanelTokenAuthenticationHandler.AllowTerminalClaim)?.Value == "true"
    };
}
