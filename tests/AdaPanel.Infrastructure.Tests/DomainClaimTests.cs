using System.Security.Claims;
using AdaPanel.Api.Security;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// <see cref="CustomerAccess.EnsureDomainClaimAsync"/> — alan adı sahiplenme denetimi.
/// <para>
/// Bu kontrolün ağırlığı şuradan geliyor: panelde bir kullanıcının hangi mail domainlerini
/// yönetebileceği site BAĞLAMALARINDAN türetiliyor. Yani alan adı iddiası mail sahipliğinin
/// güven kökü; burada açılan bir aralık doğrudan "başkasının postasını oku"ya çıkıyor.
/// </para>
/// </summary>
public sealed class DomainClaimTests
{
    private static readonly Guid KiraciId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public async Task Baskasinin_sitesine_bagli_alan_adi_sahiplenilemez()
    {
        var hata = await Assert.ThrowsAsync<AppException>(() => Calistir("kurban.com"));
        Assert.Equal("sites.host_name_taken", hata.MessageKey);
        Assert.Equal(403, hata.HttpStatusCode);
    }

    [Fact]
    public async Task Www_oneki_ile_ayni_domain_sahiplenilemez()
    {
        // GetAllowedMailDomainsAsync, "www.kurban.com" bağlamasından "kurban.com" mail domainini de
        // türetir. Denetim aynı kuralı uygulamazsa saldırgan "www.kurban.com" bağlar ve kurbanın
        // ÖNEKSİZ mail domainini alır. İki yön de kapalı olmalı.
        await Assert.ThrowsAsync<AppException>(() => Calistir("www.kurban.com"));
        await Assert.ThrowsAsync<AppException>(() => Calistir("kurban.com", kurbaninBaglamasi: "www.kurban.com"));
    }

    [Theory]
    [InlineData("KURBAN.COM")]      // büyük/küçük harf
    [InlineData("  kurban.com  ")]  // kenar boşlukları
    [InlineData("kurban.com.")]     // sondaki nokta (DNS'te aynı adı gösterir)
    public async Task Yazim_varyasyonlari_denetimi_atlatamaz(string varyasyon)
    {
        await Assert.ThrowsAsync<AppException>(() => Calistir(varyasyon));
    }

    [Fact]
    public async Task Site_ADI_da_sahiplik_sayilir()
    {
        // GetAllowedMailDomainsAsync site adının kendisini de mail domaini sayar (domains.Add(siteName)),
        // dolayısıyla adı "kurban.com" olan bir site, hiç bağlaması olmasa bile o domaine sahiptir.
        await Assert.ThrowsAsync<AppException>(() => Calistir("kurban.com", kurbaninBaglamasi: string.Empty));
    }

    [Fact]
    public async Task Sahipsiz_alan_adi_serbesttir()
    {
        // Denetim yalnız BAŞKASININ sahip olduğunu reddeder. Yeni bir alan adı bağlamak meşru bir
        // işlemdir ve kırılmamalı — mevcut akışların hepsi buradan geçiyor.
        await Calistir("yeni-alan.com");
    }

    [Fact]
    public async Task Kendi_sitesindeki_alan_adi_serbesttir()
    {
        await Calistir("benim.com");
    }

    [Fact]
    public async Task Yonetici_denetime_takilmaz()
    {
        await Calistir("kurban.com", rol: "Administrator");
    }

    [Theory]
    [InlineData("blog.kurban.com")]
    [InlineData("mail.kurban.com")]
    [InlineData("a.b.c.kurban.com")]     // çok seviyeli
    [InlineData("WWW.BLOG.KURBAN.COM")]  // büyük harf + www öneki
    public async Task Baskasinin_alan_adinin_ALTINA_site_acilamaz(string altAlan)
    {
        // Denetim yalnız TAM eşleşmeye bakıyordu: "blog.kurban.com" ile "kurban.com" kesişmediği için
        // saldırgan kurbanın alan adının altına site açabiliyordu. Bunun bedeli sadece içerik değil:
        //   • Mail sahipliği bağlamalardan türetiliyor → alt alan adının postası saldırgana geçer.
        //   • Panel o ad için Let's Encrypt sertifikası isteyebilir.
        //   • Çerezler `.kurban.com` kapsamında yazılıyorsa oturum çalınabilir.
        var hata = await Assert.ThrowsAsync<AppException>(() => Calistir(altAlan));
        Assert.Equal("sites.host_name_taken", hata.MessageKey);
    }

    [Theory]
    [InlineData("notkurban.com")]        // sonek benziyor ama etiket sınırı yok
    [InlineData("kurban.com.tr")]        // farklı üst düzey alan
    [InlineData("bizimkurban.com")]
    [InlineData("kurban.community")]
    public async Task Benzeyen_ama_ALTINDA_olmayan_adlar_serbest_kalir(string benzer)
    {
        // Sonek denetimi ETİKET SINIRINDA yapılmalı. Düz `EndsWith` kullanılsaydı "notkurban.com"
        // da reddedilirdi ve meşru müşteriler kendi alan adlarını bağlayamazdı — özellik
        // düzeltirken başka bir şeyi bozmanın tipik örneği.
        await Calistir(benzer);
    }

    [Theory]
    [InlineData("blog.benim.com")]
    [InlineData("shop.benim-site")]
    public async Task KENDI_alan_adinin_altina_site_acilabilir(string kendiAltAlani)
    {
        // Asıl kullanım budur: müşteri kendi alan adının altına alt alan adı açar. Kısıt
        // yalnız BAŞKASININ alanına girmeyi engeller.
        await Calistir(kendiAltAlani);
    }

    [Fact]
    public async Task Ust_alan_adi_sahiplenmek_ENGELLENMEZ()
    {
        // Bilinçli sınır: kurban "blog.kurban.com" adlı bir siteye sahipken, "kurban.com" iddiası
        // reddedilmiyor. Yönetici bir kurumsal alan adını bilerek birden çok müşteriye bölmüş
        // olabilir; bu yönü de kapatmak var olan kurulumları kırardı. Engellenen, asıl yükseltme
        // yolu olan "başkasının alanının ALTINA girme"dir.
        await Calistir("kurban.com", kurbaninBaglamasi: "blog.kurban.com");
    }

    // --- düzenek ---

    private static Task Calistir(
        string iddiaEdilen,
        string rol = "Customer",
        string kurbaninBaglamasi = "kurban.com")
    {
        var siteler = new List<SiteDto>
        {
            new()
            {
                Name = "benim-site",
                Bindings = [new SiteBindingDto { HostName = "benim.com" }]
            },
            new()
            {
                // Kurbanın sitesi. Adı bilerek domainden farklı: sahiplik bağlamadan gelmeli.
                Name = kurbaninBaglamasi.Length == 0 ? "kurban.com" : "kurban-site",
                Bindings = kurbaninBaglamasi.Length == 0
                    ? []
                    : [new SiteBindingDto { HostName = kurbaninBaglamasi }]
            }
        };

        var kullanici = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, KiraciId.ToString()),
                new Claim(ClaimTypes.Role, rol)
            ],
            "test"));

        return CustomerAccess.EnsureDomainClaimAsync(
            new SahteKapsam(["benim-site"]),
            new SahteSiteServisi(siteler),
            mailService: null,
            kullanici,
            iddiaEdilen,
            ownSiteName: "benim-site",
            logger: null,
            CancellationToken.None);
    }

    /// <summary>Yalnız kapsam sorularına cevap verir; geri kalan üyeler çağrılırsa test yanlış kurulmuştur.</summary>
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
        public Task<Guid?> GetSiteOwnerUserIdAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetParentSubscriptionAsync(Guid userId, Guid? parentUserId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<Guid>> GetSubUserIdsAsync(Guid parentUserId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetSubUserSectionsAsync(Guid userId, IReadOnlyCollection<string>? sections, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<string>?> GetSubUserSectionsAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        // --- Abonelik iş modeli (bu testlerde kullanılmıyor) ---
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

        public Task<SiteDto> GetSiteAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
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
