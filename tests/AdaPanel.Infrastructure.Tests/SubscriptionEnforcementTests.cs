using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Dtos.Statistics;
using AdaPanel.Core.Dtos.Users;
using AdaPanel.Core.Entities;
using AdaPanel.Core.Interfaces;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Subscriptions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Abonelik kurallarının UYGULANMASI: kota dolunca askıya alma, süre bitince askıya alma, sebep
/// kalkınca geri açma.
/// </summary>
/// <remarks>
/// Panelde limitler zaten tanımlanabiliyordu ama dolunca <b>hiçbir şey olmuyordu</b> — yani kota
/// bir iş modeli değil, süstü. Bu testler kuralın gerçekten işlediğini ve en az onun kadar
/// önemlisi <b>yanlışlıkla işlemediğini</b> (ölçülemeyen trafikte askıya almama) doğrular.
/// </remarks>
public sealed class SubscriptionEnforcementTests : IDisposable
{
    private static readonly Guid Musteri = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private readonly SqliteConnection baglanti;
    private readonly AdaPanelDbContext dbContext;

    public SubscriptionEnforcementTests()
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
    public async Task Trafik_kotasi_dolunca_ASKIYA_ALINIR_ve_siteler_DURDURULUR()
    {
        // 2 GB kota, 3 GB kullanım.
        var kapsam = new SahteAbonelik(trafikGb: 2);
        var siteler = new SahteSiteKontrol();
        var servis = Servis(kapsam, siteler, baytPerSite: 3L * 1024 * 1024 * 1024);

        var degisenler = await servis.EnforceAllAsync();

        var degisim = Assert.Single(degisenler);
        Assert.Equal("suspended", degisim.Action);
        Assert.Equal("traffic", degisim.Reason);
        Assert.True(kapsam.Askida);
        Assert.Contains("ornek-site", siteler.Durdurulanlar);
    }

    [Fact]
    public async Task Kota_altindaki_abonelik_askiya_ALINMAZ()
    {
        var kapsam = new SahteAbonelik(trafikGb: 10);
        var servis = Servis(kapsam, new SahteSiteKontrol(), baytPerSite: 1L * 1024 * 1024 * 1024);

        Assert.Empty(await servis.EnforceAllAsync());
        Assert.False(kapsam.Askida);
    }

    [Fact]
    public async Task Trafik_OLCULEMEZSE_kota_uygulanmaz()
    {
        // ÖLÇEMEDİĞİMİZ İÇİN ASKIYA ALMAK, kimsenin aşmadığı bir kotadan dolayı müşteriyi
        // yayından kaldırmak olurdu. Günlük okunamıyorsa (IIS günlük tutmuyor, klasör erişilemez)
        // kural işlemez.
        var kapsam = new SahteAbonelik(trafikGb: 1);
        var servis = Servis(kapsam, new SahteSiteKontrol(), baytPerSite: 0, olcumPatlasin: true);

        Assert.Empty(await servis.EnforceAllAsync());
        Assert.False(kapsam.Askida);
    }

    [Fact]
    public async Task Suresi_dolan_abonelik_askiya_alinir()
    {
        var kapsam = new SahteAbonelik(trafikGb: 0, bitis: DateTime.UtcNow.AddDays(-1));
        var servis = Servis(kapsam, new SahteSiteKontrol(), baytPerSite: 0);

        var degisim = Assert.Single(await servis.EnforceAllAsync());
        Assert.Equal("suspended", degisim.Action);
        Assert.Equal("expired", degisim.Reason);
    }

    [Fact]
    public async Task Trafik_kota_altina_dusunce_KENDILIGINDEN_geri_acilir()
    {
        // Yeni ay başlayınca sayaç sıfırlanır; müşterinin yöneticiyi aramasına gerek kalmamalı.
        var kapsam = new SahteAbonelik(trafikGb: 2) { Askida = true, Sebep = "traffic" };
        var siteler = new SahteSiteKontrol();
        var servis = Servis(kapsam, siteler, baytPerSite: 100 * 1024 * 1024);

        var degisim = Assert.Single(await servis.EnforceAllAsync());
        Assert.Equal("resumed", degisim.Action);
        Assert.False(kapsam.Askida);
        Assert.Contains("ornek-site", siteler.Baslatilanlar);
    }

    [Fact]
    public async Task ELLE_askiya_alinmis_abonelik_KENDILIGINDEN_acilmaz()
    {
        // Yönetici bilerek kapattıysa (ödeme yok, kötüye kullanım) kota denetimi onu geri açmamalı.
        var kapsam = new SahteAbonelik(trafikGb: 2) { Askida = true, Sebep = "manual" };
        var servis = Servis(kapsam, new SahteSiteKontrol(), baytPerSite: 1024);

        Assert.Empty(await servis.EnforceAllAsync());
        Assert.True(kapsam.Askida);
    }

    /// <summary>
    /// ABONELİK EKRANI, DİSK SAYISININ EKSİK OLABİLECEĞİNİ SÖYLER — sayıyı tam gibi göstermez.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Bu ekran müşterinin "kotamın ne kadarı kaldı" sorusunu sorduğu yerdir ve aynı DTO faturalama
    /// raporuna da gider. Disk kullanımı bütçeli bir ağaç yürüyüşüyle ölçülür; bütçe dolduğunda
    /// dönen sayı bir ALT SINIRDIR. Eksik sayıyı "1,2 GB" diye yazmak, müşteriyi dolmak üzere olan
    /// bir kotayla "yerim var" sanmaya bırakırdı — bu depoda eksik sonucu tam göstermek yasaktır.
    /// </para>
    /// <para>
    /// Kardeşi <c>TrafficMeasured</c> ile aynı sözleşme: sayının yanında "ölçülebildi mi" de
    /// taşınır. Sebep anahtarı da geçer, çünkü çevirisi zaten olan
    /// <c>files.size_truncated_*</c> anahtarları kullanıcıya NEDEN eksik olduğunu söyleyebilir.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Abonelik_ekrani_OLCULEMEYEN_diski_BILDIRIR()
    {
        var kapsam = new SahteAbonelik(trafikGb: 10)
        {
            DiskKullanimi = new DiskUsage(1200, false, "files.size_truncated_entries")
        };
        var servis = Servis(kapsam, new SahteSiteKontrol(), baytPerSite: 0);

        var kullanim = await servis.GetUsageAsync(Musteri);

        // Sayı DÜŞÜRÜLMEZ (alt sınır bile kullanışlı bilgidir)...
        Assert.Equal(1200, kullanim.DiskUsageMb);

        // ...ama eksik olduğu ve NEDEN eksik olduğu söylenir.
        Assert.False(kullanim.DiskMeasured);
        Assert.Equal("files.size_truncated_entries", kullanim.DiskUsageTruncatedReasonKey);
    }

    /// <summary>MUTASYON KONTROLÜ: TAM ölçülen disk "eksik" damgalanmaz.</summary>
    /// <remarks>
    /// Bu olmadan "bayrağı hep false yaz" da yukarıdaki testi geçerdi; o hâlde her abonelik ekranı
    /// kalıcı bir uyarı gösterir ve uyarı, gerçekten eksik olan aboneliklerde anlamını yitirirdi.
    /// </remarks>
    [Fact]
    public async Task Tam_olculen_disk_EKSIK_damgalanmaz()
    {
        var kapsam = new SahteAbonelik(trafikGb: 10)
        {
            DiskKullanimi = new DiskUsage(512, true, string.Empty)
        };
        var servis = Servis(kapsam, new SahteSiteKontrol(), baytPerSite: 0);

        var kullanim = await servis.GetUsageAsync(Musteri);

        Assert.Equal(512, kullanim.DiskUsageMb);
        Assert.True(kullanim.DiskMeasured);
        Assert.Equal(string.Empty, kullanim.DiskUsageTruncatedReasonKey);
    }

    // --- düzenek ---

    private SubscriptionEnforcementService Servis(
        SahteAbonelik kapsam,
        SahteSiteKontrol siteler,
        long baytPerSite,
        bool olcumPatlasin = false)
        => new(
            kapsam,
            new SahteIstatistik(baytPerSite, olcumPatlasin),
            siteler,
            new RecordingAuditService(),
            dbContext,
            NullLogger<SubscriptionEnforcementService>.Instance);

    /// <summary>Tek bir aboneliği temsil eder; askı çağrılarını KAYDEDER.</summary>
    private sealed class SahteAbonelik(int trafikGb, DateTime? bitis = null) : ICustomerScopeService
    {
        public bool Askida { get; set; }
        public string Sebep { get; set; } = string.Empty;

        public Task<IReadOnlyCollection<Guid>> GetSubscriptionUserIdsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyCollection<Guid>>([Musteri]);

        public Task<CustomerScope> GetScopeAsync(Guid userId, CancellationToken cancellationToken = default)
            => Task.FromResult(new CustomerScope(
                ["ornek-site"], 10, 0, 0, 0, 0,
                MaxFtpUsers: 0,
                MaxTrafficGbPerMonth: trafikGb,
                PlanId: "plan-1",
                PlanName: "Başlangıç",
                ExpiresAtUtc: bitis,
                SuspendedAtUtc: Askida ? DateTime.UtcNow.AddHours(-1) : null,
                SuspendReason: Askida ? Sebep : string.Empty));

        public Task SuspendAsync(Guid userId, string reason, CancellationToken cancellationToken = default)
        {
            Askida = true;
            Sebep = reason;
            return Task.CompletedTask;
        }

        public Task ResumeAsync(Guid userId, CancellationToken cancellationToken = default)
        {
            Askida = false;
            Sebep = string.Empty;
            return Task.CompletedTask;
        }

        /// <summary>Disk ölçümünün SONUCU testten verilir: "ölçülemedi" hâli de bir senaryodur.</summary>
        public DiskUsage DiskKullanimi { get; set; } = new(0, true, string.Empty);

        public Task<DiskUsage> GetDiskUsageMbAsync(Guid userId, CancellationToken cancellationToken = default)
            => Task.FromResult(DiskKullanimi);

        public Task<IReadOnlyCollection<string>> GetAssignedSitesAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetScopeAsync(Guid userId, IEnumerable<string> siteNames, int maxSites, int maxDiskMb, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetExtraLimitsAsync(Guid userId, int maxDatabases, int maxMailboxes, int maxScheduledTasks, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RemoveUserAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RemoveSiteFromAllScopesAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<string>> GetAllowedMailDomainsAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetOwnerAsync(Guid userId, Guid? ownerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Guid?> GetOwnerAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<Guid>> GetOwnedUserIdsAsync(Guid ownerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<string>> GetResellerSitesAsync(Guid resellerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<string>> GetResellerMailDomainsAsync(Guid resellerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Guid?> GetSiteOwnerUserIdAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetParentSubscriptionAsync(Guid userId, Guid? parentUserId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Guid?> GetParentSubscriptionAsync(Guid userId, CancellationToken cancellationToken = default) => Task.FromResult<Guid?>(null);
        public Task<IReadOnlyCollection<Guid>> GetSubUserIdsAsync(Guid parentUserId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetSubUserSectionsAsync(Guid userId, IReadOnlyCollection<string>? sections, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<string>?> GetSubUserSectionsAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetPlanAsync(Guid userId, string? planId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetExpiryAsync(Guid userId, DateTime? expiresAtUtc, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<Guid>> GetSuspendedUserIdsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class SahteIstatistik(long baytPerSite, bool patlasin) : IWebStatisticsService
    {
        public Task<WebStatisticsDto> GetSiteStatisticsAsync(string siteName, int days, CancellationToken cancellationToken = default)
            => patlasin
                ? throw new IOException("günlük okunamadı")
                : Task.FromResult(new WebStatisticsDto { SiteName = siteName, TotalBytes = baytPerSite });
    }

    /// <summary>Site başlat/durdur çağrılarını KAYDEDER: askının gerçekten yayını kestiğini doğrulamak için.</summary>
    private sealed class SahteSiteKontrol : IIisSiteService
    {
        public List<string> Durdurulanlar { get; } = [];
        public List<string> Baslatilanlar { get; } = [];

        public Task<SiteDto> StopSiteAsync(string siteName, CancellationToken cancellationToken = default)
        {
            Durdurulanlar.Add(siteName);
            return Task.FromResult(new SiteDto { Name = siteName });
        }

        public Task<SiteDto> StartSiteAsync(string siteName, CancellationToken cancellationToken = default)
        {
            Baslatilanlar.Add(siteName);
            return Task.FromResult(new SiteDto { Name = siteName });
        }

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
