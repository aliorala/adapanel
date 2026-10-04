using System.Security.Claims;
using AdaPanel.Api.Controllers;
using AdaPanel.Api.Security;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Audit;
using AdaPanel.Core.Dtos.Backup;
using AdaPanel.Core.Enums;
using AdaPanel.Core.Entities;
using AdaPanel.Core.Interfaces;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.RunningJobs;
using AdaPanel.Infrastructure.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// KIRACI ↔ VERITABANI KAPSAMI.
/// </summary>
/// <remarks>
/// <para>
/// OLCULEN ACIK: <c>POST /api/v1/backup/site</c> yalniz <c>SiteName</c>'i denetliyordu.
/// <c>BackupSiteRequest.Databases</c> ise GOVDEDEN geliyor ve <c>BackupService</c> onu dogrudan
/// <c>ExportDumpAsync</c>'e veriyor -- dokum panelin KOK kimligiyle alinir, yani sunucudaki HER
/// veritabani erisilebilir. Kiraci KENDI sitesinin adini, govdede BASKASININ veritabanini verip
/// olusan arsivi <c>GET /api/v1/backup/{id}/download</c> ile indirebiliyordu; indirme denetimi
/// arsivi kendi sitesinin yedegi gordugu icin geciyordu.
/// </para>
/// <para>
/// Bu, deponun bilinen hata sinifinin veritabani hali: <b>kapsam denetimi dogru ama denetlenen
/// degeri kiraci seciyor.</b> Kural artik tek adreste
/// (<see cref="CustomerAccess.ResolveDatabaseScopeAsync"/>) ve site↔veritabani baglama ucuyla
/// AYNI kaynagi kullaniyor; iki kopya olsaydi biri duzelir, oteki sessizce acik kalirdi.
/// </para>
/// </remarks>
public sealed class TenantDatabaseScopeTests : IDisposable
{
    private readonly List<AdaPanelDbContext> actorContexts = [];
    public void Dispose() { foreach (var context in actorContexts) context.Dispose(); }
    private static readonly Guid Kiraci = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid Kurban = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

    // ---------------------------------------------------------------------------------------
    // KURAL (CustomerAccess)
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Baskasinin_veritabani_REDDEDILIR()
    {
        var hata = await Assert.ThrowsAsync<AppException>(() => Denetle("mariadb", "kurban_wp"));
        Assert.Equal("database.not_owned", hata.MessageKey);
        Assert.Equal(403, hata.HttpStatusCode);
    }

    [Fact]
    public async Task Sahiplik_kaydi_olan_KENDI_veritabani_gecer()
    {
        // FAZLA-KAPATMA NOBETCISI: kiraciyi kendi verisinden koparmak acigin kendisinden kotudur.
        await Denetle("mariadb", "benim_wp");
    }

    [Fact]
    public async Task Site_ADIYLA_eslesen_veritabani_gecer()
    {
        // Panel disinda (ya da sahiplik kaydi tutulmadan once) acilmis veritabanlarinin kaydi
        // yoktur. Yalniz kayda bakmak bugun calisan mesru yedegi kirardi.
        await Denetle("mariadb", "benim-site");
    }

    [Fact]
    public async Task Site_adindan_TURETILEN_veritabani_adi_gecer()
    {
        await Denetle("mariadb", ResourceNaming.DatabaseNameFromSite("benim-site"));
    }

    [Fact]
    public async Task Sahiplik_kaydinda_MOTOR_da_eslesmeli()
    {
        // Ayni ad baska motorda BASKASININ olabilir: MySQL'de "test"i olan birine PostgreSQL'deki
        // "test" acilmamali. (Ad eslesmesi kurali motor ayrimi yapmaz -- o kural yalniz sitenin
        // KENDI adi icindir; burada ad site adiyla eslesmiyor.)
        var hata = await Assert.ThrowsAsync<AppException>(() => Denetle("postgresql", "benim_wp"));
        Assert.Equal("database.not_owned", hata.MessageKey);
    }

    [Fact]
    public async Task Yonetici_denetime_takilmaz()
    {
        await Denetle("mariadb", "kurban_wp", rol: "Administrator");
    }

    [Fact]
    public async Task Bayi_kendi_musterisinin_veritabanina_erisir()
    {
        await Denetle("mariadb", "kurban_wp", rol: "Reseller");
    }

    // ---------------------------------------------------------------------------------------
    // ASIL REGRESYON (BackupController): govdedeki veritabani listesi
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Site_yedegi_GOVDEDEN_baskasinin_veritabanini_ALAMAZ()
    {
        var yedekServisi = new IstekYakalayanYedekServisi();
        var denetleyici = Denetleyici(yedekServisi, "Customer");

        var hata = await Assert.ThrowsAsync<AppException>(() => denetleyici.BackupSiteAsync(
            new BackupSiteRequest
            {
                SiteName = "benim-site",
                Databases = [new BackupDatabaseRef { Engine = "mariadb", DatabaseName = "kurban_wp" }]
            },
            CancellationToken.None));

        Assert.Equal("database.not_owned", hata.MessageKey);

        // ISIN HIC BASLAMAMIS OLMASI SART: 403 dondurulup dokum yine de alinsaydi, kurbanin
        // verisi diskteki arsive girmis olurdu ve arsiv saldirganin sitesine ait sayilirdi.
        Assert.Null(yedekServisi.SonIstek);
    }

    [Fact]
    public async Task Site_yedegi_KENDI_veritabanini_ALMAYA_devam_eder()
    {
        // FAZLA-KAPATMA NOBETCISI.
        var yedekServisi = new IstekYakalayanYedekServisi();
        var denetleyici = Denetleyici(yedekServisi, "Customer");

        await denetleyici.BackupSiteAsync(
            new BackupSiteRequest
            {
                SiteName = "benim-site",
                Databases = [new BackupDatabaseRef { Engine = "mariadb", DatabaseName = "benim_wp" }]
            },
            CancellationToken.None);

        Assert.NotNull(yedekServisi.SonIstek);
        Assert.Equal("benim_wp", Assert.Single(yedekServisi.SonIstek!.Databases).DatabaseName);
    }

    [Fact]
    public async Task Yonetici_site_yedeginde_her_veritabanini_alabilir()
    {
        var yedekServisi = new IstekYakalayanYedekServisi();
        var denetleyici = Denetleyici(yedekServisi, "Administrator");

        await denetleyici.BackupSiteAsync(
            new BackupSiteRequest
            {
                SiteName = "kurban-site",
                Databases = [new BackupDatabaseRef { Engine = "mariadb", DatabaseName = "kurban_wp" }]
            },
            CancellationToken.None);

        Assert.NotNull(yedekServisi.SonIstek);
    }

    [Fact]
    public async Task Veritabanisiz_site_yedegi_calisir()
    {
        // FAZLA-KAPATMA NOBETCISI: bos liste (en sik kullanim) denetime takilmamali.
        var yedekServisi = new IstekYakalayanYedekServisi();
        var denetleyici = Denetleyici(yedekServisi, "Customer");

        await denetleyici.BackupSiteAsync(
            new BackupSiteRequest { SiteName = "benim-site" },
            CancellationToken.None);

        Assert.NotNull(yedekServisi.SonIstek);
    }

    // --- duzenek ---

    private static Task Denetle(string motor, string veritabani, string rol = "Customer") =>
        CustomerAccess.EnsureDatabaseAsync(
            new SahteKapsam(), new SahteSahiplik(), Kullanici(rol), motor, veritabani, CancellationToken.None);

    private static ClaimsPrincipal Kullanici(string rol) => new(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, Kiraci.ToString()),
            new Claim(ClaimTypes.Role, rol)
        ],
        authenticationType: "Test",
        nameType: ClaimTypes.Name,
        roleType: ClaimTypes.Role));

    private BackupController Denetleyici(IBackupService yedekServisi, string rol)
    {
        var isKoku = Path.Combine(Path.GetTempPath(), $"adapanel-dbscope-{Guid.NewGuid():N}");
        var calisanIsler = new RunningJobService(
            new JsonFileStore(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = isKoku })),
            NullLogger<RunningJobService>.Instance);

        var actorDb = new AdaPanelDbContext(new DbContextOptionsBuilder<AdaPanelDbContext>().UseSqlite("Data Source=:memory:").Options);
        actorContexts.Add(actorDb);
        actorDb.Database.OpenConnection();
        actorDb.Database.EnsureCreated();
        actorDb.Users.Add(new PanelUser { Id = Kiraci, UserName = "scope-actor", Email = "scope@example.test",
            Role = Enum.Parse<UserRole>(rol), PasswordHash = "fixture", IsActive = true });
        actorDb.SaveChanges();
        return new BackupController(
            yedekServisi,
            new SahteKapsam(),
            new SahteSahiplik(),
            new KullanilmayanProgram(),
            calisanIsler,
            new SessizDenetim(),
            actorDb)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = Kullanici(rol) }
            }
        };
    }

    /// <summary>Yedek servisi CAGRILDI MI, hangi istekle: acigin olculdugu yer tam burasi.</summary>
    private sealed class IstekYakalayanYedekServisi : IBackupService
    {
        public BackupSiteRequest? SonIstek { get; private set; }

        public Task<BackupRecordDto> BackupSiteAsync(BackupSiteRequest request, CancellationToken cancellationToken = default)
        {
            SonIstek = request;
            return Task.FromResult(new BackupRecordDto
            {
                Id = "b1",
                Name = "b1.zip",
                Type = "site",
                Source = request.SiteName,
                Status = "created",
                Format = "zip"
            });
        }

        public Task<BackupRecordDto> RetryRemoteUploadAsync(string id, string url, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyCollection<BackupRecordDto>> GetBackupsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyCollection<BackupRecordDto>>([]);

        public Task<BackupRecordDto> BackupSubscriptionAsync(BackupSubscriptionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BackupRecordDto> BackupDatabaseAsync(BackupDatabaseRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BackupRecordDto> BackupMailAsync(BackupMailRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BackupRecordDto> BackupPanelAsync(BackupPanelRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BackupRecordDto> RestoreAsync(RestoreBackupRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<RestoreBackupItemsResultDto> RestoreItemsAsync(RestoreBackupItemsRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BackupEntriesDto> GetEntriesAsync(string backupId, string? prefix, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BackupPruneResultDto> PruneAsync(int retentionDays, BackupPruneRemoteOptions? remote = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BackupRemoteTestResultDto> TestRemoteTargetAsync(BackupRemoteTestRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BackupVerificationResultDto> VerifyBackupsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BackupPruneResultDto> PruneSubscriptionAsync(Guid userId, int retentionDays, int keepCount, BackupPruneRemoteOptions? remote = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BackupChainDto> GetChainAsync(string backupId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteBackupAsync(string backupId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BackupDeleteResultDto> DeleteBackupChainAsync(string backupId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BackupEncryptionSettingsDto> GetEncryptionSettingsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BackupEncryptionSettingsDto> SetEncryptionSettingsAsync(SetBackupEncryptionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    /// <summary>"benim-site" kiracinin, "kurban-site" kurbanin; bayi kurbanin sahibidir.</summary>
    private sealed class SahteKapsam : ICustomerScopeService
    {
        public Task<IReadOnlyCollection<string>> GetAssignedSitesAsync(Guid userId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyCollection<string>>(["benim-site"]);

        public Task<IReadOnlyCollection<string>> GetResellerSitesAsync(Guid resellerId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyCollection<string>>(["benim-site"]);

        public Task<Guid?> GetParentSubscriptionAsync(Guid userId, CancellationToken cancellationToken = default)
            => Task.FromResult<Guid?>(null);

        public Task<IReadOnlyCollection<Guid>> GetOwnedUserIdsAsync(Guid ownerId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyCollection<Guid>>([Kurban]);

        public Task<Guid?> GetSiteOwnerUserIdAsync(string siteName, CancellationToken cancellationToken = default)
            => Task.FromResult<Guid?>(siteName == "benim-site" ? Kiraci : Kurban);

        public Task<IReadOnlyCollection<string>?> GetSubUserSectionsAsync(Guid userId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyCollection<string>?>(null);

        public Task<IReadOnlyCollection<string>> GetAllowedMailDomainsAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CustomerScope> GetScopeAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetScopeAsync(Guid userId, IEnumerable<string> siteNames, int maxSites, int maxDiskMb, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetExtraLimitsAsync(Guid userId, int maxDatabases, int maxMailboxes, int maxScheduledTasks, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RemoveUserAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RemoveSiteFromAllScopesAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DiskUsage> GetDiskUsageMbAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetOwnerAsync(Guid userId, Guid? ownerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Guid?> GetOwnerAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<string>> GetResellerMailDomainsAsync(Guid resellerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetParentSubscriptionAsync(Guid userId, Guid? parentUserId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<Guid>> GetSubUserIdsAsync(Guid parentUserId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetSubUserSectionsAsync(Guid userId, IReadOnlyCollection<string>? sections, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetPlanAsync(Guid userId, string? planId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetExpiryAsync(Guid userId, DateTime? expiresAtUtc, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SuspendAsync(Guid userId, string reason, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ResumeAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<Guid>> GetSuspendedUserIdsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<Guid>> GetSubscriptionUserIdsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    /// <summary>"benim_wp" kiracinin, "kurban_wp" kurbanin.</summary>
    private sealed class SahteSahiplik : IDatabaseOwnershipService
    {
        public Task<IReadOnlyCollection<(string Engine, string DatabaseName)>> GetOwnedAsync(Guid ownerUserId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyCollection<(string, string)>>(
                ownerUserId == Kiraci ? [("mariadb", "benim_wp")] : [("mariadb", "kurban_wp")]);

        public Task RecordAsync(string engine, string databaseName, Guid ownerUserId, string? siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RemoveAsync(string engine, string databaseName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RemoveBySiteAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<int> CountForOwnerAsync(Guid ownerUserId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Guid?> GetOwnerAsync(string engine, string databaseName, CancellationToken cancellationToken = default) => Task.FromResult<Guid?>(null);
        public Task<IReadOnlyCollection<(string Engine, string DatabaseName, Guid OwnerUserId, string SiteName)>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyCollection<(string, string, Guid, string)>>([
                ("mariadb", "benim_wp", Kiraci, "benim-site"),
                ("mariadb", "kurban_wp", Kurban, "kurban-site")]);
    }

    private sealed class KullanilmayanProgram : ISubscriptionBackupScheduleService
    {
        public Task<IReadOnlyCollection<SubscriptionBackupScheduleDto>> GetSchedulesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SubscriptionBackupScheduleDto> GetScheduleAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SubscriptionBackupScheduleDto> UpdateScheduleAsync(Guid userId, UpdateSubscriptionBackupScheduleRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RemoveScheduleAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SubscriptionBackupScheduleDto> RunNowAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RunDueAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class SessizDenetim : IAuditService
    {
        public Task WriteAsync(string action, string resource, string details, Guid? userId, string? ipAddress, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task WriteForSubscriptionAsync(string action, string resource, string details, Guid? userId, Guid? subscriptionUserId, string? ipAddress, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyCollection<AuditLogDto>> GetRecentAsync(int take, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AuditLogPageDto> SearchAsync(AuditLogQuery query, AuditVisibility visibility, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<AuditLogDto> StreamAsync(AuditLogQuery query, AuditVisibility visibility, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AuditVisibility> ResolveVisibilityAsync(Guid userId, UserRole role, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AuditRetentionSettingsDto> GetRetentionAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AuditRetentionSettingsDto> SetRetentionAsync(int retentionDays, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<int> PurgeAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
