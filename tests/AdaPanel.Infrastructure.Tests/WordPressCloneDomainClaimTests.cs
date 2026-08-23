using System.Security.Claims;
using AdaPanel.Api.Controllers;
using AdaPanel.Api.Security;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Mail;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// K7 — WORDPRESS KLONUNUN HEDEF SİTE ADI DA BİR SAHİPLİK İDDİASIDIR.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="DomainClaimTests"/> denetimin KENDİSİNİ doğruluyor; bu dosya onun KLON YOLUNDA
/// uygulandığını doğrular. İkisi ayrı şeydir ve açık tam da bu farkta yaşıyordu: kural doğruydu,
/// klon yolunda çağrılmıyordu.
/// </para>
/// <para>
/// Açığın zinciri: <c>CloneAsync</c> yalnız <c>TargetHostName</c>'i denetliyordu.
/// <c>TargetSiteName</c> hiçbir denetimden geçmiyordu ve <c>WordPressToolkitService.IsSafeName</c>
/// NOKTA KABUL EDER — yani "kurban.com" geçerli bir hedef site adıdır. Klon oluşunca denetleyici
/// yeni siteyi saldırganın kapsamına ekliyor, <c>CustomerScopeService</c> ise site ADINI izinli
/// POSTA ALAN ADI sayıyor: kurbanın posta kutuları listelenir, parolaları değiştirilir, alias ile
/// postası yönlendirilir. DNS bölgesi de site adından türediği için devralınır.
/// </para>
/// <para>
/// Test, denetleyicinin çağırdığı iki fonksiyonu (<see cref="CustomerAccess.EnsureDomainClaimAsync"/>
/// ve <see cref="CustomerAccess.EnsureSiteNameIsClaimableAsync"/>) klon yolundaki ARGÜMANLARLA
/// koşturur; ayrıca denetleyici kaynağının bu çağrıları gerçekten <c>TargetSiteName</c> için
/// yaptığını doğrular — aksi hâlde kural doğru olur ama yine çağrılmazdı.
/// </para>
/// </remarks>
public sealed class WordPressCloneDomainClaimTests
{
    private static readonly Guid KiraciId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public async Task SALDIRI_musteri_kurban_com_adiyla_KLONLAYAMAZ()
    {
        // Saldırı testinin ta kendisi: hedef host adı masum ("staging.saldirgan.com"), hedef SİTE
        // ADI kurbanın alan adı. Eski kod yalnız host adına baktığı için bu istek geçiyordu.
        var hata = await Assert.ThrowsAsync<AppException>(() => HedefAdiniDenetle("kurban.com"));

        Assert.Equal("sites.host_name_taken", hata.MessageKey);
        Assert.Equal(403, hata.HttpStatusCode);
    }

    [Theory]
    [InlineData("KURBAN.COM")]
    [InlineData("  kurban.com  ")]
    [InlineData("kurban.com.")]
    [InlineData("www.kurban.com")]
    [InlineData("blog.kurban.com")]
    public async Task Yazim_varyasyonlari_ve_alt_alan_adlari_da_KLONLANAMAZ(string varyasyon)
    {
        // Host adı yolunda kapatılmış her varyasyon klon yolunda da kapalı olmalı. Bu, ikinci bir
        // kural yazmak yerine AYNI fonksiyonu çağırmanın somut karşılığı: yeni kural yazılsaydı
        // bunların bir kısmı açıkta kalırdı.
        await Assert.ThrowsAsync<AppException>(() => HedefAdiniDenetle(varyasyon));
    }

    [Fact]
    public async Task Mesru_staging_klonu_ENGELLENMEZ()
    {
        // Özelliğin asıl kullanımı: müşteri kendi sitesinin staging klonunu açar. Düzeltme bunu
        // kırarsa özelliği kapatmış oluruz.
        await HedefAdiniDenetle("benim-site-staging");
        await HedefAdiniDenetle("staging.benim.com");
    }

    [Fact]
    public async Task Yonetici_klon_denetimine_takilmaz()
    {
        await HedefAdiniDenetle("kurban.com", rol: "Administrator");
    }

    [Fact]
    public async Task Kapsam_disindaki_VAR_OLAN_bir_sitenin_adiyla_klonlanamaz()
    {
        // İkinci kapı: ad "boşta" olmasa bile üzerine yazılamaz. CloneAsync'in kendi çakışma
        // denetimi GetSitesAsync'e bakar ve o SİSTEM SİTELERİNİ gizler; gizlenmiş bir siteyi
        // "yok" saymak tam da üzerine yazılmasına izin vermek olurdu.
        var hata = await Assert.ThrowsAsync<AppException>(() =>
            CustomerAccess.EnsureSiteNameIsClaimableAsync(
                new SahteKapsam(["benim-site"]),
                new SahteSiteServisi([Site("gizli-sistem-sitesi")], gizlenenler: ["gizli-sistem-sitesi"]),
                Kullanici("Customer"),
                "gizli-sistem-sitesi",
                CancellationToken.None));

        Assert.Equal("sites.duplicate_name", hata.MessageKey);
        Assert.Equal(409, hata.HttpStatusCode);
    }

    [Fact]
    public async Task SALDIRI_klon_UCU_kurban_com_hedefini_reddediyor_ve_klonlamaya_HIC_baslamiyor()
    {
        // EN GÜÇLÜ HALKA: kural değil, GERÇEK UÇ koşuluyor. Müşteri rolündeki bir kullanıcı kendi
        // sitesinden klon isterken hedef site adına kurbanın alan adını yazıyor; host adı masum.
        //
        // Klon araç servisi PATLAYAN bir sahte: uca ulaşılırsa test beklediği 403'ü değil onu
        // görür. Yani bu test yalnız "hata döndü mü"yü değil, YIKICI İŞİN HİÇ BAŞLAMADIĞINI de
        // doğruluyor — klon başlasaydı IIS sitesi, veritabanı ve kapsam ataması çoktan oluşurdu.
        var denetleyici = new WordPressController(new PatlayanArac(), new SahteKapsam(["benim-site"]), new RecordingAuditService())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = Kullanici("Customer") }
            }
        };

        var hata = await Assert.ThrowsAsync<AppException>(() => denetleyici.CloneAsync(
            "benim-site",
            new CloneWordPressRequest
            {
                TargetSiteName = "kurban.com",              // ← saldırı burada
                TargetHostName = "staging.saldirgan.com"    // ← masum görünen host adı
            },
            new SahteSiteServisi([Site("benim-site", "benim.com"), Site("kurban-site", "kurban.com")]),
            MailServisiGerekmiyor,
            NullLogger<WordPressController>.Instance,
            CancellationToken.None));

        Assert.Equal("sites.host_name_taken", hata.MessageKey);
        Assert.Equal(403, hata.HttpStatusCode);
    }

    [Fact]
    public async Task Mesru_staging_klonu_UCTAN_gecer()
    {
        // Aynı uç, meşru istek: düzeltme özelliği kapatmamalı. Buradaki klon araç servisi çalışır
        // ve uç 200 döner.
        var denetleyici = new WordPressController(new SahteArac(), new SahteKapsam(["benim-site"]), new RecordingAuditService())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = Kullanici("Customer") }
            }
        };

        var sonuc = await denetleyici.CloneAsync(
            "benim-site",
            new CloneWordPressRequest
            {
                TargetSiteName = "benim-site-staging",
                TargetHostName = "staging.benim.com"
            },
            new SahteSiteServisi([Site("benim-site", "benim.com"), Site("kurban-site", "kurban.com")]),
            MailServisiGerekmiyor,
            NullLogger<WordPressController>.Instance,
            CancellationToken.None);

        var govde = Assert.IsType<OkObjectResult>(sonuc.Result).Value as ApiResponse<WordPressCloneResultDto>;
        Assert.NotNull(govde);
        Assert.Equal("benim-site-staging", govde!.Data!.TargetSiteName);
    }

    [Fact]
    public void Denetleyici_klon_ucunda_HEDEF_SITE_ADINI_gercekten_denetliyor()
    {
        // Yukarıdaki testler kuralın DOĞRU olduğunu gösterir; bu test kuralın klon yolunda
        // ÇAĞRILDIĞINI gösterir. Açık tam olarak buradaydı: kural vardı, çağrı yoktu.
        var kaynak = File.ReadAllText(Path.Combine(DepoKoku(), "src", "AdaPanel.Api", "Controllers", "WordPressController.cs"));
        var klon = kaynak[kaynak.IndexOf("CloneAsync(", StringComparison.Ordinal)..];
        klon = klon[..klon.IndexOf("CopyDataAsync(", StringComparison.Ordinal)];

        Assert.Contains("EnsureDomainClaimAsync(", klon);
        Assert.Contains("request.TargetSiteName", klon);
        Assert.Contains("EnsureSiteNameIsClaimableAsync(", klon);

        // Host adı denetimi de yerinde durmalı — yeni denetim eskisinin yerine geçmemeli.
        Assert.Contains("request.TargetHostName", klon);
    }

    // --- düzenek ---

    /// <summary>
    /// Denetleyicinin klon yolunda yaptığı çağrının aynısı: hedef SİTE ADI için sahiplik iddiası,
    /// <c>ownSiteName</c> geçilmeden (hedef yeni bir site; kaynağın adı gerekçe olamaz).
    /// </summary>
    private static Task HedefAdiniDenetle(string hedefSiteAdi, string rol = "Customer")
        => CustomerAccess.EnsureDomainClaimAsync(
            new SahteKapsam(["benim-site"]),
            new SahteSiteServisi([
                Site("benim-site", "benim.com"),
                Site("kurban-site", "kurban.com")
            ]),
            mailService: null,
            Kullanici(rol),
            hedefSiteAdi,
            ownSiteName: null,
            logger: null,
            CancellationToken.None);

    /// <summary>
    /// Mail servisi bu testlerde GEREKMİYOR ve bilerek <c>null</c> geçiliyor:
    /// <see cref="CustomerAccess.EnsureDomainClaimAsync"/> onu <c>IMailService?</c> olarak alır ve
    /// yoksa yalnız site bağlamalarına bakar — saldırı zaten oradan yakalanıyor.
    /// <see cref="DomainClaimTests"/> de aynı yolu izliyor.
    /// </summary>
    private static readonly IMailService MailServisiGerekmiyor = null!;

    /// <summary>Klon işine ULAŞILIRSA test kırılsın: yıkıcı iş hiç başlamamalı.</summary>
    private sealed class PatlayanArac : IWordPressToolkitService
    {
        public Task<WordPressSiteDto> DetectAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<WordPressSiteDto>> DetectAllAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<WordPressSiteDto> SetMaintenanceAsync(string siteName, bool enabled, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<WordPressLoginLinkDto> CreateLoginLinkAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<WordPressHardenResultDto> HardenAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<WordPressCloneResultDto> CloneAsync(string sourceSiteName, CloneWordPressRequest request, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException(
                "KLONLAMA BASLADI: sahiplik denetimi gecilmis. Bu noktada IIS sitesi, veritabani ve kapsam atamasi olusurdu.");

        public Task<WordPressCopyDataResultDto> CopyDataAsync(string sourceSiteName, CopyWordPressDataRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<WordPressUpdatesDto> GetUpdatesAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<WordPressUpdateResultDto> ApplyUpdatesAsync(string siteName, WordPressUpdateRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    /// <summary>Meşru yol için: klonu gerçekten "yapar" (kaydını döner).</summary>
    private sealed class SahteArac : IWordPressToolkitService
    {
        public Task<WordPressCloneResultDto> CloneAsync(string sourceSiteName, CloneWordPressRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new WordPressCloneResultDto
            {
                TargetSiteName = request.TargetSiteName,
                TargetUrl = $"http://{request.TargetHostName}"
            });

        public Task<WordPressSiteDto> DetectAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<WordPressSiteDto>> DetectAllAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<WordPressSiteDto> SetMaintenanceAsync(string siteName, bool enabled, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<WordPressLoginLinkDto> CreateLoginLinkAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<WordPressHardenResultDto> HardenAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<WordPressCopyDataResultDto> CopyDataAsync(string sourceSiteName, CopyWordPressDataRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<WordPressUpdatesDto> GetUpdatesAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<WordPressUpdateResultDto> ApplyUpdatesAsync(string siteName, WordPressUpdateRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private static SiteDto Site(string ad, string? host = null) => new()
    {
        Name = ad,
        Bindings = host is null ? [] : [new SiteBindingDto { HostName = host }]
    };

    private static ClaimsPrincipal Kullanici(string rol) => new(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, KiraciId.ToString()),
            new Claim(ClaimTypes.Role, rol)
        ],
        "test"));

    private static string DepoKoku()
    {
        var klasor = Path.GetFullPath(AppContext.BaseDirectory);
        for (var derinlik = 0; derinlik < 8; derinlik++)
        {
            if (File.Exists(Path.Combine(klasor, "AdaPanel.sln")))
            {
                return klasor;
            }

            klasor = Path.GetFullPath(Path.Combine(klasor, ".."));
        }

        throw new DirectoryNotFoundException("Depo koku bulunamadi.");
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
        public Task<long> GetDiskUsageMbAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetOwnerAsync(Guid userId, Guid? ownerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Guid?> GetOwnerAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<Guid>> GetOwnedUserIdsAsync(Guid ownerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<string>> GetResellerSitesAsync(Guid resellerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<string>> GetResellerMailDomainsAsync(Guid resellerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        // Klon ucu sahiplik denetimlerinden SONRA bunu çağırır. Sahipsiz (yönetici sitesi) dönüyor:
        // kota denetimi ve kapsam ataması devre dışı kalır, böylece test yalnız sahiplik kapısını ölçer.
        public Task<Guid?> GetSiteOwnerUserIdAsync(string siteName, CancellationToken cancellationToken = default)
            => Task.FromResult<Guid?>(null);
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

    /// <summary>
    /// <paramref name="gizlenenler"/>: liste ucunun GİZLEDİĞİ (sistem) siteler. Gerçek panelde
    /// <c>GetSitesAsync</c> onları düşürür ama <c>GetSiteAsync</c> döndürür — iki denetimin farklı
    /// kaynaklara bakmasının sebebi bu.
    /// </summary>
    private sealed class SahteSiteServisi(
        IReadOnlyCollection<SiteDto> siteler,
        IReadOnlyCollection<string>? gizlenenler = null) : IIisSiteService
    {
        public Task<IReadOnlyCollection<SiteDto>> GetSitesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyCollection<SiteDto>>(siteler
                .Where(site => gizlenenler is null || !gizlenenler.Contains(site.Name, StringComparer.OrdinalIgnoreCase))
                .ToArray());

        public Task<SiteDto> GetSiteAsync(string siteName, CancellationToken cancellationToken = default)
            => siteler.FirstOrDefault(site => string.Equals(site.Name, siteName, StringComparison.OrdinalIgnoreCase)) is { } bulunan
                ? Task.FromResult(bulunan)
                : throw new AppException("sites.not_found", 404, 404);

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
