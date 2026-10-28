using System.IO.Compression;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Backup;
using AdaPanel.Core.Dtos.Databases;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Backup;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Support;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Birlesik site yedeginin geri yuklenmesi. Denetimde bu yol icin TEK BIR TEST YOKTU; oysa
/// panelin en yikici islemi burasidir: <c>files/*</c> canli site kokunun UZERINE yazilir.
/// <para>
/// Korunan iki kural:
/// (1) uzerine yazmadan ONCE mevcut icerik yana alinir (yoksa geri donulecek hicbir sey kalmaz),
/// (2) yarim kalan hicbir adim sessizce "restored" sayilmaz.
/// </para>
/// </summary>
public sealed class BackupCombinedRestoreSafetyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CombinedArchiveForwardsDefinerChoiceWithoutRewritingTheArchive(bool adapt)
    {
        using var alan = new GeciciAlan();
        var root = alan.KlasorAc("wwwroot");
        const string sql = "CREATE DEFINER=`source`@`%` VIEW v AS SELECT 1;";
        var zip = alan.BirlesikYedekUret(new Dictionary<string, string>(),
            new Dictionary<string, string> { ["databases/mariadb-shop.sql"] = sql });
        var original = await File.ReadAllBytesAsync(zip);
        DatabaseDumpRequest? captured = null;
        var service = alan.ServisKur(zip, root, new SahteVeritabaniServisi(request =>
        {
            captured = request;
            Assert.Equal(sql, File.ReadAllText(request.FilePath));
        }));
        var record = await service.RestoreAsync(new RestoreBackupRequest { BackupId = "b1", AdaptDefiners = adapt });
        Assert.Equal("restored", record.Status);
        Assert.NotNull(captured);
        Assert.Equal(adapt, captured.AdaptDefiners);
        Assert.Equal("mariadb", captured.Engine);
        Assert.Equal(original, await File.ReadAllBytesAsync(zip));
        Assert.False(File.Exists(captured.FilePath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DirectSqlRestorePersistsRealFailureAndChosenOwnership(bool cancelled)
    {
        using var alan = new GeciciAlan();
        var sql = Path.Combine(alan.Kok, "backup.sql");
        await File.WriteAllTextAsync(sql, "-- SQL backup");
        var service = alan.ServisKur(sql, alan.Kok, new SahteVeritabaniServisi(request =>
        {
            Assert.True(request.AdaptDefiners);
            if (cancelled) throw new OperationCanceledException();
            throw new AppException("database.restore_failed", 500, 500, "ERROR 1227 at line 3; CREATE VIEW v_incidents_full; SET USER");
        }));
        await alan.DosyaDeposu.WriteAsync("config/backups.json", new[]
        {
            new BackupRecordDto { Id = "b1", Name = "backup.sql", Type = "database", Source = "mariadb:shop", Format = "sql", Path = sql }
        });
        var error = await Record.ExceptionAsync(() => service.RestoreAsync(new RestoreBackupRequest { BackupId = "b1", AdaptDefiners = true }));
        Assert.NotNull(error);
        var persisted = Assert.Single(await service.GetBackupsAsync());
        Assert.Equal("restored_with_errors", persisted.Status);
        var warning = Assert.Single(persisted.Warnings);
        if (!cancelled)
        {
            Assert.Contains("1227", warning.Detail);
            Assert.Contains("v_incidents_full", warning.Detail);
        }
        Assert.Equal("-- SQL backup", await File.ReadAllTextAsync(sql));
    }

    [Fact]
    public async Task BlockedReplayRetainsTheOriginalSqlFailureForRecovery()
    {
        using var alan = new GeciciAlan();
        var sql = Path.Combine(alan.Kok, "backup.sql");
        await File.WriteAllTextAsync(sql, "-- SQL backup");
        var calls = 0;
        var service = alan.ServisKur(sql, alan.Kok, new SahteVeritabaniServisi(_ =>
        {
            if (++calls == 1) throw new AppException("database.restore_failed", 500, 500, "ERROR 1227; CREATE VIEW v_incidents_full");
            throw new AppException("database.restore_partial_retry_blocked", 409, 409, "No SQL replayed");
        }));
        await alan.DosyaDeposu.WriteAsync("config/backups.json", new[]
        {
            new BackupRecordDto { Id = "b1", Name = "backup.sql", Type = "database", Source = "mariadb:shop", Format = "sql", Path = sql }
        });
        for (var i = 0; i < 3; i++)
            await Assert.ThrowsAsync<AppException>(() => service.RestoreAsync(new RestoreBackupRequest { BackupId = "b1" }));
        var record = Assert.Single(await service.GetBackupsAsync());
        Assert.Equal("restored_with_errors", record.Status);
        Assert.Equal(2, record.Warnings.Count);
        Assert.Contains(record.Warnings, warning => warning.Detail.Contains("1227; CREATE VIEW v_incidents_full"));
        Assert.Contains(record.Warnings, warning => warning.Detail.Contains("No SQL replayed"));
    }

    [Fact]
    public async Task Restore_WhenDatabaseImportFails_ReportsWarningAndRefusesToSayRestored()
    {
        using var alan = new GeciciAlan();

        var canliKok = alan.KlasorAc("wwwroot", "shop");
        await File.WriteAllTextAsync(Path.Combine(canliKok, "index.php"), "eski");

        var zip = alan.BirlesikYedekUret(
            dosyalar: new Dictionary<string, string> { ["files/index.php"] = "yeni" },
            veritabanlari: new Dictionary<string, string> { ["databases/mysql-shop.sql"] = "-- dump" });

        var servis = alan.ServisKur(
            zip,
            canliKok,
            new SahteVeritabaniServisi(_ => throw new AppException("database.restore_failed", 500, 500, "mysql kapali")));

        var kayit = await servis.RestoreAsync(new RestoreBackupRequest { BackupId = "b1" });

        // ESKI DAVRANIS: kosulsuz "restored" + bos uyari listesi + 200. Kullanici veritabaninin
        // donmedigini asla ogrenemiyordu.
        Assert.Equal("restored_with_errors", kayit.Status);
        var uyari = Assert.Single(kayit.Warnings);
        Assert.Equal("database.restore_failed", uyari.MessageKey);
        Assert.Equal("mysql/shop", uyari.Target);
        Assert.Contains("mysql kapali", uyari.Detail);

        // Uyari kalici da olmali: ekran yenilenince "restored" gorunup sorunu unutturmasin.
        var kalici = Assert.Single(await servis.GetBackupsAsync());
        Assert.Equal("restored_with_errors", kalici.Status);
        Assert.Single(kalici.Warnings);
    }

    [Fact]
    public async Task Restore_WhenEverythingSucceeds_StaysRestoredWithoutWarnings()
    {
        using var alan = new GeciciAlan();

        var canliKok = alan.KlasorAc("wwwroot", "shop");
        await File.WriteAllTextAsync(Path.Combine(canliKok, "index.php"), "eski");

        var zip = alan.BirlesikYedekUret(
            dosyalar: new Dictionary<string, string> { ["files/index.php"] = "yeni" },
            veritabanlari: new Dictionary<string, string> { ["databases/mysql-shop.sql"] = "-- dump" });

        var servis = alan.ServisKur(zip, canliKok, new SahteVeritabaniServisi(_ => { }));

        var kayit = await servis.RestoreAsync(new RestoreBackupRequest { BackupId = "b1" });

        Assert.Equal("restored", kayit.Status);
        Assert.Empty(kayit.Warnings);
        Assert.Equal("yeni", await File.ReadAllTextAsync(Path.Combine(canliKok, "index.php")));
    }

    [Fact]
    public async Task Restore_MovesLiveContentAsideBeforeOverwriting()
    {
        using var alan = new GeciciAlan();

        var canliKok = alan.KlasorAc("wwwroot", "shop");
        await File.WriteAllTextAsync(Path.Combine(canliKok, "index.php"), "eski");
        var musteriKlasoru = Path.Combine(canliKok, "uploads");
        Directory.CreateDirectory(musteriKlasoru);
        await File.WriteAllTextAsync(Path.Combine(musteriKlasoru, "fatura.pdf"), "musteri verisi");

        // Yedekte uploads/ YOK: eski davranista bu klasor sessizce canli kokte kalir, kullanici da
        // "yedekteki hale donuldu" saniyordu. Yeni davranista kok gercekten yedekteki hale doner ve
        // eski icerik kardes klasorde durur.
        var zip = alan.BirlesikYedekUret(
            dosyalar: new Dictionary<string, string> { ["files/index.php"] = "yeni" },
            veritabanlari: new Dictionary<string, string>());

        var servis = alan.ServisKur(zip, canliKok, new SahteVeritabaniServisi(_ => { }));

        var kayit = await servis.RestoreAsync(new RestoreBackupRequest { BackupId = "b1" });

        Assert.Equal("restored", kayit.Status);
        Assert.Equal("yeni", await File.ReadAllTextAsync(Path.Combine(canliKok, "index.php")));

        // Yana alinan icerik KAYBOLMADI ve kullaniciya nerede oldugu soylendi.
        Assert.False(string.IsNullOrWhiteSpace(kayit.PreservedPath));
        Assert.True(Directory.Exists(kayit.PreservedPath));
        Assert.Equal(
            "musteri verisi",
            await File.ReadAllTextAsync(Path.Combine(kayit.PreservedPath, "uploads", "fatura.pdf")));
        Assert.Equal("eski", await File.ReadAllTextAsync(Path.Combine(kayit.PreservedPath, "index.php")));

        // Yana alma klasoru site kokunun DISINDA olmali; icinde olsaydi bir sonraki yedege girer
        // ve her turda katlanarak buyurdu.
        Assert.False(PathContainment.IsInsideRoot(canliKok, kayit.PreservedPath));
    }

    [Fact]
    public async Task Restore_WithOnlyDatabases_DoesNotEmptyTheLiveSite()
    {
        using var alan = new GeciciAlan();

        var canliKok = alan.KlasorAc("wwwroot", "shop");
        await File.WriteAllTextAsync(Path.Combine(canliKok, "index.php"), "canli");

        // Yalniz veritabani iceren yedek: dosyalara DOKUNULMAMALI. "Once yana al" kurali burada
        // korumasiz uygulansaydi canli sitenin butun dosyalari tasinip site bos kalirdi.
        var zip = alan.BirlesikYedekUret(
            dosyalar: new Dictionary<string, string>(),
            veritabanlari: new Dictionary<string, string> { ["databases/mysql-shop.sql"] = "-- dump" });

        var servis = alan.ServisKur(zip, canliKok, new SahteVeritabaniServisi(_ => { }));

        var kayit = await servis.RestoreAsync(new RestoreBackupRequest { BackupId = "b1" });

        Assert.Equal("restored", kayit.Status);
        Assert.Equal(string.Empty, kayit.PreservedPath);
        Assert.Equal("canli", await File.ReadAllTextAsync(Path.Combine(canliKok, "index.php")));
    }

    [Fact]
    public async Task Restore_ZipSlipEntry_IsRejectedAndReported()
    {
        using var alan = new GeciciAlan();

        // KARDES KLASOR TUZAGI: "shop" onekiyle "shop2" da eslesir. Ayirici denetimi olmayan
        // StartsWith kontrolu bu girdiyi KOMSU SITEYE yazardi.
        var canliKok = alan.KlasorAc("wwwroot", "shop");
        var komsuKok = alan.KlasorAc("wwwroot", "shop2");
        await File.WriteAllTextAsync(Path.Combine(komsuKok, "index.php"), "komsu site");

        var zip = alan.BirlesikYedekUret(
            dosyalar: new Dictionary<string, string>
            {
                ["files/index.php"] = "yeni",
                ["files/../shop2/index.php"] = "ELE GECIRILDI"
            },
            veritabanlari: new Dictionary<string, string>());

        var servis = alan.ServisKur(zip, canliKok, new SahteVeritabaniServisi(_ => { }));

        var kayit = await servis.RestoreAsync(new RestoreBackupRequest { BackupId = "b1" });

        Assert.Equal("komsu site", await File.ReadAllTextAsync(Path.Combine(komsuKok, "index.php")));

        // Sessizce atlamak da yeterli degildi: bozuk/kotucul arsiv "restored" sayilmamali.
        Assert.Equal("restored_with_errors", kayit.Status);
        Assert.Contains(kayit.Warnings, uyari => uyari.MessageKey == "backup.invalid_entry_path");
    }

    /// <summary>Google Drive WebDAV konusmaz; sessizce webdav'a cevrilmesi yedegin hic gitmemesi demekti.</summary>
    [Theory]
    [InlineData("drive")]
    [InlineData("gdrive")]
    [InlineData("google-drive")]
    public async Task DriveTarget_IsRejectedInsteadOfSilentlyMappedToWebdav(string hedef)
    {
        using var alan = new GeciciAlan();
        var servis = alan.BosServis();

        var sonuc = await servis.TestRemoteTargetAsync(new BackupRemoteTestRequest
        {
            TargetType = hedef,
            RemoteUrl = "https://drive.google.com/upload",
            RemoteUserName = "kullanici",
            RemotePassword = "parola"
        });

        // ESKI DAVRANIS: "drive" sessizce "webdav"e cevriliyor, WebDAV konusmayan Google Drive'a
        // istek atiliyor ve hedef calisiyormus gibi yapilandirilabiliyordu.
        Assert.False(sonuc.Success);
        Assert.Equal("backup.remote_target_unsupported", sonuc.Message);
    }

    // ---------------------------------------------------------------------------------------

    private sealed class GeciciAlan : IDisposable
    {
        public GeciciAlan()
        {
            Kok = Path.Combine(Path.GetTempPath(), $"adapanel-restore-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Kok);
            DosyaDeposu = new JsonFileStore(
                Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = Kok }));
        }

        public string Kok { get; }

        public JsonFileStore DosyaDeposu { get; }

        public string KlasorAc(params string[] parcalar)
        {
            var yol = Path.Combine([Kok, .. parcalar]);
            Directory.CreateDirectory(yol);
            return yol;
        }

        public string BirlesikYedekUret(
            IDictionary<string, string> dosyalar,
            IDictionary<string, string> veritabanlari)
        {
            var zipYolu = Path.Combine(Kok, $"yedek-{Guid.NewGuid():N}.zip");
            using var akis = new FileStream(zipYolu, FileMode.Create, FileAccess.Write);
            using var arsiv = new ZipArchive(akis, ZipArchiveMode.Create);

            YazGirdi(arsiv, "manifest.json", "{}");
            foreach (var (ad, icerik) in dosyalar.Concat(veritabanlari))
            {
                YazGirdi(arsiv, ad, icerik);
            }

            return zipYolu;
        }

        public BackupService ServisKur(string zipYolu, string siteKoku, IDatabaseService veritabani)
        {
            DosyaDeposu.WriteAsync("config/backups.json", new[]
            {
                new BackupRecordDto
                {
                    Id = "b1",
                    Name = Path.GetFileName(zipYolu),
                    Type = "site",
                    Source = "shop.example.com",
                    Path = zipYolu,
                    Format = "zip",
                    CreatedAtUtc = DateTime.UtcNow
                }
            }).GetAwaiter().GetResult();

            return new BackupService(
                new SahteSiteServisi(siteKoku),
                veritabani,
                null!,
                null!,
                null!,
                DosyaDeposu,
                null!);
        }

        public BackupService BosServis() =>
            new(null!, null!, null!, null!, null!, DosyaDeposu, null!);

        public void Dispose()
        {
            try
            {
                // Yana alma klasorleri Kok altinda olustugu icin tek silme yeter.
                Directory.Delete(Kok, recursive: true);
            }
            catch (IOException)
            {
            }
        }

        private static void YazGirdi(ZipArchive arsiv, string ad, string icerik)
        {
            var girdi = arsiv.CreateEntry(ad, CompressionLevel.Fastest);
            using var yazici = new StreamWriter(girdi.Open());
            yazici.Write(icerik);
        }
    }

    private sealed class SahteSiteServisi(string uygulamaKoku) : IIisSiteService
    {
        public Task<SiteDto> GetSiteAsync(string siteName, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SiteDto
            {
                Name = siteName,
                PhysicalPath = uygulamaKoku,
                ApplicationRootPath = uygulamaKoku
            });

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

    private sealed class SahteVeritabaniServisi(Action<DatabaseDumpRequest> iceAktarim) : IDatabaseService
    {
        public Task<DatabaseDumpResultDto> ImportDumpAsync(DatabaseDumpRequest request, CancellationToken cancellationToken = default)
        {
            iceAktarim(request);
            return Task.FromResult(new DatabaseDumpResultDto
            {
                Engine = request.Engine,
                DatabaseName = request.DatabaseName,
                FilePath = request.FilePath
            });
        }

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

        public Task<DatabaseDumpResultDto> ExportDumpAsync(DatabaseDumpRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<DatabaseTransferResultDto> TransferDatabaseAsync(DatabaseTransferRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<DatabaseDetailDto> GetDetailAsync(string engine, string databaseName, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<DatabaseCheckResultDto> CheckAndRepairAsync(string engine, string databaseName, IReadOnlyCollection<string> repairTables, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<AdaPanel.Core.Dtos.Databases.DatabaseHardeningReport> HardenAdminAccountsAsync(string engine, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
