using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Dtos.Scheduler;
using AdaPanel.Core.Dtos.Users;
using AdaPanel.Core.Entities;
using AdaPanel.Core.Interfaces;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Bayi (Reseller), müşterisine YALNIZ kendi kapsamındaki siteleri atayabilir.
/// <para>
/// Bu, panelin en ağır kiracı kaçışıydı: atama listesinde yalnızca site SAYISI denetleniyordu,
/// sitelerin KİME ait olduğu hiç sorulmuyordu. Bayi, sunucudaki herhangi bir sitenin adını
/// müşterisinin listesine yazınca o site bayinin etkin kapsamına giriyordu
/// (GetResellerSitesAsync = kendi siteleri + sahip olduğu müşterilerin siteleri) — ardından
/// dosyalar, veritabanı, mail ve SİLME dahil her şey açılıyordu.
/// </para>
/// </summary>
public sealed class ResellerSiteAssignmentTests
{
    [Fact]
    public async Task Bayi_BASKASININ_sitesini_musterisine_atayamaz()
    {
        using var duzenek = Duzenek.Kur(bayiSiteleri: ["bayinin-sitesi"]);

        var hata = await Assert.ThrowsAsync<AppException>(() =>
            duzenek.Servis.CreateUserAsync(
                Istek("kurban-sitesi"),
                duzenek.BayiId,
                ipAddress: null,
                CancellationToken.None));

        Assert.Equal("users.site_forbidden", hata.MessageKey);
        Assert.Equal(403, hata.HttpStatusCode);
        Assert.Empty(duzenek.Kapsam.YazilanKapsamlar); // kapsam HİÇ yazılmamalı
    }

    [Fact]
    public async Task Bayi_KENDI_sitesini_musterisine_atayabilir()
    {
        using var duzenek = Duzenek.Kur(bayiSiteleri: ["bayinin-sitesi"]);

        await duzenek.Servis.CreateUserAsync(
            Istek("bayinin-sitesi"),
            duzenek.BayiId,
            ipAddress: null,
            CancellationToken.None);

        // Meşru akış BOZULMAMALI: kapsam gerçekten yazılmış olmalı.
        var yazilan = Assert.Single(duzenek.Kapsam.YazilanKapsamlar);
        Assert.Equal(["bayinin-sitesi"], yazilan.Siteler);
    }

    [Fact]
    public async Task Bayi_kismen_yabanci_liste_gonderirse_TAMAMI_reddedilir()
    {
        // Tek bir yabancı ad bile isteği düşürmeli; "iyi olanları al, kötüleri at" davranışı
        // saldırganın deneme yanılmayla kapsam büyütmesine izin verirdi.
        using var duzenek = Duzenek.Kur(bayiSiteleri: ["bayinin-sitesi"]);

        await Assert.ThrowsAsync<AppException>(() =>
            duzenek.Servis.CreateUserAsync(
                Istek("bayinin-sitesi", "kurban-sitesi"),
                duzenek.BayiId,
                ipAddress: null,
                CancellationToken.None));

        Assert.Empty(duzenek.Kapsam.YazilanKapsamlar);
    }

    [Fact]
    public async Task Yonetici_her_siteyi_atayabilir()
    {
        using var duzenek = Duzenek.Kur(bayiSiteleri: []);

        await duzenek.Servis.CreateUserAsync(
            Istek("herhangi-bir-site"),
            duzenek.YoneticiId,
            ipAddress: null,
            CancellationToken.None);

        var yazilan = Assert.Single(duzenek.Kapsam.YazilanKapsamlar);
        Assert.Equal(["herhangi-bir-site"], yazilan.Siteler);
    }

    private static CreateUserRequest Istek(params string[] siteler) => new()
    {
        UserName = $"musteri-{Guid.NewGuid():N}"[..20],
        Email = $"{Guid.NewGuid():N}@ornek.test",
        Password = "Cok-Guclu-Parola-123!",
        Role = "Customer",
        AssignedSites = siteler,
        SendWelcomeEmail = false
    };

    // --- düzenek ---

    private sealed class Duzenek : IDisposable
    {
        private readonly SqliteConnection baglanti;
        private readonly AdaPanelDbContext dbContext;

        private Duzenek(SqliteConnection baglanti, AdaPanelDbContext dbContext, UserManagementService servis, KayitEdenKapsam kapsam, Guid bayiId, Guid yoneticiId)
        {
            this.baglanti = baglanti;
            this.dbContext = dbContext;
            Servis = servis;
            Kapsam = kapsam;
            BayiId = bayiId;
            YoneticiId = yoneticiId;
        }

        public UserManagementService Servis { get; }
        public KayitEdenKapsam Kapsam { get; }
        public Guid BayiId { get; }
        public Guid YoneticiId { get; }

        public static Duzenek Kur(string[] bayiSiteleri)
        {
            var baglanti = new SqliteConnection("Filename=:memory:");
            baglanti.Open();
            var options = new DbContextOptionsBuilder<AdaPanelDbContext>().UseSqlite(baglanti).Options;
            var dbContext = new AdaPanelDbContext(options);
            dbContext.Database.EnsureCreated();

            var bayi = new PanelUser
            {
                UserName = "bayi",
                Email = "bayi@ornek.test",
                Role = Core.Enums.UserRole.Reseller,
                PasswordHash = "x",
                IsActive = true
            };
            var yonetici = new PanelUser
            {
                UserName = "yonetici",
                Email = "yonetici@ornek.test",
                Role = Core.Enums.UserRole.Administrator,
                PasswordHash = "x",
                IsActive = true
            };
            dbContext.Users.AddRange(bayi, yonetici);
            dbContext.SaveChanges();

            var kapsam = new KayitEdenKapsam(bayiSiteleri);
            var servis = new UserManagementService(
                dbContext,
                new PasswordHasher<PanelUser>(),
                kapsam,
                new ThrowingNotificationService(),
                new ThrowingPreferencesService(),
                new ThrowingSecurityService(),
                NullLogger<UserManagementService>.Instance,
                new RecordingAuditService(),
                new KullanilmayanSiteServisi(),
                new KullanilmayanDbSahiplik(),
                new KullanilmayanZamanlayici(),
                new KullanilmayanCascade(),
                new ThrowingApiTokenService());

            return new Duzenek(baglanti, dbContext, servis, kapsam, bayi.Id, yonetici.Id);
        }

        public void Dispose()
        {
            dbContext.Dispose();
            baglanti.Dispose();
        }
    }

    /// <summary>Kapsam yazımlarını KAYDEDER; testin "hiç yazılmadı"yı doğrulaması buna dayanıyor.</summary>
    internal sealed class KayitEdenKapsam(string[] bayiSiteleri) : ICustomerScopeService
    {
        public List<(Guid UserId, string[] Siteler)> YazilanKapsamlar { get; } = [];

        public Task SetScopeAsync(Guid userId, IEnumerable<string> siteNames, int maxSites, int maxDiskMb, CancellationToken cancellationToken = default)
        {
            YazilanKapsamlar.Add((userId, siteNames.ToArray()));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyCollection<string>> GetResellerSitesAsync(Guid resellerId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyCollection<string>>(bayiSiteleri);

        public Task<CustomerScope> GetScopeAsync(Guid userId, CancellationToken cancellationToken = default)
            => Task.FromResult(new CustomerScope(
                YazilanKapsamlar.LastOrDefault().Siteler ?? [], 0, 0, 0, 0, 0));

        public Task SetExtraLimitsAsync(Guid userId, int maxDatabases, int maxMailboxes, int maxScheduledTasks, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetOwnerAsync(Guid userId, Guid? ownerId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<Guid?> GetOwnerAsync(Guid userId, CancellationToken cancellationToken = default) => Task.FromResult<Guid?>(null);
        public Task<Guid?> GetParentSubscriptionAsync(Guid userId, CancellationToken cancellationToken = default) => Task.FromResult<Guid?>(null);
        /// <summary>
        /// Disk ölçümünün SONUCU testten verilir. Varsayılan "ölçülmüş sıfır"dır; kullanıcı
        /// listesinin eksik ölçümü bildirdiğini ölçen test (<c>DiskUsageMeasurementTests</c>)
        /// buraya kesilmiş bir ölçüm koyar.
        /// </summary>
        public DiskUsage DiskKullanimi { get; set; } = new(0, true, string.Empty);

        public Task<DiskUsage> GetDiskUsageMbAsync(Guid userId, CancellationToken cancellationToken = default)
            => Task.FromResult(DiskKullanimi);
        public Task<IReadOnlyCollection<string>> GetAssignedSitesAsync(Guid userId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyCollection<string>>([]);

        public Task RemoveUserAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RemoveSiteFromAllScopesAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<string>> GetAllowedMailDomainsAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<Guid>> GetOwnedUserIdsAsync(Guid ownerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<string>> GetResellerMailDomainsAsync(Guid resellerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Guid?> GetSiteOwnerUserIdAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetParentSubscriptionAsync(Guid userId, Guid? parentUserId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<Guid>> GetSubUserIdsAsync(Guid parentUserId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyCollection<Guid>>([]);
        public Task SetSubUserSectionsAsync(Guid userId, IReadOnlyCollection<string>? sections, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<string>?> GetSubUserSectionsAsync(Guid userId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyCollection<string>?>(null);

        // --- Abonelik iş modeli (bu testlerde kullanılmıyor) ---
        public Task SetPlanAsync(Guid userId, string? planId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetExpiryAsync(Guid userId, DateTime? expiresAtUtc, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SuspendAsync(Guid userId, string reason, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ResumeAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<Guid>> GetSuspendedUserIdsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<Guid>> GetSubscriptionUserIdsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    // Bu yolda çağrılmazlar; çağrılırlarsa test yanlış kurulmuş demektir.
    internal sealed class KullanilmayanSiteServisi : IIisSiteService
    {
        public Task<IReadOnlyCollection<SiteDto>> GetSitesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
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

    internal sealed class KullanilmayanDbSahiplik : IDatabaseOwnershipService
    {
        public Task RecordAsync(string engine, string databaseName, Guid ownerUserId, string? siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RemoveAsync(string engine, string databaseName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RemoveBySiteAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<int> CountForOwnerAsync(Guid ownerUserId, CancellationToken cancellationToken = default) => Task.FromResult(0);
        public Task<Guid?> GetOwnerAsync(string engine, string databaseName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<(string Engine, string DatabaseName)>> GetOwnedAsync(Guid ownerUserId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<(string Engine, string DatabaseName, Guid OwnerUserId, string SiteName)>> GetAllAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    internal sealed class KullanilmayanCascade : ISiteCascadeDeletionService
    {
        public Task<SiteDeletionPlanDto> GetPlanAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDeletionResultDto> DeleteAsync(string siteName, bool deleteFiles, bool deleteDatabases, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    internal sealed class KullanilmayanZamanlayici : ISchedulerService
    {
        public Task<IReadOnlyCollection<ScheduledTaskDto>> GetTasksAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ScheduledTaskDto> CreateTaskAsync(CreateScheduledTaskRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteTaskAsync(string name, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RunTaskAsync(string name, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ScheduledTaskStatusDto> GetTaskStatusAsync(string name, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ScheduledTaskOutputDto> GetTaskOutputAsync(string name, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TaskEnvironmentDto> GetEnvironmentAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MachinePathMeasurement> AddPythonToMachinePathAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetTaskEnabledAsync(string name, bool enabled, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<StopScheduledTaskResultDto> StopTaskAsync(string name, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<int> RepairTaskSettingsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<int> RemoveTasksUnderRootAsync(string siteRootPath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
