using System.IO.Compression;
using System.Security.Claims;
using AdaPanel.Api.Controllers;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Backup;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Dtos.Store;
using AdaPanel.Core.Enums;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.AppStore;
using AdaPanel.Infrastructure.Backup;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// SUNUCU SAHİPLİĞİ SINIRININ KAĞIT ÜSTÜNDE KALMASINI ENGELLEYEN ÜÇ KAPI.
/// </summary>
/// <remarks>
/// <para>
/// 552b028'de terminal sunucu sahibine kısıtlandı. Sahiplik <c>config/server-owner.json</c>'dan
/// gelir ve korumanın TEK dayanağı o dosyanın panelin HTTP yüzeyinden YAZILAMAMASIDIR. Üç uç bu
/// dayanağı çürütüyordu — hepsi de <c>Administrator</c> rolüne açık ve panel LocalSystem:
/// </para>
/// <list type="number">
/// <item><c>POST /backup/restore</c>: <c>TargetPath</c> mutlak ve DENETİMSİZ; arşiv panelin veri
/// köküne açılıp sahiplik kaydı ezilebiliyordu. Ayrıca <c>tasks/*.xml</c> içeriği hiç
/// denetlenmeden <c>Register-ScheduledTask</c>'a veriliyordu (SYSTEM'de komut).</item>
/// <item><c>POST /store/install</c>: <c>targetPath</c> mutlak olabiliyor, <c>cleanInstall</c>
/// hedefi boşaltıyordu.</item>
/// <item><c>POST /sites</c> ve <c>PUT /sites/{ad}/document-root</c>: yönetici dalında fiziksel yol
/// kısıtı YOKTU; <c>C:\</c> köklü bir site dosya yöneticisine TÜM DİSKİ açıyordu.</item>
/// </list>
/// <para>
/// <b>Bu dosya davranış ölçer, kaynak taramaz.</b> Her test gerçek servisi/denetleyiciyi çalıştırır
/// ve sonucu (atılan istisna, diskteki dosya, çağrılan/çağrılmayan kaydedici) ölçer.
/// </para>
/// </remarks>
public sealed class SunucuSahipligiYazmaSiniriTests
{
    // =====================================================================================
    // 1) GERİ YÜKLEME HEDEFİ — sahiplik kaydının üzerine yazılamaz
    // =====================================================================================

    /// <summary>
    /// EN KRİTİK YOL: panel yedeği, hedef panelin KENDİ veri kökü verilerek geri yüklenemez.
    /// </summary>
    [Fact]
    public async Task Geri_yukleme_panelin_veri_kokune_YAZAMAZ()
    {
        using var alan = new Alan();
        var gercekSahip = Guid.NewGuid();
        await alan.Depo.WriteAsync("config/server-owner.json", new { UserId = gercekSahip });

        // Saldırganın arşivi: içinde KENDİSİNİ sahip gösteren bir kayıt var.
        var saldirgan = Guid.NewGuid();
        var zip = alan.Zip(new Dictionary<string, string>
        {
            ["config/server-owner.json"] = $"{{\"UserId\":\"{saldirgan}\"}}"
        });

        var servis = alan.Servis(zip, "panel");

        var hata = await Assert.ThrowsAsync<AppException>(() => servis.RestoreAsync(
            new RestoreBackupRequest { BackupId = "b1", TargetPath = alan.Kok }));

        Assert.Equal("backup.target_path_forbidden", hata.MessageKey);

        // ...ve canlı kayıt DOKUNULMAMIŞ olmalı. Yalnız istisnayı ölçmek yetmez: hata atılıp
        // dosyanın yine de yazıldığı bir sıralama hatası testi yeşil bırakırdı.
        var kalan = await File.ReadAllTextAsync(alan.Depo.ResolvePath("config/server-owner.json"));
        Assert.Contains(gercekSahip.ToString(), kalan, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(saldirgan.ToString(), kalan, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(@"C:\Windows\System32")]
    [InlineData(@"C:\")]
    [InlineData(@"\\sunucu\pay")]
    public async Task Geri_yukleme_sistem_alanina_ve_ag_paylasimina_YAZAMAZ(string hedef)
    {
        using var alan = new Alan();
        var zip = alan.Zip(new Dictionary<string, string> { ["a.txt"] = "x" });
        var servis = alan.Servis(zip, "panel");

        var hata = await Assert.ThrowsAsync<AppException>(() => servis.RestoreAsync(
            new RestoreBackupRequest { BackupId = "b1", TargetPath = hedef }));

        Assert.Equal("backup.target_path_forbidden", hata.MessageKey);
    }

    /// <summary>
    /// FAZLA KAPATMA NÖBETÇİSİ — SAHİBİN FELAKET KURTARMASI. Yedeği kendi seçtiği bir klasöre
    /// açmak MEŞRUDUR ve çalışmaya devam etmeli; "her hedefi reddet" çözümü burayı kırar.
    /// </summary>
    [Fact]
    public async Task Mesru_bir_hedefe_geri_yukleme_CALISMAYA_DEVAM_EDER()
    {
        using var alan = new Alan();
        var zip = alan.Zip(new Dictionary<string, string> { ["site/index.html"] = "merhaba" });
        var servis = alan.Servis(zip, "panel");

        var hedef = Path.Combine(Path.GetTempPath(), $"adapanel-hedef-{Guid.NewGuid():N}");
        var kayit = await servis.RestoreAsync(new RestoreBackupRequest { BackupId = "b1", TargetPath = hedef });

        Assert.Equal("restored", kayit.Status);
        Assert.True(File.Exists(Path.Combine(hedef, "site", "index.html")));
    }

    /// <summary>
    /// FAZLA KAPATMA NÖBETÇİSİ — HEDEF VERİLMEYEN geri yükleme. Panelin KENDİ varsayılan hedefi
    /// veri kökünün ALTINDADIR; denetim ona da uygulansaydı kural kendi kendini reddeder ve
    /// hedefsiz her geri yükleme ölürdü.
    /// </summary>
    [Fact]
    public async Task Hedef_verilmeyen_geri_yukleme_veri_kokune_acilmaya_DEVAM_EDER()
    {
        using var alan = new Alan();
        var zip = alan.Zip(new Dictionary<string, string> { ["site/index.html"] = "merhaba" });
        var servis = alan.Servis(zip, "panel");

        var kayit = await servis.RestoreAsync(new RestoreBackupRequest { BackupId = "b1" });

        Assert.Equal("restored", kayit.Status);
        Assert.StartsWith(alan.Kok, kayit.Path, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(Path.Combine(kayit.Path, "site", "index.html")));
    }

    /// <summary>
    /// KARDEŞ UÇ da aynı kapıdan geçer: seçerek geri yükleme (<c>restore-items</c>) de mutlak bir
    /// hedef alır. Yalnız birini kapatmak, aynı yazmayı öbür uçtan yapan bir yan kapı bırakırdı —
    /// bu depoda "bir tarafı kapalı, öbür tarafı açık" tekrar eden hata sınıfıdır.
    /// </summary>
    [Fact]
    public async Task Secerek_geri_yukleme_de_panelin_veri_kokune_YAZAMAZ()
    {
        using var alan = new Alan();
        var zip = alan.Zip(new Dictionary<string, string> { ["config/server-owner.json"] = "{}" });
        var servis = alan.Servis(zip, "panel");

        var hata = await Assert.ThrowsAsync<AppException>(() => servis.RestoreItemsAsync(
            new RestoreBackupItemsRequest
            {
                BackupId = "b1",
                TargetPath = alan.Kok,
                Items = ["config/server-owner.json"]
            }));

        Assert.Equal("backup.target_path_forbidden", hata.MessageKey);
    }

    // =====================================================================================
    // 2) YEDEKTEN GELEN GÖREV XML'İ — SYSTEM'de komut kurmak sahibin işidir
    // =====================================================================================

    /// <summary>
    /// Arşivden gelen görev tanımı, geri yüklemeyi SAHİP YAPMIYORSA kaydedilmez.
    /// </summary>
    /// <remarks>
    /// XML'in içeriği <c>&lt;Principal&gt;</c> ve <c>&lt;Exec&gt;&lt;Command&gt;</c> taşır; onu
    /// olduğu gibi <c>Register-ScheduledTask</c>'a vermek, sunucuda SYSTEM olarak istenen komutu
    /// kurmaktır. Ölçülen şey KAYDEDİCİNİN HİÇ ÇAĞRILMAMASI — "uyarı yazıldı ama görev de kuruldu"
    /// hâli sessizce geçerdi.
    /// </remarks>
    [Fact]
    public async Task Sahip_olmayan_yoneticinin_geri_yuklemesi_gorev_XMLini_KAYDETMEZ()
    {
        using var alan = new Alan();
        var kok = alan.GorevliCikarmaKlasoru("kotu", ZararliGorevXml);

        var uyarilar = new List<BackupRestoreWarningDto>();
        var cagrilanlar = new List<string>();

        await BackupService.TryRestoreScheduledTasksAsync(
            kok,
            caginSahibiMi: false,
            uyarilar,
            (ad, xml, _) => { cagrilanlar.Add(ad); return Task.FromResult(Basarili()); },
            CancellationToken.None);

        Assert.Empty(cagrilanlar);
        var uyari = Assert.Single(uyarilar);
        Assert.Equal("backup.restore_task_owner_required", uyari.MessageKey);
        Assert.Equal("kotu", uyari.Target);

        // SESSİZ DEĞİL: XML çıkarma klasöründe DURUYOR (sahip aynı işi tekrarlayabilsin).
        Assert.True(File.Exists(Path.Combine(kok, "tasks", "000.xml")));
    }

    /// <summary>
    /// FAZLA KAPATMA NÖBETÇİSİ: sahibin tam geri yüklemesi görevlerin HEPSİNİ kurmaya devam eder.
    /// Bu olmadan "hiçbir görevi kurma" çözümü de yeşil görünür ve felaket kurtarmayı öldürürdü
    /// (gecelik yedek görevi dahil).
    /// </summary>
    [Fact]
    public async Task Sunucu_sahibinin_geri_yuklemesi_gorevleri_KURMAYA_DEVAM_EDER()
    {
        using var alan = new Alan();
        var kok = alan.GorevliCikarmaKlasoru("AdaPanel-Gecelik", ZararliGorevXml);

        var uyarilar = new List<BackupRestoreWarningDto>();
        var gonderilen = new List<(string Ad, string Xml)>();

        await BackupService.TryRestoreScheduledTasksAsync(
            kok,
            caginSahibiMi: true,
            uyarilar,
            (ad, xml, _) => { gonderilen.Add((ad, xml)); return Task.FromResult(Basarili()); },
            CancellationToken.None);

        Assert.Empty(uyarilar);
        var tek = Assert.Single(gonderilen);
        Assert.Equal("AdaPanel-Gecelik", tek.Ad);
        Assert.Contains("<Command>", tek.Xml, StringComparison.Ordinal);
    }

    /// <summary>
    /// Sahibin yolunda BAŞARISIZ kayıt hâlâ SESSİZ DEĞİL: eski koruma (dönüşü ölçme) duruyor.
    /// </summary>
    [Fact]
    public async Task Sahibin_yolunda_basarisiz_gorev_kaydi_UYARI_uretir()
    {
        using var alan = new Alan();
        var kok = alan.GorevliCikarmaKlasoru("AdaPanel-Gecelik", ZararliGorevXml);

        var uyarilar = new List<BackupRestoreWarningDto>();

        await BackupService.TryRestoreScheduledTasksAsync(
            kok,
            caginSahibiMi: true,
            uyarilar,
            (_, _, _) => Task.FromResult(new CommandResult { ExitCode = 1, StandardError = "Access denied" }),
            CancellationToken.None);

        var uyari = Assert.Single(uyarilar);
        Assert.Equal("backup.restore_task_failed", uyari.MessageKey);
    }

    /// <summary>
    /// UÇTAN UCA: gerçek <c>RestoreAsync</c> çağrısı, arşivdeki görevi kaydetmez ve durumu
    /// DÜRÜSTÇE "restored_with_errors" bırakır.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Bu test korumanın YOLDA OLDUĞUNU ölçer, kuralın var olduğunu değil. Yukarıdaki testler
    /// kararı doğrudan çağırıyor; burada ölçülen şey <c>RestoreAsync</c>'in o kararı GERÇEKTEN
    /// sorması. Bu depoda "koruma yazılmış ama o yolda çağrılmıyor" tekrar eden hata sınıfıdır.
    /// </para>
    /// <para>
    /// <b>Sahiplik çözümleyicisi VERİLMEDİ</b> — ve beklenen sonuç "sahip değil"dir. Eksik bir
    /// bağımlılığın SYSTEM yetkisini AÇMASI, korumayı DI kurulumunun yan etkisine bağlamak olurdu.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Gercek_geri_yukleme_yolunda_gorev_kaydi_sahiplik_sorusundan_GECER()
    {
        using var alan = new Alan();
        var zip = alan.Zip(new Dictionary<string, string>
        {
            ["tasks/index.json"] = "[{\"name\":\"AdaPanel-Gecelik\",\"entry\":\"tasks/000.xml\"}]",
            ["tasks/000.xml"] = ZararliGorevXml
        });
        var servis = alan.Servis(zip, "panel");

        var hedef = Path.Combine(Path.GetTempPath(), $"adapanel-hedef-{Guid.NewGuid():N}");
        var kayit = await servis.RestoreAsync(new RestoreBackupRequest { BackupId = "b1", TargetPath = hedef });

        Assert.Equal("restored_with_errors", kayit.Status);
        Assert.Contains(kayit.Warnings, u => u.MessageKey == "backup.restore_task_owner_required");
    }

    // =====================================================================================
    // 3) UYGULAMA MAĞAZASI — keyfi mutlak hedef
    // =====================================================================================

    [Theory]
    [InlineData("config")]
    [InlineData("")]
    public async Task Magaza_kurulumu_panelin_veri_kokune_YAZAMAZ(string altKlasor)
    {
        using var alan = new Alan();
        var servis = alan.MagazaServisi();
        var hedef = altKlasor.Length == 0 ? alan.Kok : Path.Combine(alan.Kok, altKlasor);

        var hata = await Assert.ThrowsAsync<AppException>(() => servis.InstallAsync(
            new InstallAppTemplateRequest { TemplateId = "adminer", TargetPath = hedef }));

        Assert.Equal("store.target_path_forbidden", hata.MessageKey);
    }

    /// <summary>
    /// FAZLA KAPATMA NÖBETÇİSİ: yöneticinin site kökü dışına (ayrı disk) kurması MEŞRUDUR ve bu
    /// kapıdan GEÇER. Kurulum ilerideki bir adımda (indirme) düşebilir; ölçülen şey reddin
    /// BURADA olmadığıdır.
    /// </summary>
    [Fact]
    public async Task Magaza_kurulumu_mesru_bir_klasore_izin_VERIR()
    {
        using var alan = new Alan();
        var servis = alan.MagazaServisi();

        var hata = await Record.ExceptionAsync(() => servis.InstallAsync(
            new InstallAppTemplateRequest
            {
                TemplateId = "adminer",
                TargetPath = Path.Combine(Path.GetTempPath(), $"adapanel-uygulama-{Guid.NewGuid():N}")
            }));

        Assert.NotEqual("store.target_path_forbidden", (hata as AppException)?.MessageKey);
    }

    // =====================================================================================
    // 4) SİTE FİZİKSEL YOLU — yönetici dalı
    // =====================================================================================

    [Fact]
    public async Task Yonetici_belge_kokunu_SURUCU_KOKUNE_ceviremez()
    {
        using var alan = new Alan();
        var saglayici = new SahteSaglayici();
        var denetleyici = alan.SiteDenetleyicisi(saglayici);

        var hata = await Assert.ThrowsAsync<AppException>(() => denetleyici.SetDocumentRootAsync(
            "shop",
            new UpdateDocumentRootRequest { PhysicalPath = @"C:\" },
            CancellationToken.None));

        Assert.Equal("sites.physical_path_forbidden", hata.MessageKey);
        Assert.Empty(saglayici.Yollar);
    }

    [Fact]
    public async Task Yonetici_belge_kokunu_panelin_veri_kokune_ceviremez()
    {
        using var alan = new Alan();
        var saglayici = new SahteSaglayici();
        var denetleyici = alan.SiteDenetleyicisi(saglayici);

        var hata = await Assert.ThrowsAsync<AppException>(() => denetleyici.SetDocumentRootAsync(
            "shop",
            new UpdateDocumentRootRequest { PhysicalPath = Path.Combine(alan.Kok, "config") },
            CancellationToken.None));

        Assert.Equal("sites.physical_path_forbidden", hata.MessageKey);
        Assert.Empty(saglayici.Yollar);
    }

    /// <summary>
    /// FAZLA KAPATMA NÖBETÇİSİ: yöneticinin belge kökünü AYRI BİR DİSKE taşıması meşrudur ve
    /// çalışmaya devam eder. Ayrıca varsayılan site kökünün altı da geçerlidir.
    /// </summary>
    [Theory]
    [InlineData(@"D:\siteler\musteri.com\public")]
    [InlineData(@"C:\inetpub\wwwroot\musteri.com")]
    public async Task Yoneticinin_mesru_belge_koku_KABUL_edilir(string yol)
    {
        using var alan = new Alan();
        var saglayici = new SahteSaglayici();
        var denetleyici = alan.SiteDenetleyicisi(saglayici);

        var yanit = await denetleyici.SetDocumentRootAsync(
            "shop",
            new UpdateDocumentRootRequest { PhysicalPath = yol },
            CancellationToken.None);

        Assert.IsType<OkObjectResult>(yanit.Result);
        Assert.Equal([yol], saglayici.Yollar);
    }

    /// <summary>
    /// Site OLUŞTURMA da aynı kapıdan geçer: yalnız belge kökünü kapatmak, aynı sonucu
    /// oluşturmadan veren bir yan kapı bırakırdı.
    /// </summary>
    [Fact]
    public async Task Yonetici_fiziksel_yolu_SURUCU_KOKU_olan_site_ACAMAZ()
    {
        using var alan = new Alan();
        var denetleyici = alan.SiteDenetleyicisi(new SahteSaglayici());

        var hata = await Assert.ThrowsAsync<AppException>(() => denetleyici.CreateSiteAsync(
            new CreateSiteRequest { Name = "kotu", HostName = "kotu.test", PhysicalPath = @"C:\" },
            null!,
            null!,
            null!,
            null!,
            null!,
            CancellationToken.None));

        Assert.Equal("sites.physical_path_forbidden", hata.MessageKey);
    }

    /// <summary>
    /// FAZLA KAPATMA NÖBETÇİSİ: fiziksel yolu BOŞ bırakan (panelin türetmesini isteyen) yönetici
    /// isteği bu kapıda REDDEDİLMEZ — en olağan site oluşturma isteği budur.
    /// </summary>
    [Fact]
    public async Task Fiziksel_yolu_BOS_birakan_yonetici_istegi_bu_kapida_reddedilmez()
    {
        using var alan = new Alan();
        var denetleyici = alan.SiteDenetleyicisi(new SahteSaglayici());

        var hata = await Record.ExceptionAsync(() => denetleyici.CreateSiteAsync(
            new CreateSiteRequest { Name = "iyi", HostName = "iyi.test", PhysicalPath = string.Empty },
            null!,
            null!,
            null!,
            null!,
            null!,
            CancellationToken.None));

        Assert.NotEqual("sites.physical_path_forbidden", (hata as AppException)?.MessageKey);
    }

    // =====================================================================================
    // Yardımcılar
    // =====================================================================================

    /// <summary>
    /// Gerçek bir <c>Export-ScheduledTask</c> çıktısının biçiminde, ama SYSTEM olarak keyfi komut
    /// çalıştıran bir görev tanımı — ölçülen tehdidin ta kendisi.
    /// </summary>
    private const string ZararliGorevXml = """
<?xml version="1.0" encoding="UTF-16"?>
<Task xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <Principals><Principal id="Author"><UserId>S-1-5-18</UserId><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
  <Actions Context="Author"><Exec><Command>cmd.exe</Command><Arguments>/c net user saldirgan P@rola /add</Arguments></Exec></Actions>
</Task>
""";

    private static CommandResult Basarili() => new() { ExitCode = 0 };

    private sealed class Alan : IDisposable
    {
        public Alan()
        {
            Kok = Path.Combine(Path.GetTempPath(), $"adapanel-sahiplik-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Kok);
            Depo = new JsonFileStore(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = Kok }));
        }

        public string Kok { get; }

        public JsonFileStore Depo { get; }

        public string Zip(IDictionary<string, string> girdiler)
        {
            var yol = Path.Combine(Kok, $"yedek-{Guid.NewGuid():N}.zip");
            using var akis = new FileStream(yol, FileMode.Create, FileAccess.Write);
            using var arsiv = new ZipArchive(akis, ZipArchiveMode.Create);
            foreach (var (ad, icerik) in girdiler)
            {
                using var yazici = new StreamWriter(arsiv.CreateEntry(ad, CompressionLevel.Fastest).Open());
                yazici.Write(icerik);
            }

            return yol;
        }

        /// <summary>Panel yedeğinin ÇIKARILMIŞ hâli: <c>tasks/index.json</c> + tek görev XML'i.</summary>
        public string GorevliCikarmaKlasoru(string gorevAdi, string xml)
        {
            var kok = Path.Combine(Kok, $"cikarma-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path.Combine(kok, "tasks"));
            File.WriteAllText(Path.Combine(kok, "tasks", "000.xml"), xml);
            File.WriteAllText(
                Path.Combine(kok, "tasks", "index.json"),
                $"[{{\"name\":\"{gorevAdi}\",\"entry\":\"tasks/000.xml\"}}]");
            return kok;
        }

        public BackupService Servis(string zipYolu, string tur)
        {
            Depo.WriteAsync("config/backups.json", new[]
            {
                new BackupRecordDto
                {
                    Id = "b1",
                    Name = Path.GetFileName(zipYolu),
                    Type = tur,
                    Source = "panel",
                    Path = zipYolu,
                    Format = "zip",
                    CreatedAtUtc = DateTime.UtcNow
                }
            }).GetAwaiter().GetResult();

            // BAGIMLILIKLAR null!: bu yol (panel yedegi, zip) IIS'e de veritabanina da HIC
            // dokunmaz. Bir gun dokunursa test NullReferenceException ile KIRILIR -- sessizce
            // gecmez; olcum yolunun daralmasi da gorunur olsun.
            return new BackupService(null!, null!, null!, null!, null!, Depo, null!);
        }

        public AppStoreService MagazaServisi() => new(
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = Kok }),
            NullLogger<AppStoreService>.Instance);

        public SitesController SiteDenetleyicisi(ISiteProvisioningService saglayici)
        {
            var denetleyici = new SitesController(
                null!,
                saglayici,
                null!,
                null!,
                new ThrowingCustomerScopeService(),
                null!,
                null!,
                null!,
                null!,
                new RecordingAuditService(),
                null!,
                Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = Kok }),
                NullLogger<SitesController>.Instance)
            {
                ControllerContext = new ControllerContext { HttpContext = YoneticiBaglami() }
            };

            return denetleyici;
        }

        private static DefaultHttpContext YoneticiBaglami() => new()
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                new Claim(ClaimTypes.Name, "yonetici"),
                new Claim(ClaimTypes.Role, nameof(UserRole.Administrator))
            ], "test"))
        };

        public void Dispose()
        {
            // HİÇBİR ŞEY SİLİNMEZ (bu takımın kuralı): özyinelemeli silme bir kavşağı takip edip
            // testin kendisini yıkıcı hâle getirmişti. Geçici alan %TEMP% altında BIRAKILIR.
        }
    }

    private sealed class SahteSaglayici : ISiteProvisioningService
    {
        public List<string> Yollar { get; } = [];

        public Task<SiteDto> SetDocumentRootAsync(string siteName, string physicalPath, CancellationToken cancellationToken = default)
        {
            Yollar.Add(physicalPath);
            return Task.FromResult(new SiteDto { Name = siteName, PhysicalPath = physicalPath });
        }

        public Task<SiteDto> CreateAndProvisionAsync(
            CreateSiteRequest request,
            Guid? actorUserId,
            string? ipAddress,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<SiteDto> EnableFrameworkRewriteAsync(string siteName, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SitePermissionRepairResultDto> RepairPermissionsAsync(string siteName, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }


}
