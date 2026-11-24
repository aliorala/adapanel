using System.Reflection;
using System.Security.Claims;
using AdaPanel.Api.Controllers;
using AdaPanel.Api.Security;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Audit;
using AdaPanel.Core.Dtos.Ftp;
using AdaPanel.Core.Dtos.Licensing;
using AdaPanel.Core.Dtos.Scheduler;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Dtos.Statistics;
using AdaPanel.Core.Dtos.Users;
using AdaPanel.Core.Entities;
using AdaPanel.Core.Enums;
using AdaPanel.Core.Interfaces;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Advisor;
using AdaPanel.Infrastructure.Files;
using AdaPanel.Infrastructure.Licensing;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Security;
using AdaPanel.Infrastructure.Services;
using AdaPanel.Infrastructure.Subscriptions;
using AdaPanel.Infrastructure.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Dalga 2b "plan limitleri": askının kaynak kapsamı (M2), FTP kotası (M3), panel yazımlarında disk
/// kotası (M3), lisans kapısı (M4) ve kullanıcı satırı + denetim atomikliği (G4 bakiyesi).
/// </summary>
public sealed class PlanLimitsW2bTests : IDisposable
{
    private readonly string kok = Path.Combine(Path.GetTempPath(), "adapanel-w2b-" + Guid.NewGuid().ToString("N"));

    public PlanLimitsW2bTests() => Directory.CreateDirectory(kok);

    public void Dispose()
    {
        try { Directory.Delete(kok, recursive: true); } catch { /* temizlik zorunlu değil */ }
    }

    // ------------------------------------------------------------------------------------------
    // M2: askı FTP + cron'u da kapatır, önceki hali kaydeder, yalnız açık olanı geri açar
    // ------------------------------------------------------------------------------------------

    private sealed class Duzenek
    {
        public required CustomerScopeService Kapsamlar { get; init; }
        public required SubscriptionEnforcementService Servis { get; init; }
        public required DurumluSiteler Siteler { get; init; }
        public required Dictionary<string, (string Site, bool Etkin)> Ftp { get; init; }
        public required Dictionary<string, string> Gorevler { get; init; }
        public required AskiDenetimKaydiDurustlukTests.YakalayanDenetim Denetim { get; init; }
        public required HashSet<string> FtpKirilan { get; init; }
        public required SqliteConnection Baglanti { get; init; }
        public required AdaPanelDbContext Db { get; init; }
    }

    private Duzenek Kur(Action<DurumluSiteler>? siteler = null)
    {
        var sahteSiteler = new DurumluSiteler(Path.Combine(kok, "siteler"));
        siteler?.Invoke(sahteSiteler);
        var store = new JsonFileStore(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = kok }));
        var kapsamlar = new CustomerScopeService(store, new AskiDenetimKaydiDurustlukTests.PlansizServis(), new AskiDenetimKaydiDurustlukTests.TavansizServis(), sahteSiteler);
        var denetim = new AskiDenetimKaydiDurustlukTests.YakalayanDenetim();
        var baglanti = new SqliteConnection("Filename=:memory:");
        baglanti.Open();
        var db = new AdaPanelDbContext(new DbContextOptionsBuilder<AdaPanelDbContext>().UseSqlite(baglanti).Options);
        db.Database.EnsureCreated();

        var ftp = new Dictionary<string, (string Site, bool Etkin)>(StringComparer.OrdinalIgnoreCase);
        var gorevler = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var ftpKirilan = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var ftpServisi = Fake<IFtpService>((metot, args) => metot.Name switch
        {
            nameof(IFtpService.GetFtpUsersAsync) => Task.FromResult<IReadOnlyCollection<FtpUserDto>>(
                ftp.Select(item => new FtpUserDto { UserName = item.Key, SiteName = item.Value.Site, IsEnabled = item.Value.Etkin }).ToArray()),
            nameof(IFtpService.SetFtpUserEnabledAsync) => FtpAyarla(ftpKirilan, ftp, (string)args![0]!, (bool)args[1]!),
            _ => throw new NotSupportedException(metot.Name)
        });
        var zamanlayici = Fake<ISchedulerService>((metot, args) => metot.Name switch
        {
            nameof(ISchedulerService.GetTasksAsync) => Task.FromResult<IReadOnlyCollection<ScheduledTaskDto>>(
                gorevler.Select(item => new ScheduledTaskDto { Name = item.Key, State = item.Value }).ToArray()),
            nameof(ISchedulerService.SetTaskEnabledAsync) => GorevAyarla(gorevler, (string)args![0]!, (bool)args[1]!),
            _ => throw new NotSupportedException(metot.Name)
        });

        var suspender = new SubscriptionResourceSuspender(
            kapsamlar, sahteSiteler, NullLogger<SubscriptionResourceSuspender>.Instance, ftpServisi, zamanlayici);
        var servis = new SubscriptionEnforcementService(
            kapsamlar, new TrafiksizIstatistik(), sahteSiteler, denetim, db,
            NullLogger<SubscriptionEnforcementService>.Instance, null, suspender);
        return new Duzenek
        {
            Kapsamlar = kapsamlar, Siteler = sahteSiteler, Ftp = ftp, Gorevler = gorevler, Denetim = denetim,
            FtpKirilan = ftpKirilan, Baglanti = baglanti, Db = db, Servis = servis
        };
    }

    private static Task FtpAyarla(HashSet<string> kirilan, Dictionary<string, (string Site, bool Etkin)> ftp, string ad, bool etkin)
    {
        if (kirilan.Contains(ad))
        {
            throw new InvalidOperationException("ftp hesabi islenemedi: " + ad);
        }

        ftp[ad] = (ftp[ad].Site, etkin);
        return Task.CompletedTask;
    }

    private static Task GorevAyarla(Dictionary<string, string> gorevler, string ad, bool etkin)
    {
        gorevler[ad] = etkin ? "Ready" : "Disabled";
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Aski_siteleri_FTP_hesaplarini_ve_gorevleri_kapatir_ve_yalniz_ONCE_ACIK_olanlari_geri_acar()
    {
        var d = Kur(s => { s.Ekle("a.test", "Started"); s.Ekle("b.test", "Stopped"); /* b'yi musteri elle durdurmus */ });
        using var _b = d.Baglanti; using var _d = d.Db;
        var kullanici = Guid.NewGuid();
        var oneki = $"u{kullanici:N}--";
        await d.Kapsamlar.SetScopeAsync(kullanici, ["a.test", "b.test"], 5, 1024);
        d.Ftp["acik"] = ("a.test", true);
        d.Ftp["elleKapali"] = ("a.test", false);   // musteri kapatmis
        d.Ftp["baskaMusteri"] = ("baska.test", true);
        d.Gorevler[oneki + "t1"] = "Ready";
        d.Gorevler[oneki + "t2"] = "Disabled";     // musteri kapatmis
        d.Gorevler["u" + Guid.NewGuid().ToString("N") + "--t3"] = "Ready"; // baska abonelik

        await d.Servis.SuspendAsync(kullanici, "manual");

        Assert.Equal("Stopped", d.Siteler.Durum["a.test"]);
        Assert.False(d.Ftp["acik"].Etkin);
        Assert.True(d.Ftp["baskaMusteri"].Etkin); // baska aboneligin hesabina DOKUNULMAZ
        Assert.Equal("Disabled", d.Gorevler[oneki + "t1"]);
        Assert.Contains(d.Gorevler, item => item.Key.EndsWith("--t3", StringComparison.Ordinal) && item.Value == "Ready");
        var askiKaydi = d.Denetim.Son("subscription.suspended");
        Assert.Contains("1 FTP account(s) and 1 scheduled task(s) disabled", askiKaydi, StringComparison.Ordinal);

        // Posta ve veritabani kullanicilari KAPATILAMIYOR: bu "yaptim" diye gecmez, adiyla yazilir.
        Assert.Contains("CANNOT BE SUSPENDED BY THE PANEL", askiKaydi, StringComparison.Ordinal);
        Assert.Contains("mail", askiKaydi, StringComparison.Ordinal);
        var durum = await d.Kapsamlar.GetSuspensionStateAsync(kullanici);
        Assert.NotNull(durum);
        Assert.Contains("mail", durum!.Unsupported);
        Assert.Contains("database", durum.Unsupported);

        await d.Servis.ResumeAsync(kullanici);

        Assert.Equal("Started", d.Siteler.Durum["a.test"]);
        Assert.Equal("Stopped", d.Siteler.Durum["b.test"]);       // elle durdurulan site ACILMAZ
        Assert.True(d.Ftp["acik"].Etkin);
        Assert.False(d.Ftp["elleKapali"].Etkin);                  // elle kapatilan hesap ACILMAZ
        Assert.Equal("Ready", d.Gorevler[oneki + "t1"]);
        Assert.Equal("Disabled", d.Gorevler[oneki + "t2"]);       // elle kapatilan gorev ACILMAZ
        Assert.Null(await d.Kapsamlar.GetSuspensionStateAsync(kullanici)); // her sey geri acildi: kayit temiz
        Assert.DoesNotContain("STILL", d.Denetim.Son("subscription.resumed"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Kapatilamayan_kaynak_BEKLEYEN_adim_olur_zorlama_turunda_yeniden_denenir_ve_dikkat_akisinda_gorunur()
    {
        var d = Kur(s => s.Ekle("a.test", "Started"));
        using var _b = d.Baglanti; using var _d = d.Db;
        var kullanici = Guid.NewGuid();
        await d.Kapsamlar.SetScopeAsync(kullanici, ["a.test"], 5, 1024);
        d.Ftp["hesap"] = ("a.test", true);
        d.Siteler.StopKirilan.Add("a.test");
        d.FtpKirilan.Add("hesap");

        await d.Servis.SuspendAsync(kullanici, "manual");

        // SESSIZ BASARI YOK: kayit hangisinin acik kaldigini soyler ...
        var kayit = d.Denetim.Son("subscription.suspended");
        Assert.Contains("STILL RUNNING", kayit, StringComparison.Ordinal);
        Assert.Contains("NOT DISABLED (will be retried): ftp:hesap", kayit, StringComparison.Ordinal);
        var bekleyen = await d.Kapsamlar.GetSuspensionStateAsync(kullanici);
        Assert.Contains("site:a.test", bekleyen!.PendingClose);
        Assert.Contains("ftp:hesap", bekleyen.PendingClose);

        // ... ve dikkat akisi (yoneticinin gordugu tek yer) KRITIK uyari uretir.
        var ozet = await DikkatAl(d.Kapsamlar);
        Assert.Contains(ozet.Items, item => item.Id == "subscription-suspension-pending" && item.Severity == "critical");
        Assert.Contains(ozet.Items, item => item.Id == "subscription-suspension-unsupported");

        // Hata giderilince bir sonraki zorlama turu YENIDEN DENER ve temizler.
        d.Siteler.StopKirilan.Clear();
        d.FtpKirilan.Clear();
        await d.Servis.EnforceAllAsync();

        Assert.Equal("Stopped", d.Siteler.Durum["a.test"]);
        Assert.False(d.Ftp["hesap"].Etkin);
        Assert.Empty((await d.Kapsamlar.GetSuspensionStateAsync(kullanici))!.PendingClose);
        Assert.Contains("Pending suspension steps retried", d.Denetim.Son("subscription.retry"), StringComparison.Ordinal);
        Assert.DoesNotContain((await DikkatAl(d.Kapsamlar)).Items, item => item.Id == "subscription-suspension-pending");
    }

    [Fact]
    public async Task Acilamayan_kaynak_bekler_ve_zorlama_turunda_tekrar_denenir()
    {
        var d = Kur(s => s.Ekle("a.test", "Started"));
        using var _b = d.Baglanti; using var _d = d.Db;
        var kullanici = Guid.NewGuid();
        await d.Kapsamlar.SetScopeAsync(kullanici, ["a.test"], 5, 1024);
        await d.Servis.SuspendAsync(kullanici, "manual");
        Assert.Equal("Stopped", d.Siteler.Durum["a.test"]);

        d.Siteler.StartKirilan.Add("a.test");
        await d.Servis.ResumeAsync(kullanici);

        Assert.Contains("STILL STOPPED", d.Denetim.Son("subscription.resumed"), StringComparison.Ordinal);
        Assert.Contains("site:a.test", (await d.Kapsamlar.GetSuspensionStateAsync(kullanici))!.PendingRestore);
        Assert.Contains((await DikkatAl(d.Kapsamlar)).Items, item => item.Id == "subscription-suspension-pending");

        d.Siteler.StartKirilan.Clear();
        await d.Servis.EnforceAllAsync();

        Assert.Equal("Started", d.Siteler.Durum["a.test"]);
        Assert.Null(await d.Kapsamlar.GetSuspensionStateAsync(kullanici));
    }

    [Fact]
    public async Task Yoneticinin_pasife_aldigi_musterinin_kaynaklari_yeniden_deneme_turunda_ACILMAZ()
    {
        var d = Kur(s => s.Ekle("a.test", "Started"));
        using var _b = d.Baglanti; using var _d = d.Db;
        var kullanici = Guid.NewGuid();
        d.Db.Users.Add(new PanelUser
        {
            Id = kullanici, UserName = "pasif", Email = "p@x.test", Role = UserRole.Customer, PreferredLanguage = "tr",
            IsActive = false, PasswordHash = "x", CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow
        });
        await d.Db.SaveChangesAsync();
        await d.Kapsamlar.SetScopeAsync(kullanici, ["a.test"], 5, 1024);
        d.Siteler.StopKirilan.Add("a.test");
        var suspender = new SubscriptionResourceSuspender(
            d.Kapsamlar, d.Siteler, NullLogger<SubscriptionResourceSuspender>.Instance);
        await suspender.CloseAsync(kullanici, ["a.test"], default);
        d.Siteler.StopKirilan.Clear();

        await d.Servis.EnforceAllAsync(); // askida DEGIL ama IsActive=false: kapali KALMALI, bekleyen kapatma tamamlanir

        Assert.Equal("Stopped", d.Siteler.Durum["a.test"]);
    }

    // ------------------------------------------------------------------------------------------
    // M3: plan disk tavani asimi bayragi (otomatik aski YOK) + panel yazimlarinda kota
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Disk_tavani_asilirsa_bayrak_ve_dikkat_ogesi_olusur_ASKIYA_ALINMAZ_ve_altina_inince_kalkar()
    {
        var d = Kur(s => s.Ekle("a.test", "Started"));
        using var _b = d.Baglanti; using var _d = d.Db;
        var kullanici = Guid.NewGuid();
        await d.Kapsamlar.SetScopeAsync(kullanici, ["a.test"], 5, 1); // 1 MB tavan
        var dosya = Path.Combine(d.Siteler.Koku, "a.test", "buyuk.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(dosya)!);
        await File.WriteAllBytesAsync(dosya, new byte[3 * 1024 * 1024]); // FTP ile yuklenmis gibi (kota denetimini gecti)

        await d.Servis.EnforceAllAsync();

        var durumlar = await d.Kapsamlar.GetAttentionStatesAsync();
        var bayrak = Assert.Single(durumlar).DiskOverage;
        Assert.NotNull(bayrak);
        Assert.Equal(1, bayrak!.MaxDiskMb);
        Assert.Contains((await DikkatAl(d.Kapsamlar)).Items, item => item.Id == "subscription-disk-over-quota");
        // Yeni bir askı politikasi UYDURULMADI: abonelik askida DEGIL, site calisiyor.
        Assert.False((await d.Kapsamlar.GetScopeAsync(kullanici)).IsSuspended);
        Assert.Equal("Started", d.Siteler.Durum["a.test"]);

        File.Delete(dosya);
        await d.Servis.EnforceAllAsync();
        Assert.Empty(await d.Kapsamlar.GetAttentionStatesAsync());
    }

    [Fact]
    public async Task Panel_yazimi_kota_kapisi_tavani_asan_islemi_reddeder_sinirsiz_ya_da_sigan_islemi_gecirir()
    {
        var d = Kur(s => s.Ekle("a.test", "Started"));
        using var _b = d.Baglanti; using var _d = d.Db;
        var kullanici = Guid.NewGuid();
        await d.Kapsamlar.SetScopeAsync(kullanici, ["a.test"], 5, 10); // 10 MB
        var dosya = Path.Combine(d.Siteler.Koku, "a.test", "veri.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(dosya)!);
        await File.WriteAllBytesAsync(dosya, new byte[9 * 1024 * 1024]);
        var kapi = new PanelWriteQuotaGuard(d.Kapsamlar, d.Siteler);

        await kapi.EnsureRoomForSiteAsync("a.test", 512 * 1024);                       // sigar
        var hata = await Assert.ThrowsAsync<AppException>(() => kapi.EnsureRoomForSiteAsync("a.test", 2 * 1024 * 1024));
        Assert.Equal("files.quota_exceeded", hata.MessageKey);
        // Klon: kaynagin tamami kopyalanir (9 MB + 9 MB > 10 MB) -> BASLAMADAN reddedilir.
        var klon = await Assert.ThrowsAsync<AppException>(() => kapi.EnsureRoomForCopyOfSiteAsync("a.test"));
        Assert.Equal("files.quota_exceeded", klon.MessageKey);
        // Zaten tavanin ustundeyse hic bir yazima izin yok (0 bayt bile degil: dagitim baslamaz).
        await File.WriteAllBytesAsync(dosya, new byte[11 * 1024 * 1024]);
        await Assert.ThrowsAsync<AppException>(() => kapi.EnsureRoomForSiteAsync("a.test", 0));

        // Sinirsiz abonelik (0) ve sahibi olmayan site: sessizce gecer.
        await d.Kapsamlar.SetScopeAsync(kullanici, ["a.test"], 5, 0);
        await kapi.EnsureRoomForSiteAsync("a.test", long.MaxValue / 4);
        await kapi.EnsureRoomForSiteAsync("sahipsiz.test", 1);
    }

    [Fact]
    public async Task Git_dagitimi_WordPress_klonu_ve_uygulama_kurulumu_KOTA_KAPISINDAN_gecer()
    {
        var kapi = Fake<IPanelWriteQuotaGuard>((metot, _) => throw new AppException("files.quota_exceeded", 403, 403));
        var servisler = new ServiceCollection().AddSingleton(kapi).BuildServiceProvider();
        var baglam = new DefaultHttpContext { RequestServices = servisler };
        var cagrildi = false;

        var git = new GitController(
            Fake<IGitDeploymentService>((_, _) => { cagrildi = true; throw new InvalidOperationException("dagitim BASLAMAMALIYDI"); }),
            Fake<ICustomerScopeService>((_, _) => null),
            Fake<IAuditService>((_, _) => Task.CompletedTask))
        {
            ControllerContext = new ControllerContext { HttpContext = baglam }
        };
        var hata = await Assert.ThrowsAsync<AppException>(() => git.DeployAsync("a.test", default));
        Assert.Equal("files.quota_exceeded", hata.MessageKey);
        Assert.False(cagrildi);

        var depo = DepoKoku();
        foreach (var (dosya, once, sonra) in new[]
        {
            ("src/AdaPanel.Api/Controllers/GitController.cs", "DiskQuotaGate.EnsureForSiteAsync", "DeployViaWebhookAsync(siteName"),
            ("src/AdaPanel.Api/Controllers/WordPressController.cs", "DiskQuotaGate.EnsureForCopyAsync", "wordPressToolkitService.CloneAsync("),
            ("src/AdaPanel.Api/Controllers/StoreController.cs", "DiskQuotaGate.EnsureForSiteAsync", "appStoreService.InstallAsync(")
        })
        {
            var metin = await File.ReadAllTextAsync(Path.Combine(depo, dosya));
            var kapiYeri = metin.IndexOf(once, StringComparison.Ordinal);
            var isYeri = metin.IndexOf(sonra, StringComparison.Ordinal);
            Assert.True(kapiYeri >= 0 && isYeri >= 0 && kapiYeri < isYeri, $"{dosya}: kota kapisi isten ONCE gelmeli.");
        }
    }

    // ------------------------------------------------------------------------------------------
    // M2: FTP hesabini acip kapatan servis ucu (Windows yerel hesap bayragi + GERI OKUMA)
    // ------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(false, "ADAPANEL-ENABLED=False", 0, false)]  // kapatildi ve geri okundu
    [InlineData(true, "ADAPANEL-ENABLED=True", 0, true)]
    public async Task Ftp_hesabi_kapatilinca_kayit_guncellenir(bool etkinles, string cikti, int kod, bool beklenenEtkin)
    {
        var servis = FtpServisi((_, _, _) => Task.FromResult(new CommandResult { ExitCode = kod, StandardOutput = cikti }), baslangicEtkin: !etkinles);
        await servis.SetFtpUserEnabledAsync("hesap1", etkinles);
        Assert.Equal(beklenenEtkin, (await servis.GetFtpUsersAsync()).Single().IsEnabled);
    }

    [Fact]
    public async Task Ftp_hesabi_bayragi_geri_okunamazsa_BASARILI_sayilmaz_ve_kayit_degismez()
    {
        // Cmdlet hatasiz doner ama bayrak degismemis (ya da cikti yok): "kapattim" DENMEZ.
        var servis = FtpServisi((_, _, _) => Task.FromResult(new CommandResult { ExitCode = 0, StandardOutput = "ADAPANEL-ENABLED=True" }), baslangicEtkin: true);
        var hata = await Assert.ThrowsAsync<AppException>(() => servis.SetFtpUserEnabledAsync("hesap1", false));
        Assert.Equal("ftp.enable_state_unverified", hata.MessageKey);
        Assert.True((await servis.GetFtpUsersAsync()).Single().IsEnabled);

        // Panelin kendi acmadigi hesaba (Administrator vb.) dokunulmaz.
        var yabanci = await Assert.ThrowsAsync<AppException>(() => servis.SetFtpUserEnabledAsync("Administrator", false));
        Assert.Equal("ftp.user_not_found", yabanci.MessageKey);
    }

    private AdaPanel.Infrastructure.Ftp.FtpService FtpServisi(Func<string, bool, CancellationToken, Task<CommandResult>> betik, bool baslangicEtkin)
    {
        var secenekler = Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = kok });
        var store = new JsonFileStore(secenekler);
        Directory.CreateDirectory(Path.Combine(kok, "config"));
        File.WriteAllText(Path.Combine(kok, "config", "ftp-users.json"),
            System.Text.Json.JsonSerializer.Serialize(new[] { new { UserName = "hesap1", SiteName = "a.test", HomePath = "C:/x", IsEnabled = baslangicEtkin, CreatedAtUtc = DateTime.UtcNow } }));
        var servis = new AdaPanel.Infrastructure.Ftp.FtpService(null!, store, null!, secenekler)
        {
            NativeScript = betik,
            NativeAccountStatus = (_, _) => Task.FromResult(0u),
            HomeTargetResolver = path => path
        };
        return servis;
    }

    // ------------------------------------------------------------------------------------------
    // M3: FTP kotasi
    // ------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(2, 2, "Reseller", true)]   // dolu: reddedilir
    [InlineData(3, 2, "Reseller", false)]  // yer var
    [InlineData(0, 5, "Reseller", false)]  // 0 = limitsiz
    [InlineData(2, 2, "Administrator", false)] // yonetici sinirsiz (posta/site kotasiyla ayni)
    public async Task Ftp_hesap_kotasi_plan_MaxFtpUsers_degerine_gore_uygulanir(int limit, int mevcut, string rol, bool reddedilir)
    {
        var abonelik = Guid.NewGuid();
        var kapsamlar = Fake<ICustomerScopeService>((metot, _) => metot.Name switch
        {
            nameof(ICustomerScopeService.GetSiteOwnerUserIdAsync) => Task.FromResult<Guid?>(abonelik),
            nameof(ICustomerScopeService.GetScopeAsync) => Task.FromResult(new CustomerScope(["a.test", "b.test"], 0, 0, 0, 0, 0, MaxFtpUsers: limit)),
            _ => throw new NotSupportedException(metot.Name)
        });
        var hesaplar = Enumerable.Range(0, mevcut).Select(i => new FtpUserDto { UserName = "h" + i, SiteName = i % 2 == 0 ? "a.test" : "b.test" })
            .Append(new FtpUserDto { UserName = "baskasi", SiteName = "baska.test" }) // baska aboneligin hesabi SAYILMAZ
            .ToArray();
        var ftp = Fake<IFtpService>((metot, _) => metot.Name == nameof(IFtpService.GetFtpUsersAsync)
            ? Task.FromResult<IReadOnlyCollection<FtpUserDto>>(hesaplar) : throw new NotSupportedException(metot.Name));
        var denetleyici = new FtpController(ftp, kapsamlar, Fake<IIisSiteService>((_, _) => null), Fake<IAuditService>((_, _) => Task.CompletedTask));
        var kimlik = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()), new Claim(ClaimTypes.Role, rol)], "t"));

        if (reddedilir)
        {
            var hata = await Assert.ThrowsAsync<AppException>(() => denetleyici.EnsureFtpQuotaAsync(kimlik, "a.test", default));
            Assert.Equal("ftp.quota_exceeded", hata.MessageKey);
            Assert.Equal(403, hata.HttpStatusCode);
        }
        else
        {
            await denetleyici.EnsureFtpQuotaAsync(kimlik, "a.test", default);
        }
    }

    [Fact]
    public async Task Ftp_kotasi_hesap_acma_ucunda_gercekten_cagrilir()
    {
        // Kapi yalnizca tanimli kalirsa (M4'te lisans kapisinin basina gelen) koruma yoktur: yazma yolunda
        // hesabi acan cagridan ONCE (abonelik kilidi altinda, BeforeMutation icinde) calismalidir.
        var metin = await File.ReadAllTextAsync(Path.Combine(DepoKoku(), "src/AdaPanel.Api/Controllers/FtpController.cs"));
        var kapi = metin.IndexOf("await EnsureFtpQuotaAsync(actor.Principal, request.SiteName, token);", StringComparison.Ordinal);
        var olustur = metin.IndexOf("ftpService.CreateFtpUserAsync(request, BeforeMutation", StringComparison.Ordinal);
        Assert.True(kapi > 0 && olustur > kapi);
    }

    // ------------------------------------------------------------------------------------------
    // M4: lisans kapisi + kilit disi ag cagrisi
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Kisitli_lisansta_yeni_kaynak_acilmaz_ve_acik_hata_anahtari_doner()
    {
        var lisans = Fake<ILicenseService>((metot, args) => metot.Name == nameof(ILicenseService.EnsureCanCreateAsync)
            ? throw new AppException("license.expired", 402, 402) : null);
        var baglam = new DefaultHttpContext { RequestServices = new ServiceCollection().AddSingleton(lisans).BuildServiceProvider() };

        var hata = await Assert.ThrowsAsync<AppException>(() =>
            LicenseGate.EnsureCanCreateAsync(baglam, LicensedResource.Site, default));
        Assert.Equal("license.expired", hata.MessageKey);
        Assert.Equal(402, hata.HttpStatusCode);

        // Yaratan her yol kapiyi, yaratmadan ONCE cagirir.
        var depo = DepoKoku();
        foreach (var (dosya, kaynak, isim) in new[]
        {
            ("src/AdaPanel.Api/Controllers/SitesController.cs", "LicensedResource.Site", "iisSiteService.CreateSiteAsync"),
            ("src/AdaPanel.Api/Controllers/DatabasesController.cs", "LicensedResource.Database", "databaseService.CreateDatabaseAsync(request"),
            ("src/AdaPanel.Api/Controllers/ResellerDatabasesController.cs", "LicensedResource.Database", "databaseService.CreateDatabaseAsync(request"),
            ("src/AdaPanel.Api/Controllers/MailController.cs", "LicensedResource.Mailbox", "mailService.CreateAccountAsync(request"),
            ("src/AdaPanel.Api/Controllers/MailController.cs", "LicensedResource.Mailbox", "mailService.CreateDomainAsync(request"),
            ("src/AdaPanel.Infrastructure/Services/UserManagementService.cs", "LicensedResource.Subscription", "var user = new PanelUser")
        })
        {
            var metin = await File.ReadAllTextAsync(Path.Combine(depo, dosya));
            var kapiYeri = metin.IndexOf(kaynak, StringComparison.Ordinal);
            Assert.True(kapiYeri >= 0, $"{dosya}: {kaynak} kapisi yok.");
        }
    }

    [Fact]
    public async Task Kisitli_lisansta_yeni_abonelik_UserManagement_uzerinden_reddedilir()
    {
        var (conn, db) = await VeritabaniAsync();
        using var _c = conn; using var _d = db;
        var lisans = Fake<ILicenseService>((metot, args) => metot.Name == nameof(ILicenseService.EnsureCanCreateAsync)
            ? throw new AppException("license.invalid", 402, 402) : null);
        var servis = KullaniciServisi(db, lisans: lisans);
        var yonetici = (await db.Users.FirstAsync(u => u.Role == UserRole.Administrator)).Id;

        var hata = await Assert.ThrowsAsync<AppException>(() => servis.CreateUserAsync(
            new CreateUserRequest { UserName = "yeni", Email = "yeni@x.test", Password = "Cok-Guclu-Parola-2026", Role = "Customer" }, yonetici, null));

        Assert.Equal("license.invalid", hata.MessageKey);
        Assert.False(await db.Users.AnyAsync(u => u.UserName == "yeni")); // hicbir sey yaratilmadi
    }

    [Fact]
    public async Task Lisans_durumu_okunurken_ag_cagrisi_KILIT_ICINDE_degildir_ve_gec_gelen_karar_yeni_anahtari_ezmez()
    {
        var saat = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        var zaman = new ZamanSaati(saat);
        var engelle = false;
        var girdi = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var birak = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sunucu = new LisansSunucusu
        {
            BeforeReply = async token =>
            {
                if (!engelle) return;
                girdi.TrySetResult();
                await birak.Task.WaitAsync(token);
            }
        };
        var servis = LisansServisi(sunucu, zaman);
        await servis.ActivateAsync("ADA-TEST-0001");          // gecerli karar, kayit yazildi
        zaman.Ilerlet(TimeSpan.FromHours(13));                 // dogrulama araligi doldu
        engelle = true;

        var okuma = servis.GetStatusAsync();                   // aga cikar ve BEKLER
        await girdi.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Ag cagrisi surerken ayni anahtar uzerinde baska bir islem (devre disi birakma) KILIDI alabilmeli.
        // Eski kodda GetStatusAsync kilidi aga giderken tutuyordu: bu cagri zaman asimina kadar bloklanirdi.
        var ikinci = LisansServisi(sunucu, zaman);
        var devreDisi = await ikinci.DeactivateAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(devreDisi.HasKey);

        birak.TrySetResult();
        await okuma.WaitAsync(TimeSpan.FromSeconds(10));

        // Gec gelen "gecerli" karar, silinmis anahtarin uzerine YAZILMAZ.
        var son = await ikinci.GetStatusAsync();
        Assert.False(son.HasKey);
        Assert.Equal(LicenseState.Unlicensed, son.State);
    }

    // ------------------------------------------------------------------------------------------
    // G4 bakiyesi: kullanici satiri + denetim TEK islem
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Denetim_yazilamazsa_IsActive_degismez_pasif_kaynagi_geri_alinir_ve_istek_hata_doner()
    {
        var (conn, db) = await VeritabaniAsync();
        using var _c = conn; using var _d = db;
        var kaynak = new Dictionary<string, string?> { ["v"] = null };
        var denetim = Fake<IAuditService>((metot, _) => metot.Name is nameof(IAuditService.WriteAsync) or nameof(IAuditService.WriteForSubscriptionAsync)
            ? Task.FromException(new InvalidOperationException("denetim yazilamadi")) : null);
        var servis = KullaniciServisi(db, denetim: denetim, kaynak: kaynak);
        var yonetici = (await db.Users.FirstAsync(u => u.Role == UserRole.Administrator)).Id;
        var musteri = (await db.Users.FirstAsync(u => u.Role == UserRole.Customer)).Id;

        await Assert.ThrowsAsync<InvalidOperationException>(() => servis.UpdateUserAsync(
            musteri, new UpdateUserRequest { Email = "degisti@x.test", Role = "Customer", IsActive = false, MaxSites = 1, MaxDiskMb = 10 }, yonetici, null));

        // TAZE baglamla oku: izlenen nesne degil, KALICI olan.
        await using var taze = new AdaPanelDbContext(new DbContextOptionsBuilder<AdaPanelDbContext>().UseSqlite(conn).Options);
        var satir = await taze.Users.AsNoTracking().FirstAsync(u => u.Id == musteri);
        Assert.True(satir.IsActive);
        Assert.NotEqual("degisti@x.test", satir.Email);
        Assert.Null(kaynak["v"]); // pasif kaynagi JSON'u satirdan ONCE yazildi ama hata durumunda geri alindi
    }

    [Fact]
    public async Task Denetim_yazilamazsa_kullanici_olusturma_ve_silme_de_geri_alinir()
    {
        var (conn, db) = await VeritabaniAsync();
        using var _c = conn; using var _d = db;
        var denetim = Fake<IAuditService>((metot, _) => metot.Name is nameof(IAuditService.WriteAsync) or nameof(IAuditService.WriteForSubscriptionAsync)
            ? Task.FromException(new InvalidOperationException("denetim yazilamadi")) : null);
        var servis = KullaniciServisi(db, denetim: denetim);
        var yonetici = (await db.Users.FirstAsync(u => u.Role == UserRole.Administrator)).Id;
        var musteri = (await db.Users.FirstAsync(u => u.Role == UserRole.Customer)).Id;

        await Assert.ThrowsAsync<InvalidOperationException>(() => servis.CreateUserAsync(
            new CreateUserRequest { UserName = "yeni", Email = "yeni@x.test", Password = "Cok-Guclu-Parola-2026", Role = "Customer" }, yonetici, null));
        await Assert.ThrowsAsync<InvalidOperationException>(() => servis.DeleteUserAsync(musteri, yonetici, false, null));
        await Assert.ThrowsAsync<InvalidOperationException>(() => servis.UnlockUserAsync(musteri, yonetici, null));

        await using var taze = new AdaPanelDbContext(new DbContextOptionsBuilder<AdaPanelDbContext>().UseSqlite(conn).Options);
        Assert.False(await taze.Users.AnyAsync(u => u.UserName == "yeni"));
        Assert.True(await taze.Users.AnyAsync(u => u.Id == musteri)); // silme geri alindi
    }

    [Fact]
    public async Task Denetim_basariliysa_degisiklik_kalicidir_ve_pasif_kaynagi_yazilir()
    {
        var (conn, db) = await VeritabaniAsync();
        using var _c = conn; using var _d = db;
        var kaynak = new Dictionary<string, string?> { ["v"] = null };
        var servis = KullaniciServisi(db, kaynak: kaynak);
        var yonetici = (await db.Users.FirstAsync(u => u.Role == UserRole.Administrator)).Id;
        var musteri = (await db.Users.FirstAsync(u => u.Role == UserRole.Customer)).Id;

        await servis.UpdateUserAsync(
            musteri, new UpdateUserRequest { Email = "musteri@x.test", Role = "Customer", IsActive = false, MaxSites = 1, MaxDiskMb = 10 }, yonetici, null);

        await using var taze = new AdaPanelDbContext(new DbContextOptionsBuilder<AdaPanelDbContext>().UseSqlite(conn).Options);
        Assert.False((await taze.Users.AsNoTracking().FirstAsync(u => u.Id == musteri)).IsActive);
        Assert.Equal("admin", kaynak["v"]);
    }

    // ------------------------------------------------------------------------------------------
    // duzenek
    // ------------------------------------------------------------------------------------------

    private static async Task<(SqliteConnection Conn, AdaPanelDbContext Db)> VeritabaniAsync()
    {
        var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        var db = new AdaPanelDbContext(new DbContextOptionsBuilder<AdaPanelDbContext>().UseSqlite(conn).Options);
        await db.Database.EnsureCreatedAsync();
        PanelUser U(string ad, UserRole rol) => new()
        {
            Id = Guid.NewGuid(), UserName = ad, Email = ad + "@x.test", Role = rol, PreferredLanguage = "tr", IsActive = true,
            PasswordHash = "x", CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow
        };
        db.Users.AddRange(U("yonetici", UserRole.Administrator), U("musteri", UserRole.Customer));
        await db.SaveChangesAsync();
        return (conn, db);
    }

    private static UserManagementService KullaniciServisi(
        AdaPanelDbContext db, IAuditService? denetim = null, ILicenseService? lisans = null, Dictionary<string, string?>? kaynak = null)
    {
        var kapsamlar = Fake<ICustomerScopeService>((metot, args) => metot.Name switch
        {
            nameof(ICustomerScopeService.GetParentSubscriptionAsync) => Task.FromResult<Guid?>(null),
            nameof(ICustomerScopeService.GetScopeAsync) => Task.FromResult(new CustomerScope([], 0, 0, 0, 0, 0)),
            nameof(ICustomerScopeService.GetSubscriptionUserIdsAsync) => Task.FromResult<IReadOnlyCollection<Guid>>([]),
            nameof(ICustomerScopeService.GetSubUserIdsAsync) => Task.FromResult<IReadOnlyCollection<Guid>>([]),
            nameof(ICustomerScopeService.GetAssignedSitesAsync) or nameof(ICustomerScopeService.GetResellerSitesAsync) => Task.FromResult<IReadOnlyCollection<string>>([]),
            nameof(ICustomerScopeService.GetInactiveSourceAsync) => Task.FromResult(kaynak?["v"]),
            nameof(ICustomerScopeService.SetInactiveSourceAsync) => Task.Run(() => { if (kaynak is not null) kaynak["v"] = (string?)args![1]; }),
            nameof(ICustomerScopeService.GetDiskUsageMbAsync) => Task.FromResult(new DiskUsage(0, true, string.Empty)),
            _ => null
        });
        return new UserManagementService(
            db, new PasswordHasher<PanelUser>(), kapsamlar, Fake<INotificationService>((_, _) => null),
            Fake<IPanelPreferencesService>((_, _) => null), Fake<IPanelSecurityService>((_, _) => null),
            NullLogger<UserManagementService>.Instance, denetim ?? Fake<IAuditService>((_, _) => null),
            Fake<IIisSiteService>((_, _) => null), Fake<IDatabaseOwnershipService>((_, _) => null), Fake<ISchedulerService>((_, _) => null),
            Fake<ISiteCascadeDeletionService>((_, _) => null), Fake<IApiTokenService>((_, _) => null),
            Fake<IServerOwnerService>((_, _) => null), Fake<IResellerQuotaService>((_, _) => null), lisans);
    }

    private static async Task<AdaPanel.Core.Dtos.Advisor.AttentionSummaryDto> DikkatAl(ICustomerScopeService kapsamlar)
    {
        var danisman = Fake<IAdvisorService>((_, _) => throw new InvalidOperationException("kullanilmiyor"));
        var dikkat = new AttentionService(danisman, null!, null!, null!, null!, null!, null, null, kapsamlar);
        return await dikkat.GetSummaryAsync();
    }

    private static string DepoKoku()
    {
        var klasor = AppContext.BaseDirectory;
        for (var i = 0; i < 9; i++)
        {
            if (File.Exists(Path.Combine(klasor, "AdaPanel.sln"))) return Path.GetFullPath(klasor);
            klasor = Path.Combine(klasor, "..");
        }

        throw new DirectoryNotFoundException("Depo koku bulunamadi.");
    }

    private LicenseService LisansServisi(LisansSunucusu sunucu, ZamanSaati saat)
    {
        var fileStore = new JsonFileStore(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = kok }));
        return new LicenseService(
            Microsoft.Extensions.Options.Options.Create(new LicenseOptions
            {
                VerificationUrl = "https://lisans.ornek.test/verify", ProductCode = "adapanel", OfflineGraceDays = 14, VerifyIntervalHours = 12
            }),
            new LicenseStateStore(fileStore, new Koruyucu()),
            new ParmakIzi(), sunucu, saat);
    }

    // Lisans servisi ayni ad alanindaki tipler
    private sealed class Koruyucu : ISecretProtector
    {
        public string Protect(string plaintext) => "P:" + plaintext;
        public string Unprotect(string protectedValue) => protectedValue.StartsWith("P:", StringComparison.Ordinal) ? protectedValue[2..] : string.Empty;
    }

    private sealed class ParmakIzi : IMachineFingerprintProvider
    {
        public string Get() => "FINGERPRINT";
        public string GetSource() => "test";
    }

    private sealed class ZamanSaati(DateTime baslangic) : TimeProvider
    {
        private DateTime simdi = baslangic;
        public void Ilerlet(TimeSpan fark) => simdi += fark;
        public override DateTimeOffset GetUtcNow() => new(simdi, TimeSpan.Zero);
    }

    private sealed class LisansSunucusu : ILicenseVerificationClient
    {
        public Func<CancellationToken, Task>? BeforeReply { get; init; }

        public async Task<LicenseVerificationOutcome> VerifyAsync(LicenseVerificationRequest request, CancellationToken cancellationToken)
        {
            if (BeforeReply is not null) await BeforeReply(cancellationToken);
            return new LicenseVerificationOutcome(
                new LicenseVerificationResponse { Status = LicenseVerdicts.Valid, Sku = "pro" }, string.Empty);
        }
    }

    private sealed class TrafiksizIstatistik : IWebStatisticsService
    {
        public Task<WebStatisticsDto> GetSiteStatisticsAsync(string siteName, int days, CancellationToken cancellationToken = default)
            => Task.FromResult(new WebStatisticsDto());
    }

    /// <summary>Her sitenin durumunu TUTAN IIS sahtesi; adi listede olan site durdurulurken/baslatilirken firlatir.</summary>
    internal sealed class DurumluSiteler(string koku) : IIisSiteService
    {
        public string Koku { get; } = koku;
        public Dictionary<string, string> Durum { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> StopKirilan { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> StartKirilan { get; } = new(StringComparer.OrdinalIgnoreCase);

        public void Ekle(string ad, string durum)
        {
            Durum[ad] = durum;
            Directory.CreateDirectory(Path.Combine(Koku, ad));
        }

        public Task<SiteDto> GetSiteAsync(string siteName, CancellationToken cancellationToken = default)
            => Durum.TryGetValue(siteName, out var durum)
                ? Task.FromResult(new SiteDto { Name = siteName, State = durum, PhysicalPath = Path.Combine(Koku, siteName) })
                : throw new AppException("sites.not_found", 404, 404);

        public Task<SiteDto> StopSiteAsync(string siteName, CancellationToken cancellationToken = default)
        {
            if (StopKirilan.Contains(siteName)) throw new InvalidOperationException("durdurulamadi: " + siteName);
            Durum[siteName] = "Stopped";
            return GetSiteAsync(siteName, cancellationToken);
        }

        public Task<SiteDto> StartSiteAsync(string siteName, CancellationToken cancellationToken = default)
        {
            if (StartKirilan.Contains(siteName)) throw new InvalidOperationException("baslatilamadi: " + siteName);
            Durum[siteName] = "Started";
            return GetSiteAsync(siteName, cancellationToken);
        }

        public Task<IReadOnlyCollection<SiteDto>> GetSitesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyCollection<SiteDto>>([]);
        public Task<SiteDto> CreateSiteAsync(CreateSiteRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDto> UpdatePhysicalPathAsync(string siteName, string physicalPath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteSiteAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDto> AddBindingAsync(string siteName, SiteBindingRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDto> RemoveBindingAsync(string siteName, RemoveSiteBindingRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<SiteDto>> ImportExistingSitesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    // ---- ortak sahte: Task donenler tamamlanmis Task doner; handler null donerse varsayilan ----
    public class Proxy : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?>? Handler { get; set; }

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            object? handled;
            try
            {
                handled = Handler?.Invoke(method!, args);
            }
            catch (Exception exception) when (method!.ReturnType == typeof(Task) || (method.ReturnType.IsGenericType && method.ReturnType.GetGenericTypeDefinition() == typeof(Task<>)))
            {
                // Task donen metotta hata AWAIT'te gorulur (gercek servislerle ayni).
                if (method.ReturnType == typeof(Task)) return Task.FromException(exception);
                var inner = method.ReturnType.GetGenericArguments()[0];
                return typeof(Task).GetMethod(nameof(Task.FromException), 1, [typeof(Exception)])!.MakeGenericMethod(inner).Invoke(null, [exception]);
            }

            if (handled is not null) return handled;
            var type = method!.ReturnType;
            if (type == typeof(Task)) return Task.CompletedTask;
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var inner = type.GetGenericArguments()[0];
                var value = inner.IsValueType ? Activator.CreateInstance(inner) : null;
                return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(inner).Invoke(null, [value]);
            }

            return type.IsValueType && type != typeof(void) ? Activator.CreateInstance(type) : null;
        }
    }

    private static T Fake<T>(Func<MethodInfo, object?[]?, object?> handler) where T : class
    {
        var stub = DispatchProxy.Create<T, Proxy>();
        ((Proxy)(object)stub).Handler = handler;
        return stub;
    }
}
