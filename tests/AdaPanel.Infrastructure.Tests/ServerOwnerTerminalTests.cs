using System.Security.Claims;
using AdaPanel.Api.Controllers;
using AdaPanel.Api.Security;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Auth;
using AdaPanel.Core.Dtos.Terminal;
using AdaPanel.Core.Entities;
using AdaPanel.Core.Enums;
using AdaPanel.Core.Interfaces;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Security;
using AdaPanel.Infrastructure.Support;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// TERMİNAL = SYSTEM KABUĞU, VE YALNIZ SUNUCU SAHİBİNİN.
/// </summary>
/// <remarks>
/// <para>
/// <b>ÖLÇÜLEN DURUM (kod okunarak, tahmin değil):</b> panel servisi
/// <c>scripts\install-service.ps1</c> içinde <c>New-Service … -Credential VERİLMEDEN</c> kurulur →
/// <c>LocalSystem</c>. <c>TerminalService</c> <c>powershell.exe</c>'yi doğrudan alt süreç olarak
/// başlatır (kısıtlı kimlik yok). Uç ise yalnızca <c>[Authorize(Roles = "Administrator")]</c> ile
/// korunuyordu ve bir yönetici <c>UsersController</c>'dan İKİNCİ bir yönetici yaratabiliyor.
/// Sonuç: "panel yöneticisi bir Windows yöneticisi DEĞİLDİR" doktrini, terminale erişebilen
/// herkes için YANLIŞTI.
/// </para>
/// <para>
/// <b>Bu dosya davranış ölçer, kaynak taramaz.</b> Her test gerçek
/// <see cref="ServerOwnerService"/>'i (gerçek SQLite + gerçek dosya deposu) ve gerçek
/// <see cref="TerminalController"/>'ı çalıştırır.
/// </para>
/// </remarks>
public sealed class ServerOwnerTerminalTests
{
    // ---------------------------------------------------------------------
    // 1) FAZLA KAPATMA NÖBETÇİLERİ — sahibinin kapısı KAPANMAMALI
    // ---------------------------------------------------------------------

    /// <summary>
    /// NÖBETÇİ: terminali sahibine de kapatan bir çözüm özelliği ÖLDÜRÜR. Bu test, korumanın
    /// meşru kullanımı bozmadığının kanıtıdır ve her zaman yeşil kalmalıdır.
    /// </summary>
    [Fact]
    public async Task Sunucu_sahibi_terminale_GIREBILIR()
    {
        await using var duzenek = await Duzenek.OlusturAsync();

        var denetleyici = duzenek.Denetleyici(duzenek.Sahip.Id);
        var yanit = await denetleyici.ExecuteAsync(Istek("Get-Date"), CancellationToken.None);

        Assert.IsType<OkObjectResult>(yanit.Result);
        Assert.Equal(["Get-Date"], duzenek.CalisanKomutlar);
        Assert.Contains("terminal.command.executed", duzenek.Denetim.Actions);
    }

    /// <summary>
    /// NÖBETÇİ: sabitlenmiş kayıt bozuksa karar "kimse giremez" DEĞİL, en eski etkin yöneticiye
    /// geri düşüştür. Bozuk bir dosyanın paneli kilitlemesi, korumanın kendisini bir arıza
    /// kaynağına çevirirdi.
    /// </summary>
    [Fact]
    public async Task Bozuk_sahiplik_kaydi_sahibi_DISARIDA_BIRAKMAZ()
    {
        await using var duzenek = await Duzenek.OlusturAsync();
        duzenek.KaydiBozukYaz("{ bu gecerli JSON degil");

        var denetleyici = duzenek.Denetleyici(duzenek.Sahip.Id);
        var yanit = await denetleyici.ExecuteAsync(Istek("Get-Date"), CancellationToken.None);

        Assert.IsType<OkObjectResult>(yanit.Result);
    }

    /// <summary>
    /// NÖBETÇİ: kayıt hiç yoksa (var olan kurulumlar — dosya yeni geldi) sahibi yine girebilmeli.
    /// </summary>
    [Fact]
    public async Task Kayit_HIC_YOKKEN_sahip_yine_girebilir()
    {
        await using var duzenek = await Duzenek.OlusturAsync();
        Assert.False(duzenek.KayitVarMi());

        Assert.True(await duzenek.SahiplikServisi.IsServerOwnerAsync(duzenek.Sahip.Id));
    }

    // ---------------------------------------------------------------------
    // 2) KORUMA — rol yükseltmesi terminali VERMEZ
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Sonradan_yaratilan_ikinci_yonetici_terminale_GIREMEZ()
    {
        await using var duzenek = await Duzenek.OlusturAsync();

        var denetleyici = duzenek.Denetleyici(duzenek.IkinciYonetici.Id);
        var hata = await Assert.ThrowsAsync<AppException>(
            () => denetleyici.ExecuteAsync(Istek("whoami"), CancellationToken.None));

        Assert.Equal("terminal.owner_only", hata.MessageKey);
        Assert.Equal(403, hata.HttpStatusCode);
        // KOMUT HİÇ ÇALIŞMADI — 403 dönmek yetmez, sürecin başlamamış olması gerekir.
        Assert.Empty(duzenek.CalisanKomutlar);
    }

    /// <summary>
    /// Reddin KENDİSİ denetim kaydına düşer: "sunucu sahibi olmayan bir yönetici SYSTEM kabuğu
    /// denedi" bir olayın en erken işaretidir.
    /// </summary>
    [Fact]
    public async Task Reddedilen_deneme_denetim_kaydina_DUSER()
    {
        await using var duzenek = await Duzenek.OlusturAsync();

        var denetleyici = duzenek.Denetleyici(duzenek.IkinciYonetici.Id);
        await Assert.ThrowsAsync<AppException>(
            () => denetleyici.ExecuteAsync(Istek("net user hacker /add"), CancellationToken.None));

        Assert.Contains("terminal.command.denied", duzenek.Denetim.Actions);
        // Denenen komut da yazılır: reddedilen komut, niyetin kendisidir.
        Assert.Contains(duzenek.Denetim.Kayitlar, kayit => kayit.Details.Contains("net user hacker /add"));
    }

    /// <summary>
    /// Taklit oturumu (<c>auth/impersonate</c>) terminale GİREMEZ — hedef sunucu sahibi OLSA BİLE.
    /// </summary>
    [Fact]
    public async Task Taklit_edilen_oturum_sahibin_kimligiyle_bile_GIREMEZ()
    {
        await using var duzenek = await Duzenek.OlusturAsync();

        var denetleyici = duzenek.Denetleyici(duzenek.Sahip.Id, taklitEden: duzenek.IkinciYonetici.Id);
        var hata = await Assert.ThrowsAsync<AppException>(
            () => denetleyici.ExecuteAsync(Istek("whoami"), CancellationToken.None));

        Assert.Equal("terminal.owner_only", hata.MessageKey);
        Assert.Empty(duzenek.CalisanKomutlar);
    }

    // ---------------------------------------------------------------------
    // 3) SAHİPLİK DEVRİ — panelden DEĞİL, sunucudaki dosyadan
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Sunucudaki_dosya_sahipligi_DEVREDEBILIR()
    {
        await using var duzenek = await Duzenek.OlusturAsync();
        await duzenek.SahipligiSabitleAsync(duzenek.IkinciYonetici.Id);

        Assert.True(await duzenek.SahiplikServisi.IsServerOwnerAsync(duzenek.IkinciYonetici.Id));
        Assert.False(await duzenek.SahiplikServisi.IsServerOwnerAsync(duzenek.Sahip.Id));
    }

    /// <summary>
    /// Karar İLK OKUMADA diske sabitlenir. Sabitleme olmasaydı, sıralamayı değiştiren bir eylem
    /// (sahibi pasife almak) sahipliği devralmanın yolu olurdu.
    /// </summary>
    [Fact]
    public async Task Karar_ilk_okumada_diske_SABITLENIR()
    {
        await using var duzenek = await Duzenek.OlusturAsync();

        var sahip = await duzenek.SahiplikServisi.GetServerOwnerIdAsync();

        Assert.Equal(duzenek.Sahip.Id, sahip);
        Assert.True(duzenek.KayitVarMi());
        Assert.Equal(duzenek.Sahip.Id, duzenek.KayittakiSahip());
    }

    /// <summary>
    /// Sabitleme sonrası sahip pasife alınsa bile sıradaki yönetici sahipliği DEVRALMAZ; sonuç
    /// "sahip yok"tur ve kapı ancak sunucudaki dosyayla yeniden açılır.
    /// </summary>
    [Fact]
    public async Task Sahip_pasife_alinsa_bile_sıradaki_yonetici_DEVRALMAZ()
    {
        await using var duzenek = await Duzenek.OlusturAsync();
        await duzenek.SahiplikServisi.GetServerOwnerIdAsync();

        duzenek.Sahip.IsActive = false;
        await duzenek.DbContext.SaveChangesAsync();

        Assert.False(await duzenek.SahiplikServisi.IsServerOwnerAsync(duzenek.IkinciYonetici.Id));
    }

    // ---------------------------------------------------------------------
    // 4) SAF KARAR — ServerOwnerRegistry
    // ---------------------------------------------------------------------

    [Fact]
    public void Bayi_ve_musteri_sahip_ADAYI_bile_degildir()
    {
        var bayi = new SunucuSahibiAdayi(Guid.NewGuid(), UserRole.Reseller, true, new DateTime(2020, 1, 1));
        var musteri = new SunucuSahibiAdayi(Guid.NewGuid(), UserRole.Customer, true, new DateTime(2020, 1, 1));
        var yonetici = new SunucuSahibiAdayi(Guid.NewGuid(), UserRole.Administrator, true, new DateTime(2024, 1, 1));

        Assert.Equal(yonetici.Id, ServerOwnerRegistry.Belirle(null, [bayi, musteri, yonetici]));
    }

    [Fact]
    public void Pasif_yonetici_sahip_olamaz()
    {
        var pasif = new SunucuSahibiAdayi(Guid.NewGuid(), UserRole.Administrator, false, new DateTime(2020, 1, 1));
        var etkin = new SunucuSahibiAdayi(Guid.NewGuid(), UserRole.Administrator, true, new DateTime(2024, 1, 1));

        Assert.Equal(etkin.Id, ServerOwnerRegistry.Belirle(null, [pasif, etkin]));
    }

    /// <summary>
    /// Berabere kalma KARARLI olmalı: aynı anda yaratılmış iki yönetici varsa panel iki istekte
    /// iki farklı sahip döndüremez.
    /// </summary>
    [Fact]
    public void Ayni_anda_yaratilmis_iki_yoneticide_karar_KARARLIDIR()
    {
        var an = new DateTime(2024, 5, 5, 10, 0, 0, DateTimeKind.Utc);
        var a = new SunucuSahibiAdayi(Guid.Parse("11111111-1111-1111-1111-111111111111"), UserRole.Administrator, true, an);
        var b = new SunucuSahibiAdayi(Guid.Parse("22222222-2222-2222-2222-222222222222"), UserRole.Administrator, true, an);

        Assert.Equal(a.Id, ServerOwnerRegistry.Belirle(null, [a, b]));
        Assert.Equal(a.Id, ServerOwnerRegistry.Belirle(null, [b, a]));
    }

    [Fact]
    public void Hic_etkin_yonetici_yoksa_sahip_YOKTUR()
    {
        var pasif = new SunucuSahibiAdayi(Guid.NewGuid(), UserRole.Administrator, false, new DateTime(2020, 1, 1));

        Assert.Null(ServerOwnerRegistry.Belirle(null, [pasif]));
    }

    /// <summary>
    /// Sabitlenmiş kayıt artık var olmayan bir kullanıcıyı gösteriyorsa sonuç "sahip YOK"tur —
    /// sıradaki yöneticiye DÜŞMEZ. Düşseydi, sahibi kaydın dışına çıkarmak (silmek/pasife almak)
    /// sahipliği devralmanın yolu olurdu.
    /// </summary>
    [Fact]
    public void Sabit_kayit_gecersizse_sahip_YOKTUR_siradakine_DUSMEZ()
    {
        var yonetici = new SunucuSahibiAdayi(Guid.NewGuid(), UserRole.Administrator, true, new DateTime(2024, 1, 1));

        Assert.Null(ServerOwnerRegistry.Belirle(Guid.NewGuid(), [yonetici]));
    }

    /// <summary>
    /// NÖBETÇİ: "kayıt yok" ile "kayıt var ama geçersiz" AYNI ŞEY DEĞİL. Kayıt yokken geri düşüş
    /// çalışmalı, yoksa dosyayı henüz görmemiş her kurulumda terminal ölürdü.
    /// </summary>
    [Fact]
    public void Kayit_YOKKEN_en_eski_yoneticiye_DUSULUR()
    {
        var eski = new SunucuSahibiAdayi(Guid.NewGuid(), UserRole.Administrator, true, new DateTime(2024, 1, 1));
        var yeni = new SunucuSahibiAdayi(Guid.NewGuid(), UserRole.Administrator, true, new DateTime(2026, 1, 1));

        Assert.Equal(eski.Id, ServerOwnerRegistry.Belirle(null, [yeni, eski]));
    }

    // ---------------------------------------------------------------------
    // 5) TAKLİT — "log in as customer" bir yöneticiyi hedef ALAMAZ
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Yonetici_baska_bir_YONETICIYI_taklit_EDEMEZ()
    {
        await using var duzenek = await Duzenek.OlusturAsync();
        var denetleyici = duzenek.KimlikDenetleyicisi(duzenek.IkinciYonetici.Id, duzenek.Sahip);

        var yanit = await denetleyici.ImpersonateAsync(duzenek.Sahip.Id, CancellationToken.None);

        var sonuc = Assert.IsType<ObjectResult>(yanit.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, sonuc.StatusCode);
        Assert.False(duzenek.TaklitOturumuAcildi);
    }

    /// <summary>
    /// NÖBETÇİ: özellik ÖLMEDİ — müşteri hesabı hâlâ taklit edilebiliyor ("log in as customer").
    /// </summary>
    [Fact]
    public async Task Musteri_hesabi_HALA_taklit_edilebilir()
    {
        await using var duzenek = await Duzenek.OlusturAsync();
        var denetleyici = duzenek.KimlikDenetleyicisi(duzenek.Sahip.Id, duzenek.Musteri);

        var yanit = await denetleyici.ImpersonateAsync(duzenek.Musteri.Id, CancellationToken.None);

        Assert.IsType<OkObjectResult>(yanit.Result);
        Assert.True(duzenek.TaklitOturumuAcildi);
    }

    // ---------------------------------------------------------------------
    // 6) SAHİP HESABI PANELDEN KALDIRILAMAZ
    //    (aksi hâlde sahibi silmek/pasife almak sahipliği devralmanın yoluydu)
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Sunucu_sahibi_hesabi_SILINEMEZ()
    {
        await using var duzenek = await Duzenek.OlusturAsync();
        var servis = duzenek.KullaniciServisi(sahipId: duzenek.Sahip.Id);

        var hata = await Assert.ThrowsAsync<InvalidOperationException>(
            () => servis.DeleteUserAsync(duzenek.Sahip.Id, duzenek.IkinciYonetici.Id, false, null));

        Assert.Equal("users.server_owner_protected", hata.Message);
        Assert.True(await duzenek.DbContext.Users.AnyAsync(k => k.Id == duzenek.Sahip.Id));
    }

    [Fact]
    public async Task Sunucu_sahibi_hesabi_PASIFE_ALINAMAZ()
    {
        await using var duzenek = await Duzenek.OlusturAsync();
        var servis = duzenek.KullaniciServisi(sahipId: duzenek.Sahip.Id);

        var hata = await Assert.ThrowsAsync<InvalidOperationException>(
            () => servis.UpdateUserAsync(
                duzenek.Sahip.Id,
                new AdaPanel.Core.Dtos.Users.UpdateUserRequest
                {
                    Email = duzenek.Sahip.Email,
                    Role = nameof(UserRole.Administrator),
                    PreferredLanguage = "tr",
                    IsActive = false
                },
                duzenek.IkinciYonetici.Id,
                null));

        Assert.Equal("users.server_owner_protected", hata.Message);
    }

    /// <summary>
    /// NÖBETÇİ: koruma YALNIZ sahibi koruyor — sıradan bir ikinci yönetici hâlâ pasife alınabilir.
    /// Kural genişletilseydi panel yönetimi kilitlenirdi.
    /// </summary>
    [Fact]
    public async Task Sahip_OLMAYAN_yonetici_hala_pasife_alinabilir()
    {
        await using var duzenek = await Duzenek.OlusturAsync();
        var servis = duzenek.KullaniciServisi(sahipId: duzenek.Sahip.Id);

        await servis.UpdateUserAsync(
            duzenek.IkinciYonetici.Id,
            new AdaPanel.Core.Dtos.Users.UpdateUserRequest
            {
                Email = duzenek.IkinciYonetici.Email,
                Role = nameof(UserRole.Administrator),
                PreferredLanguage = "tr",
                IsActive = false
            },
            duzenek.Sahip.Id,
            null);

        Assert.False(duzenek.IkinciYonetici.IsActive);
    }

    private static ExecuteTerminalCommandRequest Istek(string komut) =>
        new() { Command = komut, WorkingDirectory = string.Empty, TimeoutSeconds = 5 };

    // ---------------------------------------------------------------------
    // düzenek
    // ---------------------------------------------------------------------

    private sealed class Duzenek : IAsyncDisposable
    {
        private readonly SqliteConnection baglanti;

        private Duzenek(SqliteConnection baglanti, AdaPanelDbContext dbContext, string veriKoku)
        {
            this.baglanti = baglanti;
            DbContext = dbContext;
            VeriKoku = veriKoku;
            DosyaDeposu = new JsonFileStore(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = veriKoku }));
            SahiplikServisi = new ServerOwnerService(
                dbContext, DosyaDeposu, NullLogger<ServerOwnerService>.Instance);
        }

        public AdaPanelDbContext DbContext { get; }

        public string VeriKoku { get; }

        public JsonFileStore DosyaDeposu { get; }

        public ServerOwnerService SahiplikServisi { get; }

        public PanelUser Sahip { get; private set; } = null!;

        public PanelUser IkinciYonetici { get; private set; } = null!;

        public PanelUser Musteri { get; private set; } = null!;

        public List<string> CalisanKomutlar { get; } = [];

        public RecordingAuditService Denetim { get; } = new();

        public bool TaklitOturumuAcildi { get; private set; }

        public static async Task<Duzenek> OlusturAsync()
        {
            var baglanti = new SqliteConnection("Data Source=:memory:");
            await baglanti.OpenAsync();
            var secenekler = new DbContextOptionsBuilder<AdaPanelDbContext>().UseSqlite(baglanti).Options;
            var dbContext = new AdaPanelDbContext(secenekler);
            await dbContext.Database.EnsureCreatedAsync();

            // Her testin KENDİ veri kökü: sabitleme dosyası testler arasında sızmamalı.
            var veriKoku = Path.Combine(Path.GetTempPath(), $"adapanel-sahiplik-{Guid.NewGuid():N}");
            Directory.CreateDirectory(veriKoku);

            var duzenek = new Duzenek(baglanti, dbContext, veriKoku);

            duzenek.Sahip = Kullanici("kurucu", UserRole.Administrator, new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            duzenek.IkinciYonetici = Kullanici("ikinci-admin", UserRole.Administrator, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            duzenek.Musteri = Kullanici("musteri", UserRole.Customer, new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc));

            dbContext.Users.AddRange(duzenek.Sahip, duzenek.IkinciYonetici, duzenek.Musteri);
            await dbContext.SaveChangesAsync();
            return duzenek;
        }

        public TerminalController Denetleyici(Guid aktorId, Guid? taklitEden = null)
        {
            var denetleyici = new TerminalController(
                new SahteTerminal(CalisanKomutlar), SahiplikServisi, Denetim);
            denetleyici.ControllerContext = new ControllerContext { HttpContext = HttpBaglami(aktorId, taklitEden) };
            return denetleyici;
        }

        public AuthController KimlikDenetleyicisi(Guid aktorId, PanelUser hedef)
        {
            var denetleyici = new AuthController(
                new PatlayanAntiforgeryIkizi(),
                new SabitKimlikServisi(hedef),
                Denetim,
                new ThrowingSecurityService(),
                new SessionRevocationStore(DosyaDeposu));
            denetleyici.ControllerContext = new ControllerContext { HttpContext = HttpBaglami(aktorId, null) };
            var actor = DbContext.Users.Single(user => user.Id == aktorId);
            var identity = (ClaimsIdentity)denetleyici.User.Identity!;
            identity.AddClaim(new(CookieSessionValidator.CredentialStampClaim, CookieSessionValidator.ComputeStamp(actor.PasswordHash)));
            identity.AddClaim(new(SessionRevocationStore.SessionIdClaim, SessionRevocationStore.NewSessionId()));
            return denetleyici;
        }

        /// <summary>
        /// Gerçek <see cref="AdaPanel.Infrastructure.Services.UserManagementService"/>; sahiplik
        /// yanıtı testin verdiği kimlikten gelir (sahiplik ÇÖZÜMLEME davranışı ayrıca ölçülüyor).
        /// </summary>
        public AdaPanel.Infrastructure.Services.UserManagementService KullaniciServisi(Guid sahipId) => new(
            DbContext,
            new Microsoft.AspNetCore.Identity.PasswordHasher<PanelUser>(),
            new SessizKapsam(),
            new ThrowingNotificationService(),
            new ThrowingPreferencesService(),
            new ThrowingSecurityService(),
            NullLogger<AdaPanel.Infrastructure.Services.UserManagementService>.Instance,
            Denetim,
            new ResellerSiteAssignmentTests.KullanilmayanSiteServisi(),
            new ResellerSiteAssignmentTests.KullanilmayanDbSahiplik(),
            new ResellerSiteAssignmentTests.KullanilmayanZamanlayici(),
            new ResellerSiteAssignmentTests.KullanilmayanCascade(),
            new ThrowingApiTokenService(),
            new SabitSunucuSahibiServisi(sahipId));

        public async Task SahipligiSabitleAsync(Guid userId) =>
            await DosyaDeposu.WriteAsync(
                ServerOwnerRegistry.MarkerRelativePath, new SunucuSahibiKaydi { UserId = userId });

        public void KaydiBozukYaz(string icerik)
        {
            var yol = Path.Combine(VeriKoku, "config", "server-owner.json");
            Directory.CreateDirectory(Path.GetDirectoryName(yol)!);
            File.WriteAllText(yol, icerik);
        }

        public bool KayitVarMi() => File.Exists(Path.Combine(VeriKoku, "config", "server-owner.json"));

        public Guid? KayittakiSahip()
        {
            var yol = Path.Combine(VeriKoku, "config", "server-owner.json");
            using var akis = File.OpenRead(yol);
            return System.Text.Json.JsonSerializer.Deserialize<SunucuSahibiKaydi>(
                akis, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))?.UserId;
        }

        public ValueTask DisposeAsync()
        {
            DbContext.Dispose();
            baglanti.Dispose();
            // Geçici veri kökü SİLİNMEZ (bu depoda ajanların silme yasağı var); %TEMP% altında
            // kalır ve işletim sistemi temizliğine bırakılır.
            return ValueTask.CompletedTask;
        }

        private HttpContext HttpBaglami(Guid aktorId, Guid? taklitEden)
        {
            List<Claim> talepler =
            [
                new(ClaimTypes.NameIdentifier, aktorId.ToString()),
                new(ClaimTypes.Name, "aktor"),
                new(ClaimTypes.Role, nameof(UserRole.Administrator))
            ];

            if (taklitEden is { } taklit)
            {
                talepler.Add(new Claim("impersonator_id", taklit.ToString()));
            }

            var servisler = new ServiceCollection();
            servisler.AddSingleton<IAuthenticationService>(new GirisiKaydedenServis(() => TaklitOturumuAcildi = true));

            return new DefaultHttpContext
            {
                RequestServices = servisler.BuildServiceProvider(),
                User = new ClaimsPrincipal(new ClaimsIdentity(talepler, CookieAuthenticationDefaults.AuthenticationScheme))
            };
        }

        private static PanelUser Kullanici(string ad, UserRole rol, DateTime yaratilma) => new()
        {
            Id = Guid.NewGuid(),
            UserName = ad,
            Email = $"{ad}@ornek.test",
            Role = rol,
            PasswordHash = "x",
            IsActive = true,
            CreatedAtUtc = yaratilma
        };
    }

    /// <summary>
    /// Kapsam yazımını SESSİZCE yutan kapsam servisi. Burada ölçülen kapsam davranışı değil,
    /// sahiplik korumasıdır; kapsam yolunun patlaması testi ölçmek istediği şeyden koparırdı.
    /// </summary>
    private sealed class SessizKapsam : ICustomerScopeService
    {
        public Task<IReadOnlyCollection<string>> GetAssignedSitesAsync(Guid userId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyCollection<string>>([]);

        public Task<CustomerScope> GetScopeAsync(Guid userId, CancellationToken cancellationToken = default) => Task.FromResult(new CustomerScope([], 0, 0, 0, 0, 0));

        public Task SetScopeAsync(Guid userId, IEnumerable<string> siteNames, int maxSites, int maxDiskMb, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SetExtraLimitsAsync(Guid userId, int maxDatabases, int maxMailboxes, int maxScheduledTasks, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RemoveUserAsync(Guid userId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RemoveSiteFromAllScopesAsync(string siteName, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyCollection<string>> GetAllowedMailDomainsAsync(Guid userId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyCollection<string>>([]);

        public Task<DiskUsage> GetDiskUsageMbAsync(Guid userId, CancellationToken cancellationToken = default) => Task.FromResult(new DiskUsage(0, true, string.Empty));

        public Task SetOwnerAsync(Guid userId, Guid? ownerId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<Guid?> GetOwnerAsync(Guid userId, CancellationToken cancellationToken = default) => Task.FromResult<Guid?>(null);

        public Task<IReadOnlyCollection<Guid>> GetOwnedUserIdsAsync(Guid ownerId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyCollection<Guid>>([]);

        public Task<IReadOnlyCollection<string>> GetResellerSitesAsync(Guid resellerId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyCollection<string>>([]);

        public Task<IReadOnlyCollection<string>> GetResellerMailDomainsAsync(Guid resellerId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyCollection<string>>([]);

        public Task<Guid?> GetSiteOwnerUserIdAsync(string siteName, CancellationToken cancellationToken = default) => Task.FromResult<Guid?>(null);

        public Task SetParentSubscriptionAsync(Guid userId, Guid? parentUserId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<Guid?> GetParentSubscriptionAsync(Guid userId, CancellationToken cancellationToken = default) => Task.FromResult<Guid?>(null);

        public Task<IReadOnlyCollection<Guid>> GetSubUserIdsAsync(Guid parentUserId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyCollection<Guid>>([]);

        public Task SetSubUserSectionsAsync(Guid userId, IReadOnlyCollection<string>? sections, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyCollection<string>?> GetSubUserSectionsAsync(Guid userId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyCollection<string>?>(null);

        public Task SetPlanAsync(Guid userId, string? planId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SetExpiryAsync(Guid userId, DateTime? expiresAtUtc, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SuspendAsync(Guid userId, string reason, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task ResumeAsync(Guid userId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyCollection<Guid>> GetSuspendedUserIdsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyCollection<Guid>>([]);

        public Task<IReadOnlyCollection<Guid>> GetSubscriptionUserIdsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyCollection<Guid>>([]);
    }

    /// <summary>Sahibi TEST belirler: burada ölçülen sahiplik çözümlemesi değil, korumanın kendisi.</summary>
    private sealed class SabitSunucuSahibiServisi(Guid sahipId) : IServerOwnerService
    {
        public Task<Guid?> GetServerOwnerIdAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<Guid?>(sahipId);

        public Task<bool> IsServerOwnerAsync(Guid userId, CancellationToken cancellationToken = default) =>
            Task.FromResult(userId == sahipId);
    }

    /// <summary>Komutu ÇALIŞTIRMAZ, yalnız kaydeder: "hiç çalışmadı" da ölçülebilir olmalı.</summary>
    private sealed class SahteTerminal(List<string> kayit) : ITerminalService
    {
        public Task<TerminalCommandResultDto> ExecuteAsync(
            ExecuteTerminalCommandRequest request, CancellationToken cancellationToken = default)
        {
            kayit.Add(request.Command);
            return Task.FromResult(new TerminalCommandResultDto
            {
                ExitCode = 0,
                StandardOutput = string.Empty,
                StandardError = string.Empty,
                WorkingDirectory = @"C:\ProgramData\AdaPanel",
                DurationMilliseconds = 1,
                TimedOut = false
            });
        }
    }

    /// <summary>Yalniz hedef kullaniciyi doner; digerlerine dokunulursa test patlar.</summary>
    private sealed class SabitKimlikServisi(PanelUser hedef) : IAuthService
    {
        public Task<CurrentUserDto> GetUserByIdAsync(Guid userId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new CurrentUserDto
            {
                Id = hedef.Id,
                UserName = hedef.UserName,
                Email = hedef.Email,
                Role = hedef.Role.ToString(),
                IsActive = hedef.IsActive
            });

        public Task<LoginResultDto> LoginAsync(LoginRequest request, string? ipAddress, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<CurrentSessionDto> GetCurrentSessionAsync(ClaimsPrincipal principal, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<TwoFactorSetupDto> BeginTwoFactorSetupAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<TwoFactorConfirmDto> ConfirmTwoFactorSetupAsync(Guid userId, string code, string? ipAddress, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<CurrentUserDto> DisableTwoFactorAsync(Guid userId, string password, string? ipAddress, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<RecoveryCodesDto> RegenerateRecoveryCodesAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task RequestPasswordResetAsync(string email, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task ResetPasswordAsync(string token, string newPassword, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ChangePasswordResultDto> ChangePasswordAsync(Guid userId, ChangePasswordRequest request, string? ipAddress, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<SetUserPasswordResultDto> SetPasswordForUserAsync(Guid targetUserId, SetUserPasswordRequest request, Guid actorUserId, string? ipAddress, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<SsoLinkDto> CreateSsoLinkAsync(Guid targetUserId, Guid actorUserId, string? ipAddress, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<CurrentUserDto?> TryRedeemSsoTokenAsync(string token, string? ipAddress, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class PatlayanAntiforgeryIkizi : Microsoft.AspNetCore.Antiforgery.IAntiforgery
    {
        public Microsoft.AspNetCore.Antiforgery.AntiforgeryTokenSet GetAndStoreTokens(HttpContext httpContext) => throw new NotSupportedException();

        public Microsoft.AspNetCore.Antiforgery.AntiforgeryTokenSet GetTokens(HttpContext httpContext) => throw new NotSupportedException();

        public Task<bool> IsRequestValidAsync(HttpContext httpContext) => throw new NotSupportedException();

        public Task ValidateRequestAsync(HttpContext httpContext) => throw new NotSupportedException();

        public void SetCookieTokenAndHeader(HttpContext httpContext) => throw new NotSupportedException();
    }

    private sealed class GirisiKaydedenServis(Action girisYapildi) : IAuthenticationService
    {
        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme) => throw new NotSupportedException();

        public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => throw new NotSupportedException();

        public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => throw new NotSupportedException();

        public Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal principal, AuthenticationProperties? properties)
        {
            girisYapildi();
            return Task.CompletedTask;
        }

        public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
    }
}
