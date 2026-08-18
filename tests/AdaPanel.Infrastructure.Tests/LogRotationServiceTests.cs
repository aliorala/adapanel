using System.Diagnostics;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Logs;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Backup;
using AdaPanel.Infrastructure.Logs;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Log döndürmenin UÇTAN UCA davranışı: gerçek dosyalar, gerçek ZSTD, gerçek kilitler.
/// </summary>
/// <remarks>
/// <para>
/// Turlar PANEL hedefi üzerinden koşuluyor çünkü panelin günlük klasörü veri kökünden türer ve
/// testten yönlendirilebilir. Site hedefinin klasörü IIS yapılandırmasından çözülür
/// (<c>IisLogPaths</c>) ve testten yönlendirilemez; bu yüzden site tarafında yalnız BAĞLANTI
/// doğrulanıyor (hedef listeye giriyor mu, klasörü <c>W3SVC&lt;id&gt;</c> mi). İşleme mantığı iki
/// hedef için aynı koddur.
/// </para>
/// <para>
/// <b>Tur testlerinde site listesi bilerek BOŞ:</b> sahte bir site verilseydi servis gerçek
/// <c>C:\inetpub\logs\LogFiles\W3SVCn</c> klasörünü işlemeye çalışırdı — yükseltilmiş bir makinede
/// test, sunucunun GERÇEK IIS günlüklerini döndürürdü.
/// </para>
/// </remarks>
public sealed class LogRotationServiceTests : IDisposable
{
    private readonly string veriKoku = Path.Combine(Path.GetTempPath(), $"ada-logrot-{Guid.NewGuid():N}");
    private readonly RecordingAuditService denetim = new();

    private string PanelGunlukKlasoru => Path.Combine(veriKoku, "logs");

    public LogRotationServiceTests() => Directory.CreateDirectory(PanelGunlukKlasoru);

    public void Dispose()
    {
        try { Directory.Delete(veriKoku, recursive: true); } catch { /* en iyi çaba */ }
    }

    /// <summary>
    /// Temel akış: kapanmış günlükler ZSTD'ye çevrilir, İÇERİK KAYBOLMAZ, aktif dosya durur.
    /// </summary>
    /// <remarks>
    /// <b>Aktif dosya bilerek ESKİ seçildi (5 gün).</b> Gerçek ve tehlikeli durum budur: trafiği
    /// olmayan bir sitede IIS günün dosyasını AÇIK tutmaya devam eder ama dosyanın son yazma
    /// zamanı günlerce ilerlemez. Yalnızca yaşa bakan bir politika tam da bu dosyayı — canlı
    /// tutulanı — döndürmeye kalkardı. Aktif dosya "taze" seçilseydi test bu hatayı göremezdi.
    /// </remarks>
    [Fact]
    public async Task Kapanmis_gunlukler_arsivlenir_aktif_dosya_KORUNUR()
    {
        var eski = GunlukYaz("adapanel-20260810.log", Tekrarli("2026-08-10 INF istek islendi"), gunOnce: 30);
        var dun = GunlukYaz("adapanel-20260817.log", Tekrarli("2026-08-17 INF istek islendi"), gunOnce: 20);
        var aktif = GunlukYaz("adapanel-20260818.log", Tekrarli("2026-08-18 INF aktif"), gunOnce: 5);
        var eskiIcerik = File.ReadAllText(eski);
        var aktifIcerik = File.ReadAllText(aktif);

        var servis = Servis();
        await AyarlaAsync(servis, panelYasGun: 1);
        var durum = await servis.RunAsync(null);

        // Aktif dosyaya DOKUNULMADI — yaş eşiğini fazlasıyla aşmış olmasına rağmen.
        Assert.True(File.Exists(aktif), "Yazilmakta olan gunluk dondurulmus.");
        Assert.False(File.Exists(aktif + ".zst"));
        Assert.Equal(aktifIcerik, File.ReadAllText(aktif));

        // Kapanmış olanlar arşive döndü.
        Assert.False(File.Exists(eski));
        Assert.False(File.Exists(dun));
        Assert.True(File.Exists(eski + ".zst"));
        Assert.True(File.Exists(dun + ".zst"));

        var hedef = PanelHedefi(durum);
        Assert.Equal(2, hedef.RotatedFiles);
        Assert.Equal(0, hedef.SkippedLockedFiles);
        Assert.Equal(0, hedef.SkippedUnsafeFiles);
        Assert.True(hedef.FreedBytes > 0, "Sikistirma hic yer kazandirmamis gorunuyor.");

        // İÇERİK GERİ ALINABİLİR — yer kazanmak, veriyi kaybetmek pahasına olamaz.
        var geri = Path.Combine(veriKoku, "geri.log");
        await BackupDumpCompression.DecompressFileAsync(eski + ".zst", geri);
        Assert.Equal(eskiIcerik, File.ReadAllText(geri));
    }

    /// <summary>
    /// BAŞKA BİR SÜREÇ TUTUYORSA dosya döndürülmez, KORUNUR ve bu durum SAYILIR.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Windows'ta IIS/Serilog o an yazdığı günlüğü açık tutar. Kilitli dosya bir HATA değildir; ama
    /// sessiz de kalmamalı — "0 dosya döndürüldü" satırı, politikanın hiç çalışmadığı bir arızayla
    /// aynı görünürdü. Bu depoda tam olarak bu karışıklık yaşandı ("0 veritabanı yedeklendi").
    /// </para>
    /// <para>
    /// Tutucu bilerek <c>FileShare.Delete</c> ile açılıyor: bu, düz <c>File.Delete</c>'in BAŞARILI
    /// olacağı ve canlı günlüğü yok edeceği kiptir.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Baska_surecin_tuttugu_gunluk_dondurulmez_ve_SAYILIR()
    {
        var kilitli = GunlukYaz("adapanel-20260810.log", Tekrarli("kilitli satir"), gunOnce: 8);
        var serbest = GunlukYaz("adapanel-20260811.log", Tekrarli("serbest satir"), gunOnce: 7);
        GunlukYaz("adapanel-20260818.log", "aktif", gunOnce: 0);
        var kilitliIcerik = File.ReadAllText(kilitli);

        var servis = Servis();
        await AyarlaAsync(servis, panelYasGun: 1);

        LogRotationStatusDto durum;
        using (var tutucu = new FileStream(kilitli, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
        {
            durum = await servis.RunAsync(null);
        }

        // Kilitli dosya SAĞ ve İÇERİĞİ AYNI.
        Assert.True(File.Exists(kilitli), "Baska surecin tuttugu gunluk SILINDI.");
        Assert.Equal(kilitliIcerik, File.ReadAllText(kilitli));
        Assert.False(File.Exists(kilitli + ".zst"));

        // Serbest olan normal şekilde döndü: kilit, turu tümden durdurmuyor.
        Assert.False(File.Exists(serbest));
        Assert.True(File.Exists(serbest + ".zst"));

        var hedef = PanelHedefi(durum);
        Assert.Equal(1, hedef.RotatedFiles);
        Assert.Equal(1, hedef.SkippedLockedFiles);
    }

    /// <summary>
    /// KLASÖRÜN KENDİSİ BİR KAVŞAKSA hiçbir şeye dokunulmaz ve durum HATA olarak görünür.
    /// </summary>
    /// <remarks>
    /// Kavşağın arkasında müşterinin site kökü olabilir. Bu depoda özyinelemeli silme tam olarak
    /// böyle bir bağlantı üzerinden müşteri sitesini götürebilecek hataya yol açtı.
    /// </remarks>
    [Fact]
    public async Task Gunluk_klasoru_bir_KAVSAK_ise_hicbir_sey_silinmez()
    {
        // Müşterinin gerçek klasörü: içinde günlük GÖRÜNÜMLÜ ama aslında müşteriye ait dosyalar.
        var musteri = Path.Combine(veriKoku, "musteri-sitesi");
        Directory.CreateDirectory(musteri);
        File.WriteAllText(Path.Combine(musteri, "index.php"), "site");
        var musteriGunluk = Path.Combine(musteri, "erisim.log");
        File.WriteAllText(musteriGunluk, Tekrarli("musteri verisi"));
        File.SetLastWriteTimeUtc(musteriGunluk, DateTime.UtcNow.AddDays(-400));
        var ikinci = Path.Combine(musteri, "eski.log");
        File.WriteAllText(ikinci, Tekrarli("ikinci"));
        File.SetLastWriteTimeUtc(ikinci, DateTime.UtcNow.AddDays(-401));

        // Panelin günlük klasörünü bu klasöre bağla.
        Directory.Delete(PanelGunlukKlasoru);
        JunctionOlustur(PanelGunlukKlasoru, musteri);
        Assert.True(Directory.Exists(PanelGunlukKlasoru), "Junction kurulamadi; test onkosulu saglanmadi.");

        var servis = Servis();
        await AyarlaAsync(servis, panelYasGun: 1);
        var durum = await servis.RunAsync(null);

        // MÜŞTERİNİN HİÇBİR DOSYASI kaybolmadı, sıkıştırılmadı.
        Assert.True(File.Exists(Path.Combine(musteri, "index.php")));
        Assert.True(File.Exists(musteriGunluk));
        Assert.True(File.Exists(ikinci));
        Assert.Empty(Directory.GetFiles(musteri, "*.zst"));

        var hedef = PanelHedefi(durum);
        Assert.Equal("error", hedef.Status);
        Assert.Equal("directory_is_reparse_point", hedef.Error);
        Assert.Equal(0, hedef.RotatedFiles);
        Assert.Equal(0, hedef.DeletedArchives);
    }

    /// <summary>ALT KLASÖRDEKİ günlüklere dokunulmaz: dolaşım yalnız üst düzeydir, özyineleme yok.</summary>
    [Fact]
    public async Task Alt_klasordeki_gunluklere_dokunulmaz()
    {
        var alt = Path.Combine(PanelGunlukKlasoru, "arsiv");
        Directory.CreateDirectory(alt);
        var derin = Path.Combine(alt, "adapanel-20260101.log");
        File.WriteAllText(derin, Tekrarli("derindeki"));
        File.SetLastWriteTimeUtc(derin, DateTime.UtcNow.AddDays(-300));

        GunlukYaz("adapanel-20260810.log", Tekrarli("ust duzey"), gunOnce: 8);
        GunlukYaz("adapanel-20260818.log", "aktif", gunOnce: 0);

        var servis = Servis();
        await AyarlaAsync(servis, panelYasGun: 1);
        await servis.RunAsync(null);

        Assert.True(File.Exists(derin), "Alt klasordeki gunluk islendi — ozyineleme sizmis.");
        Assert.False(File.Exists(derin + ".zst"));
    }

    /// <summary>Saklama: adet sınırı aşıldığında EN ESKİ arşivler silinir.</summary>
    [Fact]
    public async Task Adet_siniri_en_eski_arsivleri_temizler()
    {
        for (var i = 1; i <= 5; i++)
        {
            GunlukYaz($"adapanel-2026080{i}.log", Tekrarli($"gun {i}"), gunOnce: 20 - i);
        }

        GunlukYaz("adapanel-20260818.log", "aktif", gunOnce: 0);

        var servis = Servis();
        await AyarlaAsync(servis, panelYasGun: 1, saklanacakAdet: 2);
        var durum = await servis.RunAsync(null);

        var arsivler = Directory.GetFiles(PanelGunlukKlasoru, "*.log.zst")
            .Select(yol => Path.GetFileName(yol))
            .OrderBy(ad => ad, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(2, arsivler.Length);
        // En YENİ ikisi kalmalı (4 ve 5).
        Assert.Equal(["adapanel-20260804.log.zst", "adapanel-20260805.log.zst"], arsivler);

        var hedef = PanelHedefi(durum);
        Assert.Equal(5, hedef.RotatedFiles);
        Assert.Equal(3, hedef.DeletedArchives);
    }

    /// <summary>
    /// Sıkıştırma KAPALIYKEN hiçbir ham günlük silinmez.
    /// </summary>
    /// <remarks>
    /// Bilinçli tasarım: "sıkıştırma kapalı" bu panelde "ham günlüğü sil" anlamına GELMEZ. Aksi
    /// hâlde tek bir onay kutusu, erişim kayıtlarını geri dönüşsüz silen bir moda dönüşürdü.
    /// Kapalıyken yalnız var olan arşivlere saklama kuralı uygulanır.
    /// </remarks>
    [Fact]
    public async Task Sikistirma_kapaliyken_ham_gunluk_silinmez()
    {
        var eski = GunlukYaz("adapanel-20260101.log", Tekrarli("cok eski"), gunOnce: 300);
        GunlukYaz("adapanel-20260818.log", "aktif", gunOnce: 0);

        var servis = Servis();
        await AyarlaAsync(servis, panelYasGun: 1, sikistir: false);
        var durum = await servis.RunAsync(null);

        Assert.True(File.Exists(eski), "Sikistirma kapaliyken ham gunluk SILINDI.");
        Assert.Equal(0, PanelHedefi(durum).RotatedFiles);
    }

    /// <summary>Politika KAPALIYKEN elle çalıştırma da reddedilir — ayar bir temenni değil, kapıdır.</summary>
    [Fact]
    public async Task Politika_kapaliyken_elle_calistirma_reddedilir()
    {
        var eski = GunlukYaz("adapanel-20260101.log", Tekrarli("veri"), gunOnce: 300);
        GunlukYaz("adapanel-20260818.log", "aktif", gunOnce: 0);

        var servis = Servis();
        await AyarlaAsync(servis, panelYasGun: 1, etkin: false);

        var hata = await Assert.ThrowsAsync<AppException>(() => servis.RunAsync(null));

        Assert.Equal("logs.rotation_disabled", hata.Message);
        Assert.True(File.Exists(eski));
    }

    /// <summary>Gerçekten bir şey yapıldıysa DENETİM KAYDI bırakılır; boş turda kayıt yazılmaz.</summary>
    [Fact]
    public async Task Denetim_kaydi_yalniz_is_yapildiginda_yazilir()
    {
        GunlukYaz("adapanel-20260818.log", "aktif", gunOnce: 0);

        var servis = Servis();
        await AyarlaAsync(servis, panelYasGun: 1);

        // Döndürülecek hiçbir şey yok → gürültü üretilmemeli.
        await servis.RunAsync(null);
        Assert.Empty(denetim.Actions);

        GunlukYaz("adapanel-20260810.log", Tekrarli("veri"), gunOnce: 8);
        await servis.RunAsync(null);

        Assert.Contains("logs.rotation_executed", denetim.Actions);
    }

    /// <summary>Yaş eşiği DOLMAMIŞ dosyalar döndürülmez — politika, "eline geçeni sıkıştır" değildir.</summary>
    [Fact]
    public async Task Yasi_dolmamis_gunluk_dondurulmez()
    {
        var yeni = GunlukYaz("adapanel-20260817.log", Tekrarli("dun"), gunOnce: 1);
        GunlukYaz("adapanel-20260818.log", "aktif", gunOnce: 0);

        var servis = Servis();
        await AyarlaAsync(servis, panelYasGun: 30);
        await servis.RunAsync(null);

        Assert.True(File.Exists(yeni));
        Assert.False(File.Exists(yeni + ".zst"));
    }

    /// <summary>Site hedefi listeye girer ve klasörü IIS düzenindeki <c>W3SVC&lt;id&gt;</c>'dir.</summary>
    [Fact]
    public async Task Site_hedefi_W3SVC_klasorune_baglanir()
    {
        var servis = Servis(new SahteSiteServisi([new SiteDto { Id = 42, Name = "ornek.com" }]));

        var durum = await servis.GetStatusAsync();

        var site = Assert.Single(durum.Targets, t => t.Key == "site:ornek.com");
        Assert.Equal("ornek.com", site.SiteName);
        Assert.EndsWith(@"\W3SVC42", site.Directory);
        Assert.Contains(durum.Targets, t => t.Key == "panel");
    }

    /// <summary>Ayarlar diske YAZILIR ve yeni bir servis örneği tarafından okunur (kalıcılık).</summary>
    [Fact]
    public async Task Ayarlar_kalici_olarak_saklanir()
    {
        await AyarlaAsync(Servis(), panelYasGun: 5, siteYasGun: 45, saklanacakAdet: 9);

        var durum = await Servis().GetStatusAsync();

        Assert.Equal(5, durum.Settings.PanelLogMaxAgeDays);
        Assert.Equal(45, durum.Settings.SiteLogMaxAgeDays);
        Assert.Equal(9, durum.Settings.SiteKeepCount);
        Assert.Equal(9, durum.Settings.PanelKeepCount);
    }

    /// <summary>
    /// Panel günlüğünün 14 GÜNLÜK SAKLAMA SÖZÜ, arşivlemeye geçince de sürüyor.
    /// </summary>
    /// <remarks>
    /// Serilog panel günlüğünü <c>retainedFileCountLimit: 14</c> ile temizliyor ama bunu
    /// <c>adapanel-*.log</c> desenine göre yapar. Bir günü <c>.log.zst</c> yaptığımızda dosya o
    /// desene uymaz ve o günün kaydı Serilog'un politikasından SESSİZCE kaçardı: var olan bir
    /// saklama sözü, kimse istemeden "sonsuza kadar sakla"ya dönerdi. Panel günlüğü IP ve kullanıcı
    /// adı taşıdığı için bu teknik bir ayrıntı değil, politika değişikliğidir.
    /// </remarks>
    [Fact]
    public void Panel_arsivleri_varsayilan_olarak_Serilog_ile_AYNI_14_gunu_saklar()
    {
        var varsayilan = new LogRotationSettingsDto();

        Assert.Equal(14, LogRotationPlanner.KeepCountFor(varsayilan, isPanelTarget: true));
        // Site tarafında panelin verilmiş bir sözü YOK; oraya adet sınırı dayatmak erişim
        // kaydını silmek olurdu.
        Assert.Equal(0, LogRotationPlanner.KeepCountFor(varsayilan, isPanelTarget: false));
    }

    /// <summary>Zamanlama: hiç çalışmadıysa hemen; çalıştıysa aralık dolmadan tekrar çalışmaz.</summary>
    [Fact]
    public void Zamani_geldi_mi_karari_aralik_ile_verilir()
    {
        var simdi = new DateTime(2026, 8, 18, 12, 0, 0, DateTimeKind.Utc);
        var durum = new LogRotationService.RotationState { Settings = { IntervalHours = 24 } };

        Assert.True(LogRotationService.IsDue(durum, simdi));

        durum.LastRunUtc = simdi.AddHours(-23);
        Assert.False(LogRotationService.IsDue(durum, simdi));

        durum.LastRunUtc = simdi.AddHours(-24);
        Assert.True(LogRotationService.IsDue(durum, simdi));
    }

    // --- düzenek ---

    private LogRotationService Servis(IIisSiteService? siteServisi = null) =>
        new(siteServisi ?? new SahteSiteServisi([]),
            new JsonFileStore(Ayarlar()),
            denetim,
            Ayarlar(),
            NullLogger<LogRotationService>.Instance);

    // Tam nitelenmiş: "Options" adı bu dosyada AdaPanel.Infrastructure.Options ad alanıyla çakışıyor.
    private IOptions<PanelRuntimeOptions> Ayarlar() =>
        Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = veriKoku });

    private static async Task AyarlaAsync(
        ILogRotationService servis,
        int panelYasGun,
        int siteYasGun = 30,
        int saklanacakAdet = 0,
        bool sikistir = true,
        bool etkin = true)
    {
        await servis.UpdateSettingsAsync(new UpdateLogRotationSettingsRequest
        {
            Enabled = etkin,
            Compress = sikistir,
            PanelLogMaxAgeDays = panelYasGun,
            SiteLogMaxAgeDays = siteYasGun,
            // Turlar panel hedefi uzerinden kosuyor -> adet sinirini panel tarafina veriyoruz.
            PanelKeepCount = saklanacakAdet,
            SiteKeepCount = saklanacakAdet,
            MaxFileSizeMB = 0,
            MaxTotalSizeMB = 0,
            IntervalHours = 24
        });
    }

    private string GunlukYaz(string ad, string icerik, int gunOnce)
    {
        var yol = Path.Combine(PanelGunlukKlasoru, ad);
        File.WriteAllText(yol, icerik);
        File.SetLastWriteTimeUtc(yol, DateTime.UtcNow.AddDays(-gunOnce));
        return yol;
    }

    /// <summary>Sıkıştırmanın ölçülebilir bir kazanç üretmesi için tekrarlı, gerçekçi bir gövde.</summary>
    private static string Tekrarli(string satir) =>
        string.Join(Environment.NewLine, Enumerable.Repeat(satir, 400));

    private static LogRotationTargetDto PanelHedefi(LogRotationStatusDto durum) =>
        Assert.Single(durum.Targets, t => t.Key == "panel");

    private static void JunctionOlustur(string link, string hedef)
    {
        var betik = Path.Combine(Path.GetTempPath(), $"ada-junction-{Guid.NewGuid():N}.ps1");
        File.WriteAllText(
            betik,
            $"$ErrorActionPreference='Stop'\nNew-Item -ItemType Junction -Path '{link.Replace("'", "''")}' -Target '{hedef.Replace("'", "''")}' | Out-Null");
        try
        {
            using var surec = Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{betik}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            })!;
            surec.WaitForExit(60_000);
        }
        finally
        {
            try { File.Delete(betik); } catch { /* en iyi çaba */ }
        }
    }

    /// <summary>
    /// Site listesi testten verilir. Tur testlerinde BOŞ geçilir: doluyken servis gerçek
    /// <c>C:\inetpub\logs\LogFiles</c> altına bakardı.
    /// </summary>
    private sealed class SahteSiteServisi(IReadOnlyCollection<SiteDto> siteler) : IIisSiteService
    {
        public Task<IReadOnlyCollection<SiteDto>> GetSitesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(siteler);

        public Task<SiteDto> GetSiteAsync(string siteName, CancellationToken cancellationToken = default) =>
            siteler.FirstOrDefault(s => string.Equals(s.Name, siteName, StringComparison.OrdinalIgnoreCase)) is { } site
                ? Task.FromResult(site)
                : throw new AppException("sites.not_found", 404, 404);

        public Task<SiteDto> CreateSiteAsync(CreateSiteRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<SiteDto> UpdatePhysicalPathAsync(string siteName, string physicalPath, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task DeleteSiteAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<SiteDto> StartSiteAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<SiteDto> StopSiteAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<SiteDto> AddBindingAsync(string siteName, SiteBindingRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<SiteDto> RemoveBindingAsync(string siteName, RemoveSiteBindingRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyCollection<SiteDto>> ImportExistingSitesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
