using System.Security.Claims;
using AdaPanel.Api.Controllers;
using AdaPanel.Core.Dtos.Audit;
using AdaPanel.Core.Dtos.Backup;
using AdaPanel.Core.Enums;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.RunningJobs;
using AdaPanel.Infrastructure.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// YEDEK UCLARININ DENETIM IZI.
/// </summary>
/// <remarks>
/// <para>
/// OLCULEN BOSLUK: yedek GERI YUKLEME (<c>POST restore</c> ve <c>POST restore-items</c>) hicbir iz
/// birakmiyordu. Bu, panelin CANLI SITE VERISININ UZERINE YAZAN islemidir; "sitem dun geceki
/// haline donmus, bunu kim yapti?" sorusunun cevabi hicbir yerde yoktu. Yedek OLUSTURMA uclari da
/// (site/veritabani/posta/panel/abonelik) izsizdi -- panel yedegi admin parola ozetlerini ve 2FA
/// gizlerini iceren, indirilebilir bir arsiv uretmesine ragmen.
/// </para>
/// <para>
/// Ikinci bosluk KONU sutunuydu: kayit <c>userId</c> ve <c>subscriptionUserId</c> ikisi de bos
/// yazilirsa (<c>AuditLogQueryEngine</c>) kapsamli role -- yani KIRACIYA -- HIC gosterilmez.
/// Yoneticinin musteri sitesine yaptigi geri yukleme, musterinin kendi denetim ekraninda
/// gorunmelidir; testler bunu ayrica olcer.
/// </para>
/// </remarks>
public sealed class BackupAuditTrailTests
{
    private static readonly Guid Yonetici = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid SiteSahibi = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public async Task Geri_yukleme_IZ_BIRAKIR_ve_izin_KONUSU_sitenin_sahibidir()
    {
        var denetim = new KonuluDenetim();
        var yedek = SiteKaydi();
        var denetleyici = Denetleyici(new SahteYedekServisi(yedek), denetim);

        await denetleyici.RestoreAsync(new RestoreBackupRequest { BackupId = yedek.Id }, CancellationToken.None);

        var kayit = Assert.Single(denetim.Kayitlar, item => item.Action == "backup.restored");
        Assert.Equal(Yonetici, kayit.UserId);

        // KONU: eylemi yapan yonetici, uzerinde yapilan kaynagin sahibi ise musteri.
        Assert.Equal(SiteSahibi, kayit.SubscriptionUserId);

        // Ayrinti hangi yedekten geri donuldugunu SOYLEMELI; yoksa kayit soruyu cevaplamiyor.
        Assert.Contains("shop.example.com", kayit.Details);
        Assert.Contains(yedek.Name, kayit.Details);
    }

    [Fact]
    public async Task Geri_yukleme_PATLASA_da_iz_birakir()
    {
        // EN TEHLIKELI DURUM: dosyalar canli site kokunun uzerine ZATEN yazilmis olabilir ve islem
        // ortada patlamistir. "Denendi ama patladi" ile "hic denenmedi" asla karistirilmamali.
        var denetim = new KonuluDenetim();
        var yedek = SiteKaydi();
        var servis = new SahteYedekServisi(yedek) { GeriYuklemeHatasi = new InvalidOperationException("disk dolu") };
        var denetleyici = Denetleyici(servis, denetim);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => denetleyici.RestoreAsync(new RestoreBackupRequest { BackupId = yedek.Id }, CancellationToken.None));

        var kayit = Assert.Single(denetim.Kayitlar, item => item.Action == "backup.restore_failed");
        Assert.Equal(SiteSahibi, kayit.SubscriptionUserId);
        Assert.Contains("disk dolu", kayit.Details);
    }

    [Fact]
    public async Task Granuler_geri_yukleme_de_IZ_BIRAKIR()
    {
        var denetim = new KonuluDenetim();
        var yedek = SiteKaydi();
        var denetleyici = Denetleyici(new SahteYedekServisi(yedek), denetim);

        await denetleyici.RestoreItemsAsync(
            new RestoreBackupItemsRequest { BackupId = yedek.Id, Items = ["files/index.php", "files/uploads/"] },
            CancellationToken.None);

        var kayit = Assert.Single(denetim.Kayitlar, item => item.Action == "backup.items_restored");
        Assert.Equal(SiteSahibi, kayit.SubscriptionUserId);
        Assert.Contains("files/index.php", kayit.Details);
    }

    [Fact]
    public async Task Yedek_olusturma_IZ_BIRAKIR_ve_EKSIK_yedegi_kayitta_da_soyler()
    {
        // "created_with_errors" ile "created" arasindaki fark tam olarak "yedek eksik mi"
        // sorusunun cevabidir; denetim kaydindan da okunabilmeli.
        var denetim = new KonuluDenetim();
        var uretilen = SiteKaydi();
        uretilen.Status = "created_with_errors";
        var denetleyici = Denetleyici(new SahteYedekServisi(uretilen), denetim);

        await denetleyici.BackupSiteAsync(new BackupSiteRequest { SiteName = "shop.example.com" }, CancellationToken.None);

        var kayit = Assert.Single(denetim.Kayitlar, item => item.Action == "backup.created");
        Assert.Equal(SiteSahibi, kayit.SubscriptionUserId);
        Assert.Contains("created_with_errors", kayit.Details);
    }

    [Fact]
    public async Task Panel_yedegi_IZ_BIRAKIR()
    {
        // Panel yedegi adapanel.db'yi (yonetici parola ozetleri, 2FA gizleri) tasir ve panelden
        // INDIRILEBILIR. Kimin ne zaman aldigi izlenebilir olmali.
        var denetim = new KonuluDenetim();
        var uretilen = SiteKaydi();
        uretilen.Type = "panel";
        uretilen.Source = "panel";
        var denetleyici = Denetleyici(new SahteYedekServisi(uretilen), denetim);

        await denetleyici.BackupPanelAsync(new BackupPanelRequest(), CancellationToken.None);

        var kayit = Assert.Single(denetim.Kayitlar, item => item.Action == "backup.created");
        Assert.Equal(Yonetici, kayit.UserId);
        Assert.Contains("panel", kayit.Details);
    }

    [Fact]
    public async Task Abonelik_yedeginin_KONUSU_abonelik_sahibidir()
    {
        var denetim = new KonuluDenetim();
        var abone = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var uretilen = SiteKaydi();
        uretilen.Type = "subscription";
        uretilen.Source = abone.ToString("N");
        uretilen.SubscriptionUserId = abone.ToString("N");

        var denetleyici = Denetleyici(new SahteYedekServisi(uretilen), denetim);

        await denetleyici.BackupSubscriptionAsync(
            new BackupSubscriptionRequest { UserId = abone },
            CancellationToken.None);

        var kayit = Assert.Single(denetim.Kayitlar, item => item.Action == "backup.created");
        Assert.Equal(abone, kayit.SubscriptionUserId);
    }

    // ===========================================================================================
    // Yardimcilar
    // ===========================================================================================

    private static BackupRecordDto SiteKaydi() => new()
    {
        Id = "yedek-1",
        Name = "shop.example.com-20260819030000.zip",
        Type = "site",
        Source = "shop.example.com",
        Path = @"C:\yedek\shop.zip",
        Format = "zip",
        Status = "created",
        CreatedAtUtc = DateTime.UtcNow
    };

    private static BackupController Denetleyici(IBackupService yedekServisi, IAuditService denetim)
    {
        var kullanici = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, Yonetici.ToString()),
                new Claim(ClaimTypes.Role, "Administrator")
            ],
            authenticationType: "Test",
            nameType: ClaimTypes.Name,
            roleType: ClaimTypes.Role));

        // CALISAN-IS SERVISI TAKLIT EDILMIYOR, GERCEGI KULLANILIYOR (kendi gecici veri kokunde).
        // Taklit edilseydi bu testler "yedek ucu calisan-is kaydini aciyor mu" sorusunu hic
        // olcmezdi ve kayit acmayi unutan bir degisiklik burada yesil kalirdi. Gercek servis
        // diske yaziyor; her denetleyici kendi kokunu aldigi icin testler birbirini etkilemez.
        var isKoku = Path.Combine(Path.GetTempPath(), $"adapanel-audit-jobs-{Guid.NewGuid():N}");
        var calisanIsler = new RunningJobService(
            // Tam nitelikli: `AdaPanel.Infrastructure.Options` ad alani ile
            // `Microsoft.Extensions.Options.Options` sinifi ayni adi tasiyor.
            new JsonFileStore(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = isKoku })),
            NullLogger<RunningJobService>.Instance);

        return new BackupController(yedekServisi, new SahiplikKapsami(), new SahteProgramServisi(), calisanIsler, denetim)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = kullanici }
            }
        };
    }

    /// <summary>Denetim kaydinin YAPAN ve KONU sutunlarini AYRI AYRI saklar.</summary>
    private sealed class KonuluDenetim : IAuditService
    {
        public List<(string Action, string Resource, string Details, Guid? UserId, Guid? SubscriptionUserId)> Kayitlar { get; } = [];

        public Task WriteAsync(string action, string resource, string details, Guid? userId, string? ipAddress, CancellationToken cancellationToken = default)
        {
            Kayitlar.Add((action, resource, details, userId, null));
            return Task.CompletedTask;
        }

        public Task WriteForSubscriptionAsync(string action, string resource, string details, Guid? userId, Guid? subscriptionUserId, string? ipAddress, CancellationToken cancellationToken = default)
        {
            Kayitlar.Add((action, resource, details, userId, subscriptionUserId));
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

    private sealed class SahteYedekServisi(BackupRecordDto kayit) : IBackupService
    {
        public Exception? GeriYuklemeHatasi { get; set; }

        public Task<IReadOnlyCollection<BackupRecordDto>> GetBackupsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyCollection<BackupRecordDto>>([kayit]);

        public Task<BackupRecordDto> BackupSiteAsync(BackupSiteRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(kayit);

        public Task<BackupRecordDto> BackupSubscriptionAsync(BackupSubscriptionRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(kayit);

        public Task<BackupRecordDto> BackupDatabaseAsync(BackupDatabaseRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(kayit);

        public Task<BackupRecordDto> BackupMailAsync(BackupMailRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(kayit);

        public Task<BackupRecordDto> BackupPanelAsync(BackupPanelRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(kayit);

        public Task<BackupRecordDto> RestoreAsync(RestoreBackupRequest request, CancellationToken cancellationToken = default) =>
            GeriYuklemeHatasi is null ? Task.FromResult(kayit) : Task.FromException<BackupRecordDto>(GeriYuklemeHatasi);

        public Task<RestoreBackupItemsResultDto> RestoreItemsAsync(RestoreBackupItemsRequest request, CancellationToken cancellationToken = default) =>
            GeriYuklemeHatasi is null
                ? Task.FromResult(new RestoreBackupItemsResultDto { RestoredCount = request.Items.Length, TargetRoot = @"C:\inetpub\shop" })
                : Task.FromException<RestoreBackupItemsResultDto>(GeriYuklemeHatasi);

        public Task<BackupEntriesDto> GetEntriesAsync(string backupId, string? prefix, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<int> PruneAsync(int retentionDays, BackupPruneRemoteOptions? remote = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<BackupRemoteTestResultDto> TestRemoteTargetAsync(BackupRemoteTestRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<BackupVerificationResultDto> VerifyBackupsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<int> PruneSubscriptionAsync(Guid userId, int retentionDays, int keepCount, BackupPruneRemoteOptions? remote = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<BackupChainDto> GetChainAsync(string backupId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task DeleteBackupAsync(string backupId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<BackupDeleteResultDto> DeleteBackupChainAsync(string backupId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<BackupEncryptionSettingsDto> GetEncryptionSettingsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<BackupEncryptionSettingsDto> SetEncryptionSettingsAsync(SetBackupEncryptionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    /// <summary>Site sahipligini OLCULEBILIR bicimde donen kapsam servisi.</summary>
    private sealed class SahiplikKapsami : ICustomerScopeService
    {
        public Task<Guid?> GetSiteOwnerUserIdAsync(string siteName, CancellationToken cancellationToken = default) =>
            Task.FromResult<Guid?>(string.Equals(siteName, "shop.example.com", StringComparison.OrdinalIgnoreCase) ? SiteSahibi : null);

        public Task<IReadOnlyCollection<string>> GetAssignedSitesAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<CustomerScope> GetScopeAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

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

        public Task SuspendAsync(Guid userId, string reason, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task ResumeAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyCollection<Guid>> GetSuspendedUserIdsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyCollection<Guid>> GetSubscriptionUserIdsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class SahteProgramServisi : ISubscriptionBackupScheduleService
    {
        public Task<IReadOnlyCollection<SubscriptionBackupScheduleDto>> GetSchedulesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<SubscriptionBackupScheduleDto> GetScheduleAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<SubscriptionBackupScheduleDto> UpdateScheduleAsync(Guid userId, UpdateSubscriptionBackupScheduleRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task RemoveScheduleAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<SubscriptionBackupScheduleDto> RunNowAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task RunDueAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
