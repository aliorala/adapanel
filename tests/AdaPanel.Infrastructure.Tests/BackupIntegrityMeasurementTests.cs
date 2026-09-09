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
/// YEDEK "BASARILI" DENMEDEN ONCE GERCEKTEN OLCULUR.
/// </summary>
/// <remarks>
/// <para>
/// <b>OLCULEN HATA (dort nokta, ayni sinif):</b>
/// </para>
/// <list type="number">
///   <item><description><c>FinalizeBackupAsync</c> boyutu KAYDA YAZIYOR ama denetlemiyordu:
///   sifir baytlik bir arsiv "created" damgasi aliyordu.</description></item>
///   <item><description><c>ScanFiles</c>, kok yoksa SESSIZCE bos liste donuyordu: sitesinin
///   fiziksel yolu silinmis bir kullanici, icinde tek dosya olmayan bir arsivi her gece
///   "basarili" olarak aliyordu.</description></item>
///   <item><description><c>TryAddDatabaseDumpAsync</c> yalniz <c>File.Exists</c> soruyordu:
///   sifir baytlik dokum arsive giriyor ve "db yedeklendi" deniyordu. Kaydin
///   <c>backup.database_dump_empty</c> uyarisi vardi ama HIC TETIKLENEMIYORDU.</description></item>
///   <item><description><c>VerifyBackupsAsync</c> arsivi HIC ACMIYORDU: bos/bozuk bir dosya
///   "dogrulamadan gecti" damgasi aliyordu.</description></item>
/// </list>
/// <para>
/// <b>Bu testler ne IDDIA ETMEZ:</b> "arsiv bit bit dogrulandi". Olcum merkezi dizin okumasidir
/// (gerekcesi <see cref="BackupArchiveProbe"/>'ta); bit curumesi olculmuyor ve olculmedigi
/// SOYLENIYOR. Test de tam bunu korur.
/// </para>
/// </remarks>
public sealed class BackupIntegrityMeasurementTests
{
    // ===========================================================================================
    // 1) URETIM ANINDA OLCUM (FinalizeBackupAsync)
    // ===========================================================================================

    [Fact]
    public async Task Sifir_baytlik_yedek_BASARILI_sayilmaz()
    {
        using var alan = new ButunlukAlani();
        // Dokum araci dosyayi YARATIYOR ama BOS birakiyor: yetkisiz mysqldump, kapali servis,
        // dolmus disk. Eskiden bu "created" idi.
        var servis = alan.ServisKur(new SahteDokumcu(bosDokum: true));

        var kayit = await servis.BackupDatabaseAsync(alan.VeritabaniIstegi(), alan.Iptal);

        Assert.Equal("created_with_errors", kayit.Status);
        Assert.Equal(BackupIntegrity.Unsound, kayit.IntegrityState);

        var uyari = Assert.Single(kayit.Warnings, item => item.MessageKey == "backup.archive_unsound");
        Assert.Contains("bytes=0", uyari.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dolu_yedek_SAGLAM_olculur_ve_davranis_daralmaz()
    {
        // KARSI KONTROL: yeni olcum, calisan yedegi bozmamali.
        using var alan = new ButunlukAlani();
        var servis = alan.ServisKur(new SahteDokumcu(bosDokum: false));

        var kayit = await servis.BackupDatabaseAsync(alan.VeritabaniIstegi(), alan.Iptal);

        Assert.Equal("created", kayit.Status);
        Assert.Equal(BackupIntegrity.Sound, kayit.IntegrityState);
        Assert.Empty(kayit.Warnings);
    }

    [Fact]
    public async Task Site_koku_YOKSA_yedek_basarili_denmez()
    {
        using var alan = new ButunlukAlani();
        var servis = alan.ServisKur(new SahteDokumcu(bosDokum: false));

        // Sitenin fiziksel yolu silindi (elle silme, disk degisimi, yanlis yol duzenlemesi).
        Directory.Delete(alan.SiteKoku, recursive: true);

        var kayit = await servis.BackupSiteAsync(alan.SiteIstegi(), alan.Iptal);

        Assert.Equal("created_with_errors", kayit.Status);
        var uyari = Assert.Single(kayit.Warnings, item => item.MessageKey == "backup.source_root_missing");
        Assert.Equal("files", uyari.Target);
    }

    [Fact]
    public async Task Site_koku_VARSA_uyari_uretilmez()
    {
        // KARSI KONTROL: "kok yok" uyarisi her yedekte cikan bir gurultuye donusmemeli.
        using var alan = new ButunlukAlani();
        var servis = alan.ServisKur(new SahteDokumcu(bosDokum: false));

        var kayit = await servis.BackupSiteAsync(alan.SiteIstegi(), alan.Iptal);

        Assert.Equal("created", kayit.Status);
        Assert.DoesNotContain(kayit.Warnings, item => item.MessageKey == "backup.source_root_missing");
    }

    [Fact]
    public async Task Bos_veritabani_dokumu_arsive_GIRMEZ_ve_SITE_yedeginde_de_soylenir()
    {
        using var alan = new ButunlukAlani();
        var servis = alan.ServisKur(new SahteDokumcu(bosDokum: true));

        var istek = alan.SiteIstegi();
        istek.Databases = [new BackupDatabaseRef { Engine = "mysql", DatabaseName = "shop" }];

        var kayit = await servis.BackupSiteAsync(istek, alan.Iptal);

        // (a) Kullaniciya SOYLENIR (eskiden yalniz manifest'ten dusuyordu, kimse okumuyordu).
        var uyari = Assert.Single(kayit.Warnings, item => item.MessageKey == "backup.database_dump_empty");
        Assert.Equal("mysql/shop", uyari.Target);
        Assert.Equal("created_with_errors", kayit.Status);

        // (b) Bos dokum arsive de GIRMEZ: sifir baytlik bir .sql, geri yuklemede bos veritabani demek.
        using var arsiv = ZipFile.OpenRead(kayit.Path);
        Assert.DoesNotContain(arsiv.Entries, girdi => girdi.FullName.StartsWith("databases/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Dolu_veritabani_dokumu_arsive_GIRER()
    {
        // KARSI KONTROL: olcum, calisan dokumu disarida birakmamali.
        using var alan = new ButunlukAlani();
        var servis = alan.ServisKur(new SahteDokumcu(bosDokum: false));

        var istek = alan.SiteIstegi();
        istek.Databases = [new BackupDatabaseRef { Engine = "mysql", DatabaseName = "shop" }];

        var kayit = await servis.BackupSiteAsync(istek, alan.Iptal);

        Assert.DoesNotContain(kayit.Warnings, item => item.MessageKey == "backup.database_dump_empty");
        using var arsiv = ZipFile.OpenRead(kayit.Path);
        Assert.Contains(arsiv.Entries, girdi => girdi.FullName == "databases/mysql-shop.sql");
    }

    // ===========================================================================================
    // 2) DOGRULAMA ARTIK ARSIVI ACIYOR (VerifyBackupsAsync)
    // ===========================================================================================

    [Fact]
    public async Task Dogrulama_bos_arsivi_GECIRMEZ_ve_sonucu_kayda_yazar()
    {
        using var alan = new ButunlukAlani();
        var servis = alan.ServisKur(new SahteDokumcu(bosDokum: false));

        var bos = alan.YerelKayit("bos", icerikBaytlari: 0);
        var saglam = alan.ZipKayit("saglam");
        await alan.KayitlariYazAsync(bos, saglam);

        var sonuc = await servis.VerifyBackupsAsync(alan.Iptal);

        // (a) SAYILIR ve "dosya yok"tan AYRI sayilir.
        Assert.Equal(1, sonuc.Unsound);
        Assert.Equal(0, sonuc.MissingLocal);

        var sorun = Assert.Single(sonuc.Issues, item => item.MessageKey == "backup.verify.unsound");
        Assert.Equal("bos", sorun.BackupId);

        // (b) OLCUM KAYDA YAZILIR: bir sonraki listelemede/budamada da bilinsin.
        var kayitlar = await servis.GetBackupsAsync(alan.Iptal);
        Assert.Equal(BackupIntegrity.Unsound, kayitlar.Single(item => item.Id == "bos").IntegrityState);
        Assert.Equal(BackupIntegrity.Sound, kayitlar.Single(item => item.Id == "saglam").IntegrityState);
    }

    [Fact]
    public async Task Sifreli_arsiv_BOZUK_DEGIL_olculemedi_sayilir()
    {
        // UC DEGERLI GERCEK: "olcemedim" ile "bozuk" ayni sey degil. Sifreli bir arsivin ici
        // parola olmadan okunamaz; ona "bozuk" demek SAGLAM yedegi cop ilan etmek olurdu.
        using var alan = new ButunlukAlani();
        var duz = Path.Combine(alan.Kok, "duz.zip");
        alan.ZipYaz(duz, "a.txt", "icerik");

        var sifreli = Path.Combine(alan.Kok, "duz.zip.enc");
        await BackupArchiveCipher.EncryptFileAsync(duz, sifreli, "parola-123", alan.Iptal);

        var olcum = BackupArchiveProbe.Measure(sifreli, "zip");

        Assert.Equal(BackupIntegrity.Unknown, olcum.State);
        Assert.False(olcum.Unsound);
        Assert.Contains("wrapper=present", olcum.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Sifreli_ama_SIFIR_BAYT_dosya_yine_de_BOZUKTUR()
    {
        // Sarmalayici muafiyeti, "dosya bos ama sifreli olabilir" bahanesine donusmemeli:
        // sifir bayt hicbir bicimde gecerli bir arsiv degildir.
        using var alan = new ButunlukAlani();
        var bos = Path.Combine(alan.Kok, "bos.zip.enc");
        File.WriteAllBytes(bos, []);

        Assert.Equal(BackupIntegrity.Unsound, BackupArchiveProbe.Measure(bos, "zip").State);
    }

    [Fact]
    public void Tek_girdisi_BOS_DOSYA_olan_arsiv_BOZUK_SAYILMAZ()
    {
        // OLCULEN YANLIS POZITIF: "girdi var ama acilmis toplam boyut 0" ilk yazimda BOZUK
        // sayiliyordu ve mevcut bir test hemen kizardi. Tek icerigi sifir baytlik bir dosya olan
        // site (placeholder index.php) YAPISAL OLARAK GECERLI bir arsiv uretir ve geri
        // yuklendiginde o dosya aynen doner. Saglam yedegi cop ilan etmek, hic olcmemekten kotudur.
        using var alan = new ButunlukAlani();
        var yol = Path.Combine(alan.Kok, "bos-girdili.zip");
        using (var akis = new FileStream(yol, FileMode.Create, FileAccess.Write))
        using (var arsiv = new ZipArchive(akis, ZipArchiveMode.Create))
        {
            arsiv.CreateEntry("files/index.php");
        }

        var olcum = BackupArchiveProbe.Measure(yol, "zip");

        Assert.Equal(BackupIntegrity.Sound, olcum.State);
        // Sayi yine de RAPORLANIR: teshis icin degerlidir, karar icin degil.
        Assert.Contains("expanded_bytes=0", olcum.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Yarim_yazilmis_zip_BOZUK_olculur()
    {
        using var alan = new ButunlukAlani();
        var yol = Path.Combine(alan.Kok, "yarim.zip");
        alan.ZipYaz(yol, "a.txt", "icerik");

        // Merkezi dizin dosyanin SONUNDADIR; son 40 bayti kesmek onu okunamaz yapar.
        var bayt = File.ReadAllBytes(yol);
        File.WriteAllBytes(yol, bayt[..Math.Max(1, bayt.Length - 40)]);

        var olcum = BackupArchiveProbe.Measure(yol, "zip");
        Assert.Equal(BackupIntegrity.Unsound, olcum.State);
        Assert.Contains("archive=unreadable", olcum.Detail, StringComparison.Ordinal);
    }

    // ===========================================================================================
    // 3) SONUC: BOZUK ARSIV "TAZE YEDEK" SAYILMAZ (budamanin kaynak korumasi)
    // ===========================================================================================

    [Fact]
    public async Task Bozuk_taze_yedek_ESKILERI_sildirmez()
    {
        // EN PAHALI SONUC: budama, "bu kaynagin guncel yedegi var" derse eskilerini siler.
        // Sifir baytlik bir arsiv taze yedek sayilsaydi, panel korumasi olmayan bir kaynakta
        // GERCEK eski yedekleri silerdi.
        using var alan = new ButunlukAlani();
        var servis = alan.ServisKur(new SahteDokumcu(bosDokum: false));

        var eski = alan.ZipKayit("eski", gunOnce: 40);
        var tazeBozuk = alan.YerelKayit("taze", icerikBaytlari: 0);
        tazeBozuk.IntegrityState = BackupIntegrity.Unsound;
        await alan.KayitlariYazAsync(eski, tazeBozuk);

        var sonuc = await servis.PruneAsync(14, cancellationToken: alan.Iptal);

        Assert.Equal(0, sonuc.Removed);
        Assert.True(File.Exists(eski.Path), "Bozuk taze yedek yuzunden GERCEK eski yedek silindi.");
    }

    [Fact]
    public async Task Saglam_taze_yedek_eskileri_ESKISI_GIBI_sildirir()
    {
        // KARSI KONTROL: koruma daralmadi, yalnizca bozuk yedegi kapsam disi birakti.
        using var alan = new ButunlukAlani();
        var servis = alan.ServisKur(new SahteDokumcu(bosDokum: false));

        var eski = alan.ZipKayit("eski", gunOnce: 40);
        var taze = alan.ZipKayit("taze");
        taze.IntegrityState = BackupIntegrity.Sound;
        await alan.KayitlariYazAsync(eski, taze);

        var sonuc = await servis.PruneAsync(14, cancellationToken: alan.Iptal);

        Assert.Equal(1, sonuc.Removed);
        Assert.False(File.Exists(eski.Path));
    }

    // ===========================================================================================
    // 4) GERI YUKLEME: YARIM KALAN ISE "restored" DENMEZ
    // ===========================================================================================

    [Fact]
    public async Task Panel_geri_yuklemesinde_kurulamayan_site_kayda_TASINIR()
    {
        using var alan = new ButunlukAlani();
        var servis = alan.ServisKur(new SahteDokumcu(bosDokum: false), siteKurulumuPatlasin: true);

        var kayit = alan.PanelKaydi("panel1", iisEnvanteri: true, ftpEnvanteri: false);
        await alan.KayitlariYazAsync(kayit);

        var sonuc = await servis.RestoreAsync(new RestoreBackupRequest { BackupId = "panel1" }, alan.Iptal);

        Assert.Equal("restored_with_errors", sonuc.Status);
        var uyari = Assert.Single(sonuc.Warnings, item => item.MessageKey == "backup.restore_site_failed");
        Assert.Equal("kurtarilan.test", uyari.Target);
    }

    [Fact]
    public async Task Panel_geri_yuklemesinde_kurulamayan_FTP_hesabi_kayda_TASINIR()
    {
        using var alan = new ButunlukAlani();
        var servis = alan.ServisKur(new SahteDokumcu(bosDokum: false), ftpKurulumuPatlasin: true);

        var kayit = alan.PanelKaydi("panel2", iisEnvanteri: false, ftpEnvanteri: true);
        await alan.KayitlariYazAsync(kayit);

        var sonuc = await servis.RestoreAsync(new RestoreBackupRequest { BackupId = "panel2" }, alan.Iptal);

        Assert.Equal("restored_with_errors", sonuc.Status);
        Assert.Single(sonuc.Warnings, item => item.MessageKey == "backup.restore_ftp_failed");
    }

    [Fact]
    public async Task Sorunsuz_panel_geri_yuklemesi_HALA_restored_der()
    {
        // KARSI KONTROL: "restored" damgasi kaybolmadi, yalnizca HAK EDILMESI gerekiyor.
        using var alan = new ButunlukAlani();
        var servis = alan.ServisKur(new SahteDokumcu(bosDokum: false));

        var kayit = alan.PanelKaydi("panel3", iisEnvanteri: true, ftpEnvanteri: true);
        await alan.KayitlariYazAsync(kayit);

        var sonuc = await servis.RestoreAsync(new RestoreBackupRequest { BackupId = "panel3" }, alan.Iptal);

        Assert.Equal("restored", sonuc.Status);
        Assert.Empty(sonuc.Warnings);
    }

    // ===========================================================================================
    // Alan (test kurulumu)
    // ===========================================================================================

    private sealed class ButunlukAlani : IDisposable
    {
        private readonly CancellationTokenSource _iptal = new(TimeSpan.FromSeconds(120));

        public ButunlukAlani()
        {
            Kok = Path.Combine(Path.GetTempPath(), $"adapanel-butunluk-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Kok);
            DosyaDeposu = new JsonFileStore(
                Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = Kok }));
            SiteKoku = Path.Combine(Kok, "wwwroot", "shop");
            Directory.CreateDirectory(SiteKoku);
            File.WriteAllText(Path.Combine(SiteKoku, "index.php"), "MUSTERI ICERIGI");
            YedekKoku = Path.Combine(Kok, "backups");
        }

        public const string Site = "shop.example.com";

        public string Kok { get; }

        public string SiteKoku { get; }

        public string YedekKoku { get; }

        public JsonFileStore DosyaDeposu { get; }

        public CancellationToken Iptal => _iptal.Token;

        public BackupService ServisKur(
            SahteDokumcu dokumcu,
            bool siteKurulumuPatlasin = false,
            bool ftpKurulumuPatlasin = false) =>
            new(new ButunlukSiteServisi(SiteKoku, siteKurulumuPatlasin),
                dokumcu,
                null!,
                new ButunlukTercihServisi(YedekKoku),
                new ButunlukFtpServisi(ftpKurulumuPatlasin),
                DosyaDeposu,
                new ButunlukIstemciFabrikasi());

        public BackupSiteRequest SiteIstegi() => new()
        {
            SiteName = Site,
            IncludeFiles = true,
            IncludeConfig = false,
            IncludeMail = false,
            TargetType = "local"
        };

        public BackupDatabaseRequest VeritabaniIstegi() => new()
        {
            Engine = "mysql",
            DatabaseName = "shop",
            TargetType = "local"
        };

        public Task KayitlariYazAsync(params BackupRecordDto[] kayitlar) =>
            DosyaDeposu.WriteAsync("config/backups.json", kayitlar, Iptal);

        /// <summary>Diskte GERCEKTEN duran, istenen boyutta duz bir dosya + kaydi.</summary>
        public BackupRecordDto YerelKayit(string id, int icerikBaytlari, int gunOnce = 0)
        {
            var yol = Path.Combine(Kok, $"{id}.zip");
            File.WriteAllBytes(yol, new byte[icerikBaytlari]);
            return Kayit(id, yol, gunOnce);
        }

        /// <summary>Diskte GERCEKTEN acilabilen bir zip + kaydi.</summary>
        public BackupRecordDto ZipKayit(string id, int gunOnce = 0)
        {
            var yol = Path.Combine(Kok, $"{id}.zip");
            ZipYaz(yol, "manifest.json", "{\"kind\":\"test\"}");
            return Kayit(id, yol, gunOnce);
        }

        public void ZipYaz(string yol, string girdi, string icerik)
        {
            using var akis = new FileStream(yol, FileMode.Create, FileAccess.Write);
            using var arsiv = new ZipArchive(akis, ZipArchiveMode.Create);
            using var yazici = new StreamWriter(arsiv.CreateEntry(girdi).Open());
            yazici.Write(icerik);
        }

        /// <summary>
        /// Panel yedegi: icinde istege gore IIS envanteri ve/veya FTP envanteri olan gercek bir zip.
        /// </summary>
        public BackupRecordDto PanelKaydi(string id, bool iisEnvanteri, bool ftpEnvanteri)
        {
            var yol = Path.Combine(Kok, $"{id}.zip");
            using (var akis = new FileStream(yol, FileMode.Create, FileAccess.Write))
            using (var arsiv = new ZipArchive(akis, ZipArchiveMode.Create))
            {
                Yaz(arsiv, "manifest.json", "{\"kind\":\"panel\"}");

                if (iisEnvanteri)
                {
                    Yaz(
                        arsiv,
                        "iis/sites.json",
                        "[{\"name\":\"kurtarilan.test\",\"physicalPath\":\"" +
                        Path.Combine(Kok, "kurtarilan").Replace("\\", "\\\\") +
                        "\",\"bindings\":[{\"protocol\":\"http\",\"hostName\":\"kurtarilan.test\",\"ipAddress\":\"*\",\"port\":80}]}]");
                }

                if (ftpEnvanteri)
                {
                    Yaz(
                        arsiv,
                        "config/ftp-users.json",
                        "[{\"userName\":\"ftpkullanici\",\"homePath\":\"" +
                        Path.Combine(Kok, "kurtarilan").Replace("\\", "\\\\") +
                        "\",\"siteName\":\"kurtarilan.test\"}]");
                }
            }

            var kayit = Kayit(id, yol, gunOnce: 0);
            kayit.Type = "panel";
            return kayit;

            static void Yaz(ZipArchive arsiv, string ad, string icerik)
            {
                using var yazici = new StreamWriter(arsiv.CreateEntry(ad).Open());
                yazici.Write(icerik);
            }
        }

        private static BackupRecordDto Kayit(string id, string yol, int gunOnce) => new()
        {
            Id = id,
            Name = $"{id}.zip",
            Type = "site",
            Source = Site,
            Path = yol,
            Format = "zip",
            CreatedAtUtc = DateTime.UtcNow.AddDays(-gunOnce),
            TargetType = "local",
            Status = "created"
        };

        public void Dispose()
        {
            _iptal.Dispose();
            try
            {
                Directory.Delete(Kok, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>
    /// Dokum aracinin SAHTESI. <paramref name="bosDokum"/> ile "dosyayi yaratir ama bos birakir"
    /// halini uretir: gercek dunyada yetkisiz mysqldump'in yaptigi sey budur.
    /// </summary>
    private sealed class SahteDokumcu(bool bosDokum) : IDatabaseService
    {
        public Task<DatabaseDumpResultDto> ExportDumpAsync(DatabaseDumpRequest request, CancellationToken cancellationToken = default)
        {
            if (bosDokum)
            {
                File.WriteAllBytes(request.FilePath, []);
            }
            else
            {
                File.WriteAllText(request.FilePath, $"-- dump of {request.Engine}/{request.DatabaseName}");
            }

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

        public Task<AdaPanel.Core.Dtos.Databases.DatabaseHardeningReport> HardenAdminAccountsAsync(string engine, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class ButunlukSiteServisi(string uygulamaKoku, bool kurulumPatlasin) : IIisSiteService
    {
        public Task<SiteDto> GetSiteAsync(string siteName, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SiteDto
            {
                Name = siteName,
                PhysicalPath = uygulamaKoku,
                ApplicationRootPath = uygulamaKoku
            });

        /// <summary>DR senaryosu: sunucuda hic site yok, envanterdekilerin hepsi EKSIK.</summary>
        public Task<IReadOnlyCollection<SiteDto>> GetSitesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyCollection<SiteDto>>([]);

        public Task<SiteDto> CreateSiteAsync(CreateSiteRequest request, CancellationToken cancellationToken = default) =>
            kurulumPatlasin
                ? throw new InvalidOperationException("IIS uygulama havuzu olusturulamadi")
                : Task.FromResult(new SiteDto { Name = request.Name, PhysicalPath = request.PhysicalPath });

        public Task<SiteDto> UpdatePhysicalPathAsync(string siteName, string physicalPath, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task DeleteSiteAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<SiteDto> StartSiteAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<SiteDto> StopSiteAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<SiteDto> AddBindingAsync(string siteName, SiteBindingRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<SiteDto> RemoveBindingAsync(string siteName, RemoveSiteBindingRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyCollection<SiteDto>> ImportExistingSitesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class ButunlukFtpServisi(bool kurulumPatlasin) : IFtpService
    {
        public Task<FtpServerStatusDto> EnsureServerAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<FtpServerStatusDto> GetServerStatusAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyCollection<FtpUserDto>> GetFtpUsersAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<FtpUserDto> CreateFtpUserAsync(CreateFtpUserRequest request, CancellationToken cancellationToken = default) =>
            kurulumPatlasin
                ? throw new InvalidOperationException("Windows yerel hesabi olusturulamadi")
                : Task.FromResult(new FtpUserDto { UserName = request.UserName, HomePath = request.HomePath });

        public Task SetFtpPasswordAsync(SetFtpPasswordRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task DeleteFtpUserAsync(string userName, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<bool> GetRequireSslAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<AdaPanel.Core.Dtos.Ftp.FtpTlsMeasurement> SetRequireSslAsync(bool requireSsl, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class ButunlukTercihServisi(string yedekKoku) : IPanelPreferencesService
    {
        public Task<PanelPreferencesDto> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new PanelPreferencesDto { DefaultBackupRoot = yedekKoku });

        public Task<PanelPreferencesDto> UpdateAsync(UpdatePanelPreferencesRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    /// <summary>Yerel hedefte HIC istek atilmaz; fabrika yalnizca kurucunun istedigi icin var.</summary>
    private sealed class ButunlukIstemciFabrikasi : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new NotSupportedException("Yerel hedefte ag istegi olmamali.");
    }
}
