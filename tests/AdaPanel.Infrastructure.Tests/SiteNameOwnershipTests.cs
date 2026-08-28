using System.Security.Claims;
using AdaPanel.Api.Security;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Dtos.Users;
using AdaPanel.Core.Interfaces;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// SITE ADI DA BIR SAHIPLIK IDDIASIDIR -- ve hic denetlenmiyordu.
/// <para>
/// Panel host adi icin sahiplik soruyordu (EnsureDomainClaimAsync), site ADI icin sormuyordu.
/// Uc ayri capraz-kiraci yolu aciktı:
/// </para>
/// <list type="number">
/// <item>
/// <b>IIS sitesinin devralinmasi:</b> CreateSiteAsync ayni adli IIS sitesini onay sormadan
/// KALDIRIR. Oradaki tek koruma AdaPanel.* onekiydi -- yani PANELIN kendi altyapisi korunuyor,
/// MUSTERILERINKI korunmuyordu.
/// </item>
/// <item>
/// <b>Posta devri:</b> CustomerScopeService atanan site ADINI izinli mail domaini sayar.
/// "kurban.com" adli site acan kiraci kurbanin posta kutularini listeler ve PAROLALARINI
/// DEGISTIRIR.
/// </item>
/// <item>
/// <b>DNS bolgesi:</b> varsayilan bolge adi site adindan turetilir.
/// </item>
/// </list>
/// </summary>
public sealed class SiteNameOwnershipTests
{
    private static readonly Guid KiraciId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    // --- 1) Ad iddiasi: baskasinin adiyla site acilamaz ---

    [Theory]
    [InlineData("kurban.com")]
    [InlineData("KURBAN.COM")]
    [InlineData("www.kurban.com")]
    public async Task Baskasinin_alan_adiyla_SITE_ADI_alinamaz(string ad)
    {
        // Ayni fonksiyon host adi icin zaten cagriliyordu; acik, site ADI icin HIC cagrilmamasiydi.
        var hata = await Assert.ThrowsAsync<AppException>(() => IddiaEt(ad));
        Assert.Equal("sites.host_name_taken", hata.MessageKey);
    }

    [Fact]
    public async Task Sahipsiz_bir_ad_serbest_kalir()
    {
        // Kural yalnizca BASKASININ adini reddeder; yeni site acmak bugunku gibi calismali.
        await IddiaEt("yepyeni-site.com");
    }

    // --- 2) Var olan IIS sitesinin ustune yazilamaz ---

    [Fact]
    public async Task Kapsam_disindaki_var_olan_IIS_sitesinin_adi_kullanilamaz()
    {
        // Bu, (1)'den AYRI bir kapidir: EnsureDomainClaimAsync sistem sitelerini goremez
        // (GetSitesAsync onlari gizler) ve ad "bosta" gorunur. Oysa var olan bir siteyi kaldirmak
        // yikici bir istir.
        var hata = await Assert.ThrowsAsync<AppException>(() => AdiKullan("kurban-site"));

        Assert.Equal("sites.duplicate_name", hata.MessageKey);
        Assert.Equal(409, hata.HttpStatusCode);
    }

    [Fact]
    public async Task Gizlenmis_sistem_sitesinin_adi_da_kullanilamaz()
    {
        // Kontrol GetSiteAsync ile yapilir; liste ucuyla yapilsaydi bu site "yok" sayilir ve
        // panelin kendi araci sessizce kaldirilirdi.
        var hata = await Assert.ThrowsAsync<AppException>(() => AdiKullan("AdaPanel FTP"));
        Assert.Equal("sites.duplicate_name", hata.MessageKey);
    }

    [Fact]
    public async Task KENDI_sitesinin_adi_yeniden_kullanilabilir()
    {
        // Mesru akis: ayni domaini yeniden ekleme. Kiraci zaten o siteyi silebiliyor.
        await AdiKullan("benim-site");
    }

    [Fact]
    public async Task Bosta_olan_ad_serbest_kalir()
    {
        await AdiKullan("hic-olmayan-site");
    }

    [Fact]
    public async Task Yonetici_bu_denetime_takilmaz()
    {
        await AdiKullan("kurban-site", rol: "Administrator");
    }

    // --- 3) Kablolama: uc gercekten cagriliyor mu ---

    /// <summary>
    /// Kontrolun VAR olmasi yetmez, CAGRILIYOR olmali ve provisyondan ONCE gelmeli.
    /// </summary>
    [Fact]
    public void Site_olusturma_ucu_ADI_da_dogruluyor()
    {
        var kaynak = OkuKaynak("src", "AdaPanel.Api", "Controllers", "SitesController.cs");

        var adIddiasi = kaynak.IndexOf("User, request.Name, null, logger, cancellationToken", StringComparison.Ordinal);
        var adBosta = kaynak.IndexOf("EnsureSiteNameIsClaimableAsync", StringComparison.Ordinal);
        var provisyon = kaynak.IndexOf("CreateAndProvisionAsync", StringComparison.Ordinal);

        Assert.True(adIddiasi > 0, "Site ADI icin sahiplik iddiasi sorulmuyor.");
        Assert.True(adBosta > 0, "Var olan site adi kontrolu yok.");
        Assert.True(provisyon > adIddiasi && provisyon > adBosta, "Kontroller provisyondan SONRA calisiyor.");
    }

    // --- duzenek ---

    private static readonly List<SiteDto> Siteler =
    [
        new() { Name = "benim-site", Bindings = [new SiteBindingDto { HostName = "benim.com" }] },
        new() { Name = "kurban-site", Bindings = [new SiteBindingDto { HostName = "kurban.com" }] },
        // Sistem sitesi: liste ucunda GORUNMEZ ama IIS'te vardir.
        new() { Name = "AdaPanel FTP", Bindings = [] }
    ];

    private static ClaimsPrincipal Kullanici(string rol) => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.NameIdentifier, KiraciId.ToString()), new Claim(ClaimTypes.Role, rol)],
        "test"));

    private static Task IddiaEt(string ad) => CustomerAccess.EnsureDomainClaimAsync(
        new SahteKapsam(["benim-site"]),
        new SahteSiteServisi(Siteler),
        mailService: null,
        Kullanici("Customer"),
        ad,
        ownSiteName: null,
        logger: null,
        CancellationToken.None);

    private static Task AdiKullan(string ad, string rol = "Customer") =>
        CustomerAccess.EnsureSiteNameIsClaimableAsync(
            new SahteKapsam(["benim-site"]),
            new SahteSiteServisi(Siteler),
            Kullanici(rol),
            ad,
            CancellationToken.None);

    private static string OkuKaynak(params string[] parcalar)
    {
        var kok = AppContext.BaseDirectory;
        while (kok is not null && !File.Exists(Path.Combine(kok, "AdaPanel.sln")))
        {
            kok = Path.GetDirectoryName(kok);
        }

        Assert.NotNull(kok);
        return File.ReadAllText(Path.Combine([kok!, .. parcalar]));
    }

    /// <summary>Liste ucu sistem sitelerini GIZLER; tekil okuma gizlemez -- gercek davranis budur.</summary>
    private sealed class SahteSiteServisi(IReadOnlyCollection<SiteDto> siteler) : IIisSiteService
    {
        public Task<IReadOnlyCollection<SiteDto>> GetSitesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyCollection<SiteDto>>(
                siteler.Where(site => !site.Name.StartsWith("AdaPanel", StringComparison.OrdinalIgnoreCase)).ToArray());

        public Task<SiteDto> GetSiteAsync(string siteName, CancellationToken cancellationToken = default)
            => siteler.FirstOrDefault(site => string.Equals(site.Name, siteName, StringComparison.OrdinalIgnoreCase)) is { } bulunan
                ? Task.FromResult(bulunan)
                : throw new AppException("sites.site_not_found", 404, 404);

        public Task<SiteDto> CreateSiteAsync(CreateSiteRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDto> UpdatePhysicalPathAsync(string siteName, string physicalPath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteSiteAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDto> StartSiteAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDto> StopSiteAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDto> AddBindingAsync(string siteName, SiteBindingRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDto> RemoveBindingAsync(string siteName, RemoveSiteBindingRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<SiteDto>> ImportExistingSitesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class SahteKapsam(IReadOnlyCollection<string> siteler) : ICustomerScopeService
    {
        public Task<IReadOnlyCollection<string>> GetAssignedSitesAsync(Guid userId, CancellationToken cancellationToken = default)
            => Task.FromResult(siteler);

        public Task<IReadOnlyCollection<string>> GetAllowedMailDomainsAsync(Guid userId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyCollection<string>>(["benim-site", "benim.com"]);

        public Task<Guid?> GetParentSubscriptionAsync(Guid userId, CancellationToken cancellationToken = default)
            => Task.FromResult<Guid?>(null);

        public Task<CustomerScope> GetScopeAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetScopeAsync(Guid userId, IEnumerable<string> siteNames, int maxSites, int maxDiskMb, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetExtraLimitsAsync(Guid userId, int maxDatabases, int maxMailboxes, int maxScheduledTasks, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RemoveUserAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RemoveSiteFromAllScopesAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DiskUsage> GetDiskUsageMbAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetOwnerAsync(Guid userId, Guid? ownerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Guid?> GetOwnerAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<Guid>> GetOwnedUserIdsAsync(Guid ownerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<string>> GetResellerSitesAsync(Guid resellerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<string>> GetResellerMailDomainsAsync(Guid resellerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Guid?> GetSiteOwnerUserIdAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetParentSubscriptionAsync(Guid userId, Guid? parentUserId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
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
}
