using AdaPanel.Core.Dtos.Audit;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Dtos.Statistics;
using AdaPanel.Core.Entities;
using AdaPanel.Core.Interfaces;
using AdaPanel.Core.Enums;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Security;
using AdaPanel.Infrastructure.Subscriptions;
using AdaPanel.Infrastructure.Support;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Askıya alma/kaldırma denetim kaydı, İSTENEN değil YAPILAN sayıyı yazar.
/// </summary>
/// <remarks>
/// <para>
/// <b>Ölçülen açık:</b> her iki yol da siteleri tek tek durduruyor/başlatıyor ve tek bir sitenin
/// başarısızlığını <c>catch</c> ile yutup devam ediyor — doğrusu da budur, bir site yüzünden askı
/// yarım kalmamalı. Ama denetim kaydı koşulsuz <c>scope.Sites.Count</c> yazıyordu, yani beş siteden
/// üçü durdurulamasa bile kayıt <c>"5 site(s) stopped"</c> diyordu.
/// </para>
/// <para>
/// Bedeli iki yönde de somut. <b>Askıya almada:</b> ödeme yapmayan müşterinin siteleri YAYINDA
/// kalır ve kayıt aksini söyler; yönetici arızayı ancak trafik faturası gelince görür.
/// <b>Askı kaldırmada:</b> parasını ödemiş müşterinin sitesi KAPALI kalır ve kayıt açıldığını
/// söyler; müşteri şikâyet eder, yönetici kayda bakar, kayıt "başladı" dediği için başka yerde arar.
/// </para>
/// <para>
/// Bu, bu deponun tekrar eden hata sınıfıdır: panel yapamadığı şeye "yaptım" der. Kardeş yol da
/// aynı anda düzeltildi — biri düzeltilip öteki bırakılsaydı, o da bu depoda defalarca ölçülen
/// ikinci kalıp olurdu.
/// </para>
/// </remarks>
public sealed class AskiDenetimKaydiDurustlukTests : IDisposable
{
    private readonly string kok;
    private readonly SqliteConnection baglanti;
    private readonly AdaPanelDbContext dbContext;
    private readonly ICustomerScopeService kapsamlar;
    private readonly YakalayanDenetim denetim = new();
    private readonly SecilenSiteyiKiranServis siteler = new();
    private readonly SubscriptionEnforcementService servis;

    public AskiDenetimKaydiDurustlukTests()
    {
        kok = Path.Combine(Path.GetTempPath(), "adapanel-aski-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(kok);

        baglanti = new SqliteConnection("Filename=:memory:");
        baglanti.Open();
        dbContext = new AdaPanelDbContext(
            new DbContextOptionsBuilder<AdaPanelDbContext>().UseSqlite(baglanti).Options);
        dbContext.Database.EnsureCreated();

        var store = new JsonFileStore(
            Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = kok }));
        kapsamlar = new CustomerScopeService(store, new PlansizServis(), new TavansizServis(), siteler);

        servis = new SubscriptionEnforcementService(
            kapsamlar,
            new IstatistiksizServis(),
            siteler,
            denetim,
            dbContext,
            NullLogger<SubscriptionEnforcementService>.Instance);
    }

    public void Dispose()
    {
        dbContext.Dispose();
        baglanti.Dispose();
        try { Directory.Delete(kok, recursive: true); } catch { /* temizlik zorunlu değil */ }
    }

    [Fact]
    public async Task Durdurulamayan_site_varsa_kayit_YAPILAN_sayiyi_ve_ADLARI_yazar()
    {
        var kullanici = Guid.NewGuid();
        await kapsamlar.SetScopeAsync(kullanici, ["a.test", "b.test", "c.test"], 5, 1024);
        siteler.Kiranlar.Add("b.test");

        await servis.SuspendAsync(kullanici, "traffic");

        var kayit = denetim.Son("subscription.suspended");
        Assert.Contains("2 of 3", kayit, StringComparison.Ordinal);
        Assert.Contains("STILL RUNNING", kayit, StringComparison.Ordinal);
        Assert.Contains("b.test", kayit, StringComparison.Ordinal);

        // BASARILI siteler "hâlâ yayinda" diye YAZILMAMALI. Bu, kaydin ikinci yalan sekli olurdu:
        // sayiyi duzeltip listeyi yanlis doldurmak, yoneticiyi calisan bir siteyi kovalamaya
        // gonderirdi.
        var uyariBolumu = kayit[kayit.IndexOf("STILL RUNNING", StringComparison.Ordinal)..];
        Assert.DoesNotContain("a.test", uyariBolumu, StringComparison.Ordinal);
        Assert.DoesNotContain("c.test", uyariBolumu, StringComparison.Ordinal);

        // NOT: burada "3 site(s) stopped" alt dizesi YASAKLANAMAZ -- dogru mesajin kendisi
        // ("2 of 3 site(s) stopped; ...") onu icerir. Ilk yazimim tam da bunu yapti ve DOGRU
        // davranisi kirmizi yakti; olculmesi gereken sey alt dize degil, RAPOR EDILEN SAYI.
    }

    /// <summary>KARDES YOL: aski kaldirma da ayni durustlugu uygular.</summary>
    [Fact]
    public async Task Baslatilamayan_site_varsa_kayit_YAPILAN_sayiyi_yazar()
    {
        var kullanici = Guid.NewGuid();
        await kapsamlar.SetScopeAsync(kullanici, ["a.test", "b.test"], 5, 1024);
        siteler.Kiranlar.Add("a.test");

        await servis.ResumeAsync(kullanici);

        var kayit = denetim.Son("subscription.resumed");
        Assert.Contains("1 of 2", kayit, StringComparison.Ordinal);
        Assert.Contains("STILL STOPPED", kayit, StringComparison.Ordinal);
        Assert.Contains("a.test", kayit, StringComparison.Ordinal);
    }

    /// <summary>
    /// FAZLA KAPATMA NÖBETÇİSİ: her şey yolundayken kayıt SADE kalmalı.
    /// </summary>
    /// <remarks>
    /// "Her zaman uyarı yaz" da yukarıdaki iki testi geçerdi ve her başarılı askıyı şüpheli
    /// gösterirdi. Sürekli uyaran bir kayıt, uyarmayan kadar kullanışsızdır.
    /// </remarks>
    [Fact]
    public async Task Hepsi_basariliysa_kayit_uyari_ICERMEZ()
    {
        var kullanici = Guid.NewGuid();
        await kapsamlar.SetScopeAsync(kullanici, ["a.test", "b.test"], 5, 1024);

        await servis.SuspendAsync(kullanici, "traffic");

        var kayit = denetim.Son("subscription.suspended");
        Assert.Contains("2 site(s) stopped", kayit, StringComparison.Ordinal);
        Assert.DoesNotContain("STILL RUNNING", kayit, StringComparison.Ordinal);
    }

    // --- duzenek ---

    private sealed class YakalayanDenetim : IAuditService
    {
        private readonly List<(string Eylem, string Ayrinti)> kayitlar = [];

        public string Son(string eylem) =>
            kayitlar.Where(k => k.Eylem == eylem).Select(k => k.Ayrinti).LastOrDefault()
            ?? throw new InvalidOperationException($"'{eylem}' kaydi hic yazilmadi.");

        public Task WriteForSubscriptionAsync(string action, string resource, string details, Guid? userId, Guid? subscriptionUserId, string? ipAddress, CancellationToken cancellationToken = default)
        {
            kayitlar.Add((action, details));
            return Task.CompletedTask;
        }

        public Task WriteAsync(string action, string resource, string details, Guid? userId, string? ipAddress, CancellationToken cancellationToken = default)
        {
            kayitlar.Add((action, details));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyCollection<AuditLogDto>> GetRecentAsync(int take, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AuditLogPageDto> SearchAsync(AuditLogQuery query, AuditVisibility visibility, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<AuditLogDto> StreamAsync(AuditLogQuery query, AuditVisibility visibility, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AuditVisibility> ResolveVisibilityAsync(Guid userId, UserRole role, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AuditRetentionSettingsDto> GetRetentionAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AuditRetentionSettingsDto> SetRetentionAsync(int retentionDays, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<int> PurgeAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    /// <summary>Adi listede olan siteyi durdururken/baslatirken FIRLATIR.</summary>
    private sealed class SecilenSiteyiKiranServis : IIisSiteService
    {
        public HashSet<string> Kiranlar { get; } = new(StringComparer.OrdinalIgnoreCase);

        private Task<SiteDto> BelkiKir(string siteName) =>
            Kiranlar.Contains(siteName)
                ? throw new InvalidOperationException($"site islenemedi: {siteName}")
                : Task.FromResult(new SiteDto { Name = siteName });

        public Task<SiteDto> StopSiteAsync(string siteName, CancellationToken cancellationToken = default) => BelkiKir(siteName);
        public Task<SiteDto> StartSiteAsync(string siteName, CancellationToken cancellationToken = default) => BelkiKir(siteName);

        public Task<IReadOnlyCollection<SiteDto>> GetSitesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyCollection<SiteDto>>([]);
        public Task<SiteDto> GetSiteAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDto> CreateSiteAsync(CreateSiteRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDto> UpdatePhysicalPathAsync(string siteName, string physicalPath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteSiteAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDto> AddBindingAsync(string siteName, SiteBindingRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDto> RemoveBindingAsync(string siteName, RemoveSiteBindingRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<SiteDto>> ImportExistingSitesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class IstatistiksizServis : IWebStatisticsService
    {
        public Task<WebStatisticsDto> GetSiteStatisticsAsync(string siteName, int days, CancellationToken cancellationToken = default)
            => Task.FromResult(new WebStatisticsDto());
    }

    private sealed class PlansizServis : IServicePlanService
    {
        public Task<Core.Dtos.Users.ServicePlanDto?> GetPlanAsync(string id, CancellationToken cancellationToken = default)
            => Task.FromResult<Core.Dtos.Users.ServicePlanDto?>(null);
        public Task<IReadOnlyCollection<Core.Dtos.Users.ServicePlanDto>> GetPlansAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyCollection<Core.Dtos.Users.ServicePlanDto>>([]);
        public Task<Core.Dtos.Users.ServicePlanDto> CreatePlanAsync(Core.Dtos.Users.ServicePlanRequest request, Guid? ownerId = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Core.Dtos.Users.ServicePlanDto> UpdatePlanAsync(string id, Core.Dtos.Users.ServicePlanRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeletePlanAsync(string id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class TavansizServis : IResellerQuotaService
    {
        public Task<Core.Dtos.Users.ResellerQuotaDto?> GetQuotaAsync(Guid resellerId, CancellationToken cancellationToken = default)
            => Task.FromResult<Core.Dtos.Users.ResellerQuotaDto?>(null);
        public Task<Core.Dtos.Users.ResellerQuotaDto> SetQuotaAsync(Guid resellerId, Core.Dtos.Users.ResellerQuotaRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RemoveQuotaAsync(Guid resellerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
