using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Dtos.Scheduler;
using AdaPanel.Core.Dtos.Users;
using AdaPanel.Core.Entities;
using AdaPanel.Core.Interfaces;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Services;
using AdaPanel.Infrastructure.Support;
using AdaPanel.Infrastructure.Options;
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rejected_create_does_not_persist_user_or_success_audit(bool forbiddenSite)
    {
        using var fixture = Duzenek.Kur(["owned"], persistingAudit: true);
        var request = new CreateUserRequest
        {
            UserName = "rejected-customer", Email = "rejected@example.test",
            Password = "Cok-Guclu-Parola-123!", Role = "Customer", SendWelcomeEmail = false,
            AssignedSites = forbiddenSite ? ["foreign"] : ["owned", "second"],
            MaxSites = forbiddenSite ? 0 : 1
        };
        var error = await Record.ExceptionAsync(() => fixture.Servis.CreateUserAsync(request,
            forbiddenSite ? fixture.BayiId : fixture.YoneticiId, null));
        if (forbiddenSite) Assert.Equal("users.site_forbidden", Assert.IsType<AppException>(error).MessageKey);
        else Assert.Equal("users.too_many_sites", Assert.IsType<InvalidOperationException>(error).Message);

        Assert.False(await fixture.Db.Users.AsNoTracking().AnyAsync(user => user.UserName == request.UserName));
        Assert.False(await fixture.Db.AuditLogs.AsNoTracking().AnyAsync(log => log.Action == "users.created"));
        Assert.Empty(fixture.Kapsam.YazilanKapsamlar);
        // A later unrelated save must not publish a rejected tracked entity either.
        await fixture.Db.SaveChangesAsync();
        Assert.False(await fixture.Db.Users.AsNoTracking().AnyAsync(user => user.UserName == request.UserName));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rejected_update_preserves_persisted_and_tracked_user_fields(bool forbiddenSite)
    {
        using var fixture = Duzenek.Kur(["owned"], persistingAudit: true);
        var user = new PanelUser { UserName = "existing", Email = "old@example.test",
            Role = Core.Enums.UserRole.Customer, PreferredLanguage = "tr", IsActive = true, PasswordHash = "x" };
        fixture.Db.Users.Add(user);
        await fixture.Db.SaveChangesAsync();
        fixture.Kapsam.OwnerId = fixture.BayiId;
        var request = new UpdateUserRequest { Email = "new@example.test", Role = "Customer",
            PreferredLanguage = "en", IsActive = false,
            AssignedSites = forbiddenSite ? ["foreign"] : ["owned", "second"],
            MaxSites = forbiddenSite ? 0 : 1 };
        var error = await Record.ExceptionAsync(() => fixture.Servis.UpdateUserAsync(user.Id, request,
            forbiddenSite ? fixture.BayiId : fixture.YoneticiId, null));
        if (forbiddenSite) Assert.Equal("users.site_forbidden", Assert.IsType<AppException>(error).MessageKey);
        else Assert.Equal("users.too_many_sites", Assert.IsType<InvalidOperationException>(error).Message);

        var saved = await fixture.Db.Users.AsNoTracking().SingleAsync(item => item.Id == user.Id);
        Assert.Equal("old@example.test", saved.Email);
        Assert.Equal("tr", saved.PreferredLanguage);
        Assert.True(saved.IsActive);
        Assert.False(await fixture.Db.AuditLogs.AsNoTracking().AnyAsync(log => log.Action == "users.updated"));
        Assert.Empty(fixture.Kapsam.YazilanKapsamlar);
        await fixture.Db.SaveChangesAsync();
        Assert.Equal("old@example.test", (await fixture.Db.Users.AsNoTracking().SingleAsync(item => item.Id == user.Id)).Email);
    }

    [Fact]
    public async Task Accepted_create_and_update_still_persist_with_real_audit_writer()
    {
        using var fixture = Duzenek.Kur(["owned"], persistingAudit: true);
        var created = await fixture.Servis.CreateUserAsync(Istek("owned"), fixture.BayiId, null);
        Assert.True(await fixture.Db.Users.AsNoTracking().AnyAsync(user => user.Id == created.Id));
        Assert.Single(await fixture.Db.AuditLogs.Where(log => log.Action == "users.created").ToListAsync());
        fixture.Kapsam.OwnerId = fixture.BayiId;
        await fixture.Servis.UpdateUserAsync(created.Id,
            new UpdateUserRequest { Email = "updated@example.test", Role = "Customer", AssignedSites = ["owned"], MaxSites = 1 },
            fixture.BayiId, null);
        Assert.Equal("updated@example.test", (await fixture.Db.Users.AsNoTracking().SingleAsync(user => user.Id == created.Id)).Email);
        Assert.Single(await fixture.Db.AuditLogs.Where(log => log.Action == "users.updated").ToListAsync());
        Assert.Equal(2, fixture.Kapsam.YazilanKapsamlar.Count);
    }

    [Fact]
    public async Task Additional_user_keeps_parent_scope_and_ignores_independent_site_limits()
    {
        using var fixture = Duzenek.Kur([], persistingAudit: true);
        var parent = new PanelUser { UserName = "parent", Email = "parent@example.test",
            Role = Core.Enums.UserRole.Customer, IsActive = true, PasswordHash = "x" };
        fixture.Db.Users.Add(parent);
        await fixture.Db.SaveChangesAsync();
        var created = await fixture.Servis.CreateUserAsync(new CreateUserRequest {
            UserName = "additional", Email = "additional@example.test", Password = "Cok-Guclu-Parola-123!",
            Role = "Customer", ParentUserId = parent.Id, AssignedSites = ["ignored-a", "ignored-b"], MaxSites = 1,
            SendWelcomeEmail = false }, fixture.YoneticiId, null);
        Assert.Equal(parent.Id, created.ParentUserId);
        Assert.Empty(fixture.Kapsam.YazilanKapsamlar);

        var updated = await fixture.Servis.UpdateUserAsync(created.Id, new UpdateUserRequest {
            Email = "additional-new@example.test", Role = "Customer", IsActive = false,
            AssignedSites = ["ignored-a", "ignored-b"], MaxSites = 1 }, fixture.YoneticiId, null);
        Assert.Equal(parent.Id, updated.ParentUserId);
        Assert.False(updated.IsActive);
        Assert.Empty(fixture.Kapsam.YazilanKapsamlar);
        Assert.True((await fixture.Db.Users.AsNoTracking().SingleAsync(user => user.Id == parent.Id)).IsActive);
        Assert.False((await fixture.Db.Users.AsNoTracking().SingleAsync(user => user.Id == created.Id)).IsActive);
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

        public AdaPanelDbContext Db => dbContext;
        public UserManagementService Servis { get; }
        public KayitEdenKapsam Kapsam { get; }
        public Guid BayiId { get; }
        public Guid YoneticiId { get; }

        public static Duzenek Kur(string[] bayiSiteleri, bool persistingAudit = false)
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
                persistingAudit
                    ? new AuditService(dbContext, kapsam, new JsonFileStore(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = Path.Combine(Path.GetTempPath(), "AdaPanel-audit-fixture-" + Guid.NewGuid().ToString("N")) })))
                    : new RecordingAuditService(),
                new KullanilmayanSiteServisi(),
                new KullanilmayanDbSahiplik(),
                new KullanilmayanZamanlayici(),
                new KullanilmayanCascade(),
                new ThrowingApiTokenService(),
                new SahipsizSunucuSahibiServisi());

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
        private readonly Dictionary<Guid, Guid?> parents = [];
        public Guid? OwnerId { get; set; }
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
        public Task<Guid?> GetOwnerAsync(Guid userId, CancellationToken cancellationToken = default) => Task.FromResult(OwnerId);
        public Task<Guid?> GetParentSubscriptionAsync(Guid userId, CancellationToken cancellationToken = default) => Task.FromResult(parents.GetValueOrDefault(userId));
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
        public Task SetParentSubscriptionAsync(Guid userId, Guid? parentUserId, CancellationToken cancellationToken = default) { parents[userId] = parentUserId; return Task.CompletedTask; }
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
        public Task<ScheduledTaskDto> CreateTaskAsync(CreateScheduledTaskRequest request, ScheduledTaskPrivilege privilege, CancellationToken cancellationToken = default) => throw new NotSupportedException();
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
