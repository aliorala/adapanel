using System.IO.Compression;
using AdaPanel.Core.Dtos.Backup;
using AdaPanel.Core.Dtos.Databases;
using AdaPanel.Core.Dtos.Ftp;
using AdaPanel.Core.Dtos.Settings;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Backup;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Support;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// ABONELIK yedeginin GERCEK arsivi: kiraci izolasyonu dosya duzeyinde olculur.
/// <para>
/// Planlayici testi kararin dogru oldugunu gosterir; bu test kararin GERCEKTEN uygulandigini
/// gosterir. Ikisi ayri seydir: dogru bir plan, arsiv yazarken yanlis bir dongude kullanilirsa
/// yine baskasinin dosyasi arsive girer.
/// </para>
/// </summary>
public sealed class SubscriptionBackupArchiveTests
{
    private static readonly Guid Musteri = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid BaskaMusteri = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public async Task SubscriptionBackup_ContainsOnlyItsOwnSitesDatabasesAndFtpAccounts()
    {
        using var alan = new AbonelikAlani();

        alan.SiteKur("shop.example.com", ("index.php", "musterinin dosyasi"));
        alan.SiteKur("kurban.com", ("gizli.php", "BASKA MUSTERININ DOSYASI"));

        var servis = alan.ServisKur(
            kapsamdakiSiteler: ["shop.example.com"],
            sahiplik:
            [
                ("mysql", "shop_db", Musteri, "shop.example.com"),
                ("mysql", "kurban_db", BaskaMusteri, "kurban.com")
            ],
            ftpHesaplari:
            [
                ("shop_ftp", "shop.example.com"),
                ("kurban_ftp", "kurban.com")
            ]);

        var kayit = await servis.BackupSubscriptionAsync(new BackupSubscriptionRequest
        {
            UserId = Musteri,
            IncludeMail = false
        });

        Assert.Equal("subscription", kayit.Type);
        Assert.Equal(Musteri.ToString("N"), kayit.SubscriptionUserId);

        using var arsiv = ZipFile.OpenRead(kayit.Path);
        var girdiler = arsiv.Entries.Select(entry => entry.FullName).ToArray();

        // KENDI verisi ICERIDE.
        Assert.Contains("sites/shop.example.com/files/index.php", girdiler);
        Assert.Contains("databases/mysql-shop_db.sql", girdiler);

        // BASKA aboneligin verisi HICBIR bicimde iceride DEGIL.
        Assert.DoesNotContain(girdiler, ad => ad.Contains("kurban", StringComparison.OrdinalIgnoreCase));

        var ftpGirdi = arsiv.GetEntry("ftp/accounts.json");
        Assert.NotNull(ftpGirdi);
        using var okuyucu = new StreamReader(ftpGirdi!.Open());
        var ftpJson = await okuyucu.ReadToEndAsync();
        Assert.Contains("shop_ftp", ftpJson, StringComparison.Ordinal);
        Assert.DoesNotContain("kurban_ftp", ftpJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SubscriptionBackup_Incremental_CarriesOnlyChangedFilesAcrossAllSites()
    {
        using var alan = new AbonelikAlani();

        alan.SiteKur("shop.example.com", ("sabit.txt", "degismez"), ("degisken.txt", "ilk"));
        alan.SiteKur("blog.example.com", ("yazi.txt", "eski yazi"));

        var servis = alan.ServisKur(
            kapsamdakiSiteler: ["shop.example.com", "blog.example.com"],
            sahiplik: [],
            ftpHesaplari: []);

        var istek = new BackupSubscriptionRequest
        {
            UserId = Musteri,
            IncludeMail = false,
            IncludeFtp = false,
            IncludeDatabases = false,
            Incremental = true,
            FullBackupEveryDays = 30
        };

        var tam = await servis.BackupSubscriptionAsync(istek);
        Assert.Equal("full", tam.BackupMode);

        alan.DosyaYaz("shop.example.com", "degisken.txt", "ikinci deger daha uzun");

        var artimsal = await servis.BackupSubscriptionAsync(istek);
        Assert.Equal("incremental", artimsal.BackupMode);
        Assert.Equal(tam.ChainId, artimsal.ChainId);

        using var arsiv = ZipFile.OpenRead(artimsal.Path);
        var dosyalar = arsiv.Entries
            .Select(entry => entry.FullName)
            .Where(ad => ad.Contains("/files/", StringComparison.Ordinal))
            .ToArray();

        // TEK bir dosya degisti; zincir abonelik BUTUNU uzerinden kurulur, site site degil.
        Assert.Equal(["sites/shop.example.com/files/degisken.txt"], dosyalar);
    }

    [Fact]
    public async Task SubscriptionBackup_WhenASiteIsMissing_ReportsItInsteadOfSayingSuccess()
    {
        using var alan = new AbonelikAlani();
        alan.SiteKur("shop.example.com", ("index.php", "var"));

        // "yok.example.com" kapsamda AMA IIS'te yok.
        var servis = alan.ServisKur(
            kapsamdakiSiteler: ["shop.example.com", "yok.example.com"],
            sahiplik: [],
            ftpHesaplari: []);

        var kayit = await servis.BackupSubscriptionAsync(new BackupSubscriptionRequest
        {
            UserId = Musteri,
            IncludeMail = false,
            IncludeFtp = false,
            IncludeDatabases = false
        });

        // KISMI BASARI DURUST RAPORLANIR: "yedeklendi" deyip icinde olmayan bir siteyi geri
        // yuklerken kesfetmek, panelin verebilecegi en pahali surprizdir.
        Assert.Equal("created_with_errors", kayit.Status);
        var uyari = Assert.Single(kayit.Warnings);
        Assert.Equal("backup.site_unavailable", uyari.MessageKey);
        Assert.Equal("yok.example.com", uyari.Target);
    }

    // ------------------------------------------------------------------------------------

    private sealed class AbonelikAlani : IDisposable
    {
        private readonly Dictionary<string, string> _siteKokleri = new(StringComparer.OrdinalIgnoreCase);

        public AbonelikAlani()
        {
            Kok = Path.Combine(Path.GetTempPath(), $"adapanel-abonelik-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Kok);
            DosyaDeposu = new JsonFileStore(
                Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = Kok }));
        }

        public string Kok { get; }

        public JsonFileStore DosyaDeposu { get; }

        public void SiteKur(string siteAdi, params (string Ad, string Icerik)[] dosyalar)
        {
            var kok = Path.Combine(Kok, "wwwroot", siteAdi);
            Directory.CreateDirectory(kok);
            _siteKokleri[siteAdi] = kok;
            foreach (var (ad, icerik) in dosyalar)
            {
                File.WriteAllText(Path.Combine(kok, ad), icerik);
            }
        }

        public void DosyaYaz(string siteAdi, string ad, string icerik) =>
            File.WriteAllText(Path.Combine(_siteKokleri[siteAdi], ad), icerik);

        public BackupService ServisKur(
            IReadOnlyList<string> kapsamdakiSiteler,
            IReadOnlyCollection<(string Engine, string DatabaseName, Guid OwnerUserId, string SiteName)> sahiplik,
            IReadOnlyCollection<(string UserName, string SiteName)> ftpHesaplari)
        {
            return new BackupService(
                new AbonelikSiteServisi(_siteKokleri),
                new AbonelikVeritabaniServisi(),
                null!,
                new AbonelikTercihServisi(Path.Combine(Kok, "backups")),
                new AbonelikFtpServisi(ftpHesaplari),
                DosyaDeposu,
                null!,
                null,
                null,
                new AbonelikKapsamServisi(kapsamdakiSiteler),
                new AbonelikSahiplikServisi(sahiplik));
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Kok, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private sealed class AbonelikTercihServisi(string yedekKoku) : IPanelPreferencesService
    {
        public Task<PanelPreferencesDto> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new PanelPreferencesDto { DefaultBackupRoot = yedekKoku });

        public Task<PanelPreferencesDto> UpdateAsync(UpdatePanelPreferencesRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class AbonelikSiteServisi(Dictionary<string, string> kokler) : IIisSiteService
    {
        public Task<SiteDto> GetSiteAsync(string siteName, CancellationToken cancellationToken = default)
        {
            if (!kokler.TryGetValue(siteName, out var kok))
            {
                throw new AdaPanel.Core.Common.AppException("sites.not_found", 404, 404);
            }

            return Task.FromResult(new SiteDto { Name = siteName, PhysicalPath = kok, ApplicationRootPath = kok });
        }

        public Task<IReadOnlyCollection<SiteDto>> GetSitesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<SiteDto> CreateSiteAsync(CreateSiteRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<SiteDto> UpdatePhysicalPathAsync(string siteName, string physicalPath, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task DeleteSiteAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<SiteDto> StartSiteAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<SiteDto> StopSiteAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<SiteDto> AddBindingAsync(string siteName, SiteBindingRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<SiteDto> RemoveBindingAsync(string siteName, RemoveSiteBindingRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyCollection<SiteDto>> ImportExistingSitesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    /// <summary>Dokumu gercekten dosyaya yazar: arsive girip girmedigi olculebilsin.</summary>
    private sealed class AbonelikVeritabaniServisi : IDatabaseService
    {
        public Task<DatabaseDumpResultDto> ExportDumpAsync(DatabaseDumpRequest request, CancellationToken cancellationToken = default)
        {
            File.WriteAllText(request.FilePath, $"-- dump of {request.Engine}/{request.DatabaseName}");
            return Task.FromResult(new DatabaseDumpResultDto
            {
                Engine = request.Engine,
                DatabaseName = request.DatabaseName,
                FilePath = request.FilePath
            });
        }

        public Task<DatabaseDumpResultDto> ImportDumpAsync(DatabaseDumpRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyCollection<DatabaseEngineStatusDto>> GetEnginesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<DatabaseEngineStatusDto> RegisterCredentialsAsync(RegisterDatabaseCredentialsRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<DatabaseWorkspaceDto> GetWorkspaceAsync(string engine, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<DatabaseWorkspaceDto> CreateDatabaseAsync(CreateDatabaseRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<DatabaseWorkspaceDto> ImportDatabaseAsync(ImportDatabaseRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task DeleteDatabaseAsync(string engine, string name, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<DatabaseWorkspaceDto> CreateUserAsync(CreateDatabaseUserRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task DeleteUserAsync(string engine, string userName, string host, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<DatabaseWorkspaceDto> SetUserPasswordAsync(SetDatabaseUserPasswordRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<DatabaseWorkspaceDto> GrantAccessAsync(GrantDatabaseAccessRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<DatabaseTransferResultDto> TransferDatabaseAsync(DatabaseTransferRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<DatabaseDetailDto> GetDetailAsync(string engine, string databaseName, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<DatabaseCheckResultDto> CheckAndRepairAsync(string engine, string databaseName, IReadOnlyCollection<string> repairTables, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<string>> HardenAdminAccountsAsync(string engine, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class AbonelikFtpServisi(IReadOnlyCollection<(string UserName, string SiteName)> hesaplar) : IFtpService
    {
        public Task<IReadOnlyCollection<FtpUserDto>> GetFtpUsersAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyCollection<FtpUserDto>>(hesaplar
                .Select(hesap => new FtpUserDto { UserName = hesap.UserName, SiteName = hesap.SiteName, HomePath = @"C:\ftp" })
                .ToArray());

        public Task<FtpServerStatusDto> EnsureServerAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<FtpServerStatusDto> GetServerStatusAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<FtpUserDto> CreateFtpUserAsync(CreateFtpUserRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task SetFtpPasswordAsync(SetFtpPasswordRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task DeleteFtpUserAsync(string userName, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<bool> GetRequireSslAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<bool> SetRequireSslAsync(bool requireSsl, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class AbonelikSahiplikServisi(
        IReadOnlyCollection<(string Engine, string DatabaseName, Guid OwnerUserId, string SiteName)> kayitlar) : IDatabaseOwnershipService
    {
        public Task<IReadOnlyCollection<(string Engine, string DatabaseName, Guid OwnerUserId, string SiteName)>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(kayitlar);

        public Task RecordAsync(string engine, string databaseName, Guid ownerUserId, string? siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task RemoveAsync(string engine, string databaseName, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task RemoveBySiteAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<int> CountForOwnerAsync(Guid ownerUserId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Guid?> GetOwnerAsync(string engine, string databaseName, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyCollection<(string Engine, string DatabaseName)>> GetOwnedAsync(Guid ownerUserId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class AbonelikKapsamServisi(IReadOnlyList<string> siteler) : ICustomerScopeService
    {
        public Task<CustomerScope> GetScopeAsync(Guid userId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new CustomerScope(siteler, 10, 1024, 5, 5, 5));

        public Task<IReadOnlyCollection<string>> GetAllowedMailDomainsAsync(Guid userId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyCollection<string>>(siteler);

        public Task<IReadOnlyCollection<string>> GetAssignedSitesAsync(Guid userId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyCollection<string>>(siteler);

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
}
