using System.Security.Claims;
using AdaPanel.Api.Controllers;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Dtos.Users;
using AdaPanel.Core.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Panelin BÜTÜN site seçicilerini besleyen uç (<c>GET /api/v1/scope/sites</c>) kapsamı doğru
/// çözüyor mu?
/// </summary>
/// <remarks>
/// <para>
/// <b>Bulunan hata (canlı panelde ölçüldü, 0.8.4):</b> uç kapsamı KENDİ İÇİNDE hesaplıyordu —
/// "Administrator değilse ve <c>IsCustomer</c> değilse boş liste". Bayi ikisine de girmediği için
/// kendi sitesi olduğu hâlde boş liste alıyordu: <c>GET /api/v1/sites</c> siteyi döndürürken,
/// <c>GET /api/v1/scope/sites</c> <c>[]</c> döndürüyordu. Panelin dosya, yedek, e-posta, FTP, DNS,
/// PHP ve günlük ekranlarının hepsi site listesini tek bir depodan bu uçtan okuduğu için bayinin
/// o ekranlarında seçenek hiç çıkmıyordu.
/// </para>
/// <para>
/// Bu testler iki yönü birlikte korur: kapsamın <b>yeterince geniş</b> (bayi kendi sitesini görür)
/// ve <b>yeterince dar</b> (kapsamsız rol hiçbir şey görmez) olması. Yalnız birincisini yazmak,
/// düzeltmeyi bir yetki genişletmesine çevirme riskini korumasız bırakırdı.
/// </para>
/// </remarks>
public sealed class ScopeSiteListTests
{
    private static readonly Guid KullaniciId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    [Fact]
    public async Task Bayi_KENDI_kapsamindaki_siteyi_gorur()
    {
        // Bayinin etkin kapsamı = kendi siteleri + sahip olduğu müşterilerin siteleri
        // (CustomerScopeService.GetResellerSitesAsync). Uç bunu sormalı.
        var sonuc = await CagirAsync("Reseller", bayiSiteleri: ["bayinin-sitesi"], musteriSiteleri: []);

        var site = Assert.Single(sonuc);
        Assert.Equal("bayinin-sitesi", site.Name);
    }

    [Fact]
    public async Task Musteri_YALNIZ_atanan_siteyi_gorur()
    {
        var sonuc = await CagirAsync("Customer", bayiSiteleri: ["baskasinin-sitesi"], musteriSiteleri: ["benim-sitem"]);

        var site = Assert.Single(sonuc);
        Assert.Equal("benim-sitem", site.Name);
    }

    [Fact]
    public async Task Yonetici_TUM_siteleri_gorur()
    {
        var sonuc = await CagirAsync("Administrator", bayiSiteleri: [], musteriSiteleri: []);

        Assert.Equal(
            ["bayinin-sitesi", "benim-sitem", "baskasinin-sitesi"],
            sonuc.Select(site => site.Name).ToArray());
    }

    [Fact]
    public async Task Kapsami_TANIMSIZ_rol_HICBIR_sey_gormez()
    {
        // Operator kapsam denetimine tabi DEĞİLDİR (CustomerAccess onu kapsamsız sayar ve null
        // döner). null'ı "hepsi" saymak bu ucu yetki genişletmesine çevirirdi; kapalı tarafta
        // kalınmalı. Bu test, düzeltmenin yönünü sabitler.
        var sonuc = await CagirAsync("Operator", bayiSiteleri: ["bayinin-sitesi"], musteriSiteleri: ["benim-sitem"]);

        Assert.Empty(sonuc);
    }

    private static async Task<IReadOnlyCollection<SiteDto>> CagirAsync(
        string rol,
        IReadOnlyCollection<string> bayiSiteleri,
        IReadOnlyCollection<string> musteriSiteleri)
    {
        var siteler = new List<SiteDto>
        {
            new() { Name = "bayinin-sitesi" },
            new() { Name = "benim-sitem" },
            new() { Name = "baskasinin-sitesi" }
        };

        var controller = new ScopeController(
            new SahteSiteServisi(siteler),
            new SahteKapsam(musteriSiteleri, bayiSiteleri))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [
                            new Claim(ClaimTypes.NameIdentifier, KullaniciId.ToString()),
                            new Claim(ClaimTypes.Role, rol)
                        ],
                        authenticationType: "test"))
                }
            }
        };

        var yanit = await controller.GetSitesAsync(CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(yanit.Result);
        var zarf = Assert.IsType<ApiResponse<IReadOnlyCollection<SiteDto>>>(ok.Value);
        return zarf.Data!;
    }

    /// <summary>Yalnız kapsam sorularına cevap verir; başka üye çağrılırsa test yanlış kurulmuştur.</summary>
    private sealed class SahteKapsam(
        IReadOnlyCollection<string> musteriSiteleri,
        IReadOnlyCollection<string> bayiSiteleri) : ICustomerScopeService
    {
        public Task<IReadOnlyCollection<string>> GetAssignedSitesAsync(Guid userId, CancellationToken cancellationToken = default)
            => Task.FromResult(musteriSiteleri);

        public Task<IReadOnlyCollection<string>> GetResellerSitesAsync(Guid resellerId, CancellationToken cancellationToken = default)
            => Task.FromResult(bayiSiteleri);

        public Task<CustomerScope> GetScopeAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetScopeAsync(Guid userId, IEnumerable<string> siteNames, int maxSites, int maxDiskMb, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetExtraLimitsAsync(Guid userId, int maxDatabases, int maxMailboxes, int maxScheduledTasks, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RemoveUserAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RemoveSiteFromAllScopesAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<string>> GetAllowedMailDomainsAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DiskUsage> GetDiskUsageMbAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetOwnerAsync(Guid userId, Guid? ownerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Guid?> GetOwnerAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<Guid>> GetOwnedUserIdsAsync(Guid ownerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<string>> GetResellerMailDomainsAsync(Guid resellerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Guid?> GetSiteOwnerUserIdAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetParentSubscriptionAsync(Guid userId, Guid? parentUserId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Guid?> GetParentSubscriptionAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<Guid>> GetSubUserIdsAsync(Guid parentUserId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetSubUserSectionsAsync(Guid userId, IReadOnlyCollection<string>? sections, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<string>?> GetSubUserSectionsAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetPlanAsync(Guid userId, string? planId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetExpiryAsync(Guid userId, DateTime? expiresAtUtc, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SuspendAsync(Guid userId, string reason, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ResumeAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<Guid>> GetSuspendedUserIdsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<Guid>> GetSubscriptionUserIdsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class SahteSiteServisi(IReadOnlyCollection<SiteDto> siteler) : IIisSiteService
    {
        public Task<IReadOnlyCollection<SiteDto>> GetSitesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(siteler);

        public Task<SiteDto> GetSiteAsync(string siteName, CancellationToken cancellationToken = default)
        {
            var site = siteler.FirstOrDefault(item => string.Equals(item.Name, siteName, StringComparison.OrdinalIgnoreCase));
            return site is null
                ? throw new AppException("sites.not_found", 404, 404)
                : Task.FromResult(site);
        }

        public Task<SiteDto> CreateSiteAsync(CreateSiteRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDto> UpdatePhysicalPathAsync(string siteName, string physicalPath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteSiteAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDto> StartSiteAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDto> StopSiteAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDto> AddBindingAsync(string siteName, SiteBindingRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDto> RemoveBindingAsync(string siteName, RemoveSiteBindingRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<SiteDto>> ImportExistingSitesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
