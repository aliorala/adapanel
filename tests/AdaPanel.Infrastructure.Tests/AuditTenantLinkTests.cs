using System.Security.Claims;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Dtos.Statistics;
using AdaPanel.Core.Dtos.Users;
using AdaPanel.Core.Entities;
using AdaPanel.Core.Interfaces;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Audit;
using AdaPanel.Infrastructure.Subscriptions;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Denetim kaydının KONUSU (kiracı bağı) ÖLÇÜLEREK çözülür — ve ölçülemediğinde BOŞ kalır.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden bu davranış korunuyor:</b> <c>SubscriptionUserId</c> alanı, kiracının "benim
/// kaynağıma kim dokundu" sorusunu cevaplamasını sağlar (<c>AuditLogQueryEngine.ApplyVisibility</c>
/// bu sütunu ikinci koşul olarak sorar). Alanın YANLIŞ doldurulması, doğru doldurulmamasından
/// kötüdür: kayıt yanlış kiracının görünümüne düşer, yani komşusunun işlemini görür — kapatılmak
/// istenen çapraz-kiracı sızıntısının ta kendisi. Bu yüzden "çözemedik → boş bırak" kuralı
/// testle sabitlenir.
/// </para>
/// </remarks>
public sealed class AuditSubjectTests
{
    private static readonly Guid Kiraci = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid IkinciKiraci = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public async Task Site_adi_dogrudan_SAHIBINE_cozulur()
    {
        var kapsam = new SahiplikKapsami(new Dictionary<string, Guid> { ["musteri-site"] = Kiraci });

        Assert.Equal(Kiraci, await AuditSubject.SiteOwnerAsync(kapsam, "musteri-site", CancellationToken.None));
    }

    [Fact]
    public async Task Sahipsiz_site_BOS_doner()
    {
        // Yöneticinin kendi sitesi hiçbir aboneliğe atanmamıştır. Onu bir kiracıya bağlamak,
        // panelin kendi altyapısını bir müşterinin görünümüne sokmak olurdu.
        var kapsam = new SahiplikKapsami(new Dictionary<string, Guid>());

        Assert.Null(await AuditSubject.SiteOwnerAsync(kapsam, "panel-sitesi", CancellationToken.None));
    }

    [Fact]
    public async Task Sahiplik_okunamazsa_ISTEK_DUSMEZ_konu_BOS_kalir()
    {
        // Konu çözümü ASIL İŞLEMİN yan ürünüdür ve işlem çoktan yapılmıştır. Sahiplik dosyası
        // okunamadı diye posta kutusu açma isteğinin 500 dönmesi, kaydı zenginleştirme çabasının
        // kullanıcıya arıza olarak yansıması olurdu.
        var kapsam = new PatlayanKapsam();

        Assert.Null(await AuditSubject.SiteOwnerAsync(kapsam, "herhangi", CancellationToken.None));
    }

    [Fact]
    public async Task Posta_alan_adi_SITE_BAGLAMASINDAN_sahibine_cozulur()
    {
        // Panelde posta sahipliği site BAĞLAMALARINDAN türetilir (GetAllowedMailDomainsAsync).
        // Bu çözüm o kaynağın TERSİDİR: site adı "musteri1", bağlama "ornek.com".
        var kapsam = new SahiplikKapsami(new Dictionary<string, Guid> { ["musteri1"] = Kiraci });
        var siteler = new SahteSiteler([Site("musteri1", "ornek.com")]);

        Assert.Equal(
            Kiraci,
            await AuditSubject.MailDomainOwnerAsync(kapsam, siteler, "ornek.com", CancellationToken.None));
    }

    [Fact]
    public async Task Www_onekli_baglama_oneksiz_alan_adini_da_SAHIPLENIR()
    {
        // Kontrol, koruduğu şeyle AYNI kuralı uygulamalı: GetAllowedMailDomainsAsync ve
        // CustomerAccess.DomainClaimForms "www." önekini düşürür. Burada düşürülmeseydi
        // www.ornek.com bağlamalı bir aboneliğin ornek.com postası kiracısız kalırdı.
        var kapsam = new SahiplikKapsami(new Dictionary<string, Guid> { ["musteri1"] = Kiraci });
        var siteler = new SahteSiteler([Site("musteri1", "www.ornek.com")]);

        Assert.Equal(
            Kiraci,
            await AuditSubject.MailDomainOwnerAsync(kapsam, siteler, "ornek.com", CancellationToken.None));
    }

    [Fact]
    public async Task Ayni_alan_adini_IKI_ABONELIK_sahipleniyorsa_konu_BOS_kalir()
    {
        // ÖLÇEMEDİK. Birini seçmek yazı tura atmaktır ve yanlış seçim, kaydı yabancı bir kiracının
        // görünümüne düşürür. Bu test, "en azından birini yaz" kolaycılığını yasaklar.
        var kapsam = new SahiplikKapsami(new Dictionary<string, Guid>
        {
            ["musteri1"] = Kiraci,
            ["musteri2"] = IkinciKiraci
        });
        var siteler = new SahteSiteler([Site("musteri1", "ornek.com"), Site("musteri2", "ornek.com")]);

        Assert.Null(await AuditSubject.MailDomainOwnerAsync(kapsam, siteler, "ornek.com", CancellationToken.None));
    }

    [Fact]
    public async Task Posta_ADRESI_alan_adindan_cozulur_yerel_kisimda_istege_duser()
    {
        var kapsam = new SahiplikKapsami(new Dictionary<string, Guid> { ["ornek.com"] = Kiraci });
        var siteler = new SahteSiteler([]);

        Assert.Equal(
            Kiraci,
            await AuditSubject.MailAddressOwnerAsync(kapsam, siteler, "info@ornek.com", null, CancellationToken.None));

        // Adres yalnız yerel kısımsa (panelin posta uçları bunu da kabul eder) istekteki alan adı.
        Assert.Equal(
            Kiraci,
            await AuditSubject.MailAddressOwnerAsync(kapsam, siteler, "info", "ornek.com", CancellationToken.None));
    }

    [Fact]
    public async Task Veritabani_sahibi_PANELIN_KENDI_KAYDINDAN_okunur()
    {
        var sahiplik = new SahteDbSahiplik(new Dictionary<string, Guid> { ["mariadb/shop_db"] = Kiraci });

        Assert.Equal(
            Kiraci,
            await AuditSubject.DatabaseOwnerAsync(sahiplik, "mariadb", "shop_db", CancellationToken.None));

        // Kaydı olmayan (panel dışında açılmış) veritabanının sahibi TAHMİN EDİLMEZ.
        Assert.Null(await AuditSubject.DatabaseOwnerAsync(sahiplik, "mariadb", "yabanci_db", CancellationToken.None));
    }

    private static SiteDto Site(string ad, string host) => new()
    {
        Name = ad,
        Bindings = [new SiteBindingDto { Protocol = "http", HostName = host, Port = 80 }]
    };

    // --- düzenek ---

    private sealed class SahteSiteler(IReadOnlyCollection<SiteDto> siteler) : IIisSiteService
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

    /// <summary>Yalnız site→sahip eşlemesini bilen kapsam servisi; gerisi bu testlerde çağrılmamalı.</summary>
    private sealed class SahiplikKapsami(IReadOnlyDictionary<string, Guid> sahipler) : TemelKapsam
    {
        public override Task<Guid?> GetSiteOwnerUserIdAsync(string siteName, CancellationToken cancellationToken = default)
            => Task.FromResult(sahipler.TryGetValue(siteName, out var sahip) ? sahip : (Guid?)null);
    }

    private sealed class PatlayanKapsam : TemelKapsam
    {
        public override Task<Guid?> GetSiteOwnerUserIdAsync(string siteName, CancellationToken cancellationToken = default)
            => throw new IOException("sahiplik dosyasi okunamadi");
    }

    private sealed class SahteDbSahiplik(IReadOnlyDictionary<string, Guid> sahipler) : IDatabaseOwnershipService
    {
        public Task<Guid?> GetOwnerAsync(string engine, string databaseName, CancellationToken cancellationToken = default)
            => Task.FromResult(sahipler.TryGetValue($"{engine}/{databaseName}", out var sahip) ? sahip : (Guid?)null);

        public Task RecordAsync(string engine, string databaseName, Guid ownerUserId, string? siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RemoveAsync(string engine, string databaseName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RemoveBySiteAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<int> CountForOwnerAsync(Guid ownerUserId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<(string Engine, string DatabaseName)>> GetOwnedAsync(Guid ownerUserId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<(string Engine, string DatabaseName, Guid OwnerUserId, string SiteName)>> GetAllAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    /// <summary>
    /// Kapsam servisinin "bu testlerde kullanılmayan" gövdesi.
    /// </summary>
    /// <remarks>
    /// Üyeler bilerek PATLAR: çözümün beklenmedik bir yoldan (ör. plan okuma) veri toplaması
    /// gerekirse test sessizce geçmek yerine kırılsın.
    /// </remarks>
    internal abstract class TemelKapsam : ICustomerScopeService
    {
        public virtual Task<Guid?> GetSiteOwnerUserIdAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyCollection<string>> GetAssignedSitesAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public virtual Task<CustomerScope> GetScopeAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetScopeAsync(Guid userId, IEnumerable<string> siteNames, int maxSites, int maxDiskMb, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetExtraLimitsAsync(Guid userId, int maxDatabases, int maxMailboxes, int maxScheduledTasks, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RemoveUserAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RemoveSiteFromAllScopesAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<string>> GetAllowedMailDomainsAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<long> GetDiskUsageMbAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetOwnerAsync(Guid userId, Guid? ownerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Guid?> GetOwnerAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<Guid>> GetOwnedUserIdsAsync(Guid ownerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<string>> GetResellerSitesAsync(Guid resellerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<string>> GetResellerMailDomainsAsync(Guid resellerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetParentSubscriptionAsync(Guid userId, Guid? parentUserId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Guid?> GetParentSubscriptionAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<Guid>> GetSubUserIdsAsync(Guid parentUserId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetSubUserSectionsAsync(Guid userId, IReadOnlyCollection<string>? sections, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<string>?> GetSubUserSectionsAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetPlanAsync(Guid userId, string? planId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetExpiryAsync(Guid userId, DateTime? expiresAtUtc, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public virtual Task SuspendAsync(Guid userId, string reason, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public virtual Task ResumeAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<Guid>> GetSuspendedUserIdsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public virtual Task<IReadOnlyCollection<Guid>> GetSubscriptionUserIdsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}

/// <summary>
/// Askıya alma/geri açma kaydı GERÇEK AKTÖRÜ yazar; konusu aboneliktir.
/// </summary>
/// <remarks>
/// <para>
/// <b>Düzeltilen hata:</b> <c>SubscriptionEnforcementService</c> her iki kaydı da
/// <c>userId</c> olarak ABONELİĞİN SAHİBİYLE yazıyordu ve IP hiç taşımıyordu. Yani bir yönetici
/// müşteriyi elle askıya aldığında denetim kaydı "müşteri kendini askıya aldı" diyordu — denetim
/// kaydının cevaplaması gereken tek soru ("kim yaptı") tam tersine cevaplanıyordu. Yanlış olduğu
/// biliniyordu bile: denetleyiciye doğru aktörü taşıyan İKİNCİ bir kayıt eklenmişti, yani her elle
/// askı iki satır üretiyor ve biri yalan söylüyordu.
/// </para>
/// <para>
/// Ayrım TAHMİN EDİLMEZ, ÖLÇÜLÜR: HTTP bağlamı varsa bir insan panelden tetiklemiştir; yoksa arka
/// plan turudur.
/// </para>
/// </remarks>
public sealed class SubscriptionSuspensionAuditTests : IDisposable
{
    private static readonly Guid Musteri = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid Yonetici = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private readonly SqliteConnection baglanti;
    private readonly AdaPanelDbContext dbContext;

    public SubscriptionSuspensionAuditTests()
    {
        baglanti = new SqliteConnection("Filename=:memory:");
        baglanti.Open();
        dbContext = new AdaPanelDbContext(new DbContextOptionsBuilder<AdaPanelDbContext>().UseSqlite(baglanti).Options);
        dbContext.Database.EnsureCreated();
        dbContext.Users.Add(new PanelUser
        {
            Id = Musteri,
            UserName = "musteri",
            Email = "musteri@ornek.test",
            Role = Core.Enums.UserRole.Customer,
            PasswordHash = "x",
            IsActive = true
        });
        dbContext.SaveChanges();
    }

    public void Dispose()
    {
        dbContext.Dispose();
        baglanti.Dispose();
    }

    [Fact]
    public async Task ELLE_askiya_almada_kayit_YONETICIYI_ve_IP_yi_yazar_konu_MUSTERIDIR()
    {
        var kayitlar = new KayitDefteri();
        var servis = Servis(kayitlar, IstekBaglami(Yonetici, "203.0.113.7"));

        await servis.SuspendAsync(Musteri, "manual", CancellationToken.None);

        var kayit = Assert.Single(kayitlar.Kayitlar);
        Assert.Equal("subscription.suspended", kayit.Action);

        // ASIL DÜZELTME: aktör YÖNETİCİDİR, müşteri değil.
        Assert.Equal(Yonetici, kayit.UserId);
        Assert.NotEqual(Musteri, kayit.UserId);

        // Konu müşteridir: kiracı, aboneliğinin askıya alındığını KENDİ ekranında görebilmeli.
        Assert.Equal(Musteri, kayit.SubscriptionUserId);

        // IP artık taşınıyor: "hangi yönetici, NEREDEN" sorusu cevaplanabilir.
        Assert.Equal("203.0.113.7", kayit.IpAddress);

        // Kaynak, hangi aboneliğin askıya alındığını söylemeli; sabit "subscription" yetmez.
        Assert.Contains(Musteri.ToString("N"), kayit.Resource, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OTOMATIK_kota_uygulamasi_ELLE_askidan_AYIRT_EDILEBILIR()
    {
        // Arka plan turunda HTTP bağlamı yoktur: aktör "system" (null) kalır ve tetikleyici
        // kayıtta yazılıdır. İkisi ayrılmazsa, kota yüzünden kapanan bir abonelik ile yöneticinin
        // bilerek kapattığı abonelik denetim ekranında AYNI görünürdü.
        var kayitlar = new KayitDefteri();
        var otomatik = Servis(kayitlar, httpContextAccessor: null);
        await otomatik.SuspendAsync(Musteri, "traffic", CancellationToken.None);

        var otomatikKayit = Assert.Single(kayitlar.Kayitlar);
        Assert.Null(otomatikKayit.UserId);
        Assert.Null(otomatikKayit.IpAddress);
        Assert.Contains("automatic background enforcement", otomatikKayit.Details, StringComparison.Ordinal);
        Assert.Contains("rule: traffic", otomatikKayit.Details, StringComparison.Ordinal);

        var elleKayitlar = new KayitDefteri();
        var elle = Servis(elleKayitlar, IstekBaglami(Yonetici, "203.0.113.7"));
        await elle.SuspendAsync(Musteri, "manual", CancellationToken.None);

        var elleKayit = Assert.Single(elleKayitlar.Kayitlar);
        Assert.Contains("panel request", elleKayit.Details, StringComparison.Ordinal);
        Assert.Contains("rule: manual", elleKayit.Details, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASKIYI_KALDIRMA_kaydi_da_gercek_aktoru_yazar()
    {
        var kayitlar = new KayitDefteri();
        var servis = Servis(kayitlar, IstekBaglami(Yonetici, "198.51.100.4"));

        await servis.ResumeAsync(Musteri, CancellationToken.None);

        var kayit = Assert.Single(kayitlar.Kayitlar);
        Assert.Equal("subscription.resumed", kayit.Action);
        Assert.Equal(Yonetici, kayit.UserId);
        Assert.Equal(Musteri, kayit.SubscriptionUserId);
        Assert.Equal("198.51.100.4", kayit.IpAddress);
    }

    // --- düzenek ---

    private SubscriptionEnforcementService Servis(KayitDefteri kayitlar, IHttpContextAccessor? httpContextAccessor)
        => new(
            new AskiKapsami(),
            new SahteIstatistik(),
            new SahteSiteKontrol(),
            kayitlar,
            dbContext,
            NullLogger<SubscriptionEnforcementService>.Instance,
            httpContextAccessor);

    private static IHttpContextAccessor IstekBaglami(Guid aktor, string ip)
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, aktor.ToString())],
                "test"))
        };
        context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse(ip);
        return new HttpContextAccessor { HttpContext = context };
    }

    /// <summary>Kaydın BÜTÜN alanlarını tutar: aktörü ve IP'yi doğrulamak için ad yetmez.</summary>
    private sealed class KayitDefteri : IAuditService
    {
        public List<(string Action, string Resource, string Details, Guid? UserId, Guid? SubscriptionUserId, string? IpAddress)> Kayitlar { get; } = [];

        public Task WriteAsync(string action, string resource, string details, Guid? userId, string? ipAddress, CancellationToken cancellationToken = default)
            => WriteForSubscriptionAsync(action, resource, details, userId, null, ipAddress, cancellationToken);

        public Task WriteForSubscriptionAsync(string action, string resource, string details, Guid? userId, Guid? subscriptionUserId, string? ipAddress, CancellationToken cancellationToken = default)
        {
            Kayitlar.Add((action, resource, details, userId, subscriptionUserId, ipAddress));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyCollection<Core.Dtos.Audit.AuditLogDto>> GetRecentAsync(int take, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Core.Dtos.Audit.AuditLogPageDto> SearchAsync(Core.Dtos.Audit.AuditLogQuery query, Core.Dtos.Audit.AuditVisibility visibility, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<Core.Dtos.Audit.AuditLogDto> StreamAsync(Core.Dtos.Audit.AuditLogQuery query, Core.Dtos.Audit.AuditVisibility visibility, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Core.Dtos.Audit.AuditVisibility> ResolveVisibilityAsync(Guid userId, Core.Enums.UserRole role, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Core.Dtos.Audit.AuditRetentionSettingsDto> GetRetentionAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Core.Dtos.Audit.AuditRetentionSettingsDto> SetRetentionAsync(int retentionDays, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<int> PurgeAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class AskiKapsami : AuditSubjectTests.TemelKapsam
    {
        public override Task<CustomerScope> GetScopeAsync(Guid userId, CancellationToken cancellationToken = default)
            => Task.FromResult(new CustomerScope(["ornek-site"], 10, 0, 0, 0, 0));

        public override Task SuspendAsync(Guid userId, string reason, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public override Task ResumeAsync(Guid userId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class SahteIstatistik : IWebStatisticsService
    {
        public Task<WebStatisticsDto> GetSiteStatisticsAsync(string siteName, int days, CancellationToken cancellationToken = default)
            => Task.FromResult(new WebStatisticsDto { SiteName = siteName });
    }

    private sealed class SahteSiteKontrol : IIisSiteService
    {
        public Task<SiteDto> StartSiteAsync(string siteName, CancellationToken cancellationToken = default)
            => Task.FromResult(new SiteDto { Name = siteName });

        public Task<SiteDto> StopSiteAsync(string siteName, CancellationToken cancellationToken = default)
            => Task.FromResult(new SiteDto { Name = siteName });

        public Task<IReadOnlyCollection<SiteDto>> GetSitesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDto> GetSiteAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDto> CreateSiteAsync(CreateSiteRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDto> UpdatePhysicalPathAsync(string siteName, string physicalPath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteSiteAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDto> AddBindingAsync(string siteName, SiteBindingRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDto> RemoveBindingAsync(string siteName, RemoveSiteBindingRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<SiteDto>> ImportExistingSitesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
