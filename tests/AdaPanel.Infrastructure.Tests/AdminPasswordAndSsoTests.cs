using System.Security.Claims;
using System.Text.Json.Nodes;
using AdaPanel.Api.Security;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Ai;
using AdaPanel.Core.Dtos.Audit;
using AdaPanel.Core.Dtos.Auth;
using AdaPanel.Core.Dtos.Security;
using AdaPanel.Core.Dtos.Settings;
using AdaPanel.Core.Dtos.Users;
using AdaPanel.Core.Entities;
using AdaPanel.Core.Enums;
using AdaPanel.Core.Interfaces;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Services;
using AdaPanel.Infrastructure.Support;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// FATURALAMA SİSTEMİNİN İKİ EKSİK UCU: yönetici/bayi tarafından parola belirleme ve tek
/// kullanımlık giriş bağlantısı (SSO).
/// </summary>
/// <remarks>
/// <para>
/// Burada korunan şey iki özellik değil, iki HATA SINIFI:
/// </para>
/// <list type="number">
/// <item>
/// <b>Kiracı sızıntısı.</b> "Başkasının parolasını belirle" ucu, panelin en yüksek etkili
/// işlemidir: yanlış bir kapsam denetimi, bayiye RAKİBİNİN müşterisinin hesabını verir. Bu yüzden
/// yalnız mutlu yol değil, reddedilmesi gereken her aktör ayrı ayrı sabitleniyor.
/// </item>
/// <item>
/// <b>Yarım kesme.</b> Parolayı değiştirmek, açık oturumları ve API anahtarlarını düşürmüyorsa
/// erişimi kesmez — yalnız kesildiği İZLENİMİ verir. Testler bunu damga karşılaştırmasıyla değil,
/// gerçek çerez doğrulayıcısını çalıştırarak ölçüyor.
/// </item>
/// </list>
/// </remarks>
public sealed class AdminPasswordAndSsoTests
{
    private const string YeniParola = "Cok-Guclu-Yeni-Parola-2026";

    // --- 1) Parola belirleme: KİM, KİMİN parolasını belirleyebilir ---

    [Fact]
    public async Task Bayi_BASKA_bayinin_musterisinin_parolasini_DEGISTIREMEZ()
    {
        await using var duzenek = await Duzenek.OlusturAsync();
        var oncekiKarma = duzenek.YabanciMusteri.PasswordHash;

        var hata = await Assert.ThrowsAsync<AppException>(() => duzenek.Servis.SetPasswordForUserAsync(
            duzenek.YabanciMusteri.Id,
            new SetUserPasswordRequest { NewPassword = YeniParola },
            duzenek.Bayi.Id,
            "203.0.113.10"));

        Assert.Equal("users.forbidden", hata.MessageKey);
        Assert.Equal(403, hata.HttpStatusCode);
        Assert.Equal(oncekiKarma, duzenek.YabanciMusteri.PasswordHash);
        // Basarisiz denemede anahtarlar da DUSMEMELI: aksi halde yetkisi olmayan bir bayi, sadece
        // deneyerek rakibinin musterisinin entegrasyonlarini kesebilirdi (hizmet engelleme).
        Assert.Equal(0, duzenek.Anahtarlar.RevokeAllCallCount);
    }

    [Fact]
    public async Task Musteri_BASKASININ_parolasini_DEGISTIREMEZ()
    {
        // Denetleyicideki [Authorize(Roles = …)] bunu zaten engelliyor; kural SERVİSTE de var,
        // cunku ayni servis MCP araclarindan ve ileride eklenecek baska bir denetleyiciden de
        // cagrilabilir. Yetkilendirmenin tek kapisi oznitelik olsaydi o yollar korumasiz kalirdi.
        await using var duzenek = await Duzenek.OlusturAsync();

        var hata = await Assert.ThrowsAsync<AppException>(() => duzenek.Servis.SetPasswordForUserAsync(
            duzenek.YabanciMusteri.Id,
            new SetUserPasswordRequest { NewPassword = YeniParola },
            duzenek.Musteri.Id,
            null));

        Assert.Equal("users.forbidden", hata.MessageKey);
    }

    [Fact]
    public async Task Bayi_KENDI_musterisinin_parolasini_BELIRLEYEBILIR()
    {
        // Mesru akis BOZULMAMALI; aksi halde kural "her seyi reddet" olurdu.
        await using var duzenek = await Duzenek.OlusturAsync();
        duzenek.Anahtarlar.ActiveTokens = 2;

        var sonuc = await duzenek.Servis.SetPasswordForUserAsync(
            duzenek.Musteri.Id,
            new SetUserPasswordRequest { NewPassword = YeniParola },
            duzenek.Bayi.Id,
            null);

        Assert.Equal(
            PasswordVerificationResult.Success,
            new PasswordHasher<PanelUser>().VerifyHashedPassword(duzenek.Musteri, duzenek.Musteri.PasswordHash, YeniParola));
        Assert.Equal(2, sonuc.RevokedApiTokens);
    }

    [Fact]
    public async Task Yonetici_HERKESIN_parolasini_belirleyebilir()
    {
        await using var duzenek = await Duzenek.OlusturAsync();

        await duzenek.Servis.SetPasswordForUserAsync(
            duzenek.YabanciMusteri.Id,
            new SetUserPasswordRequest { NewPassword = YeniParola },
            duzenek.Yonetici.Id,
            null);

        Assert.Equal(
            PasswordVerificationResult.Success,
            new PasswordHasher<PanelUser>().VerifyHashedPassword(
                duzenek.YabanciMusteri, duzenek.YabanciMusteri.PasswordHash, YeniParola));
    }

    [Fact]
    public async Task KENDI_parolasi_bu_uctan_DEGISTIRILEMEZ()
    {
        // Bu uc MEVCUT parolayi sormaz. Kendi hesabina da acik olsaydi, calinmis bir yonetici
        // cerezi parolayi hic bilmeden hesabi kalici olarak devralirdi ve change-password
        // ucundaki mevcut-parola kosulu hicbir sey korumazdi.
        await using var duzenek = await Duzenek.OlusturAsync();

        var hata = await Assert.ThrowsAsync<AppException>(() => duzenek.Servis.SetPasswordForUserAsync(
            duzenek.Yonetici.Id,
            new SetUserPasswordRequest { NewPassword = YeniParola },
            duzenek.Yonetici.Id,
            null));

        Assert.Equal("users.forbidden", hata.MessageKey);
    }

    [Fact]
    public async Task Kisa_parola_REDDEDILIR()
    {
        await using var duzenek = await Duzenek.OlusturAsync();
        var oncekiKarma = duzenek.Musteri.PasswordHash;

        var hata = await Assert.ThrowsAsync<InvalidOperationException>(() => duzenek.Servis.SetPasswordForUserAsync(
            duzenek.Musteri.Id,
            new SetUserPasswordRequest { NewPassword = "kisa" },
            duzenek.Yonetici.Id,
            null));

        Assert.Equal("users.password_too_short", hata.Message);
        Assert.Equal(oncekiKarma, duzenek.Musteri.PasswordHash);
    }

    // --- 2) Parola degisince erisim GERCEKTEN kesilir ---

    [Fact]
    public async Task Parola_degisince_ACIK_CEREZ_dusurulur()
    {
        await using var duzenek = await Duzenek.OlusturAsync();

        // Cerez, parola DEGISMEDEN ONCEKI karmayla damgalanmis gibi kurulur.
        var damga = CookieSessionValidator.ComputeStamp(duzenek.Musteri.PasswordHash);
        Assert.NotNull((await duzenek.CerezDogrulaAsync(duzenek.Musteri, damga)).Principal);

        await duzenek.Servis.SetPasswordForUserAsync(
            duzenek.Musteri.Id,
            new SetUserPasswordRequest { NewPassword = YeniParola },
            duzenek.Yonetici.Id,
            null);

        // ESKI cerez artik gecersiz: damga parola karmasindan turer, karma degisti.
        var baglam = await duzenek.CerezDogrulaAsync(duzenek.Musteri, damga);
        Assert.Null(baglam.Principal);
    }

    [Fact]
    public async Task Parola_degisince_API_ANAHTARLARI_iptal_edilir()
    {
        await using var duzenek = await Duzenek.OlusturAsync();
        duzenek.Anahtarlar.ActiveTokens = 3;

        var sonuc = await duzenek.Servis.SetPasswordForUserAsync(
            duzenek.Musteri.Id,
            new SetUserPasswordRequest { NewPassword = YeniParola },
            duzenek.Yonetici.Id,
            null);

        // Anahtar paroladan BAGIMSIZ bir kimlik bilgisidir: dusurulmezse parolasi sifirlanan kisi
        // MCP/REST uzerinden erisimini surdurur ve kimse fark etmez.
        Assert.Equal(1, duzenek.Anahtarlar.RevokeAllCallCount);
        Assert.Equal(duzenek.Musteri.Id, duzenek.Anahtarlar.SonKullanici);
        Assert.Equal(3, sonuc.RevokedApiTokens);
    }

    [Fact]
    public async Task Parola_degisince_hesap_KILIDI_acilir()
    {
        await using var duzenek = await Duzenek.OlusturAsync();
        duzenek.Musteri.FailedLoginCount = 4;
        duzenek.Musteri.LockedUntilUtc = DateTime.UtcNow.AddMinutes(15);
        await duzenek.DbContext.SaveChangesAsync();

        await duzenek.Servis.SetPasswordForUserAsync(
            duzenek.Musteri.Id,
            new SetUserPasswordRequest { NewPassword = YeniParola },
            duzenek.Yonetici.Id,
            null);

        // Yonetici parolayi sifirlarken amaci kullaniciyi ICERI sokmaktir; kalan kilidi ayrica
        // temizlemek zorunda kalmamali.
        Assert.Null(duzenek.Musteri.LockedUntilUtc);
        Assert.Equal(0, duzenek.Musteri.FailedLoginCount);
    }

    // --- 3) Denetim kaydi: iz VAR, sir YOK ---

    [Fact]
    public async Task Parola_belirleme_denetim_kaydi_YAZAR_ama_PAROLAYI_yazmaz()
    {
        await using var duzenek = await Duzenek.OlusturAsync();

        await duzenek.Servis.SetPasswordForUserAsync(
            duzenek.Musteri.Id,
            new SetUserPasswordRequest { NewPassword = YeniParola },
            duzenek.Yonetici.Id,
            "203.0.113.10");

        var kayit = Assert.Single(duzenek.Denetim.Kayitlar, item => item.Action == "auth.password_set_by_admin");
        Assert.Contains(duzenek.Musteri.UserName, kayit.Details, StringComparison.Ordinal);
        Assert.Contains(duzenek.Yonetici.UserName, kayit.Details, StringComparison.Ordinal);
        Assert.Equal(duzenek.Yonetici.Id, kayit.UserId);

        // Kayit, korumaya calistigi sirrin IKINCI DEPOSU olmamali: denetim kaydi yedeklere yayilir
        // ve panelde okuma ucu vardir.
        Assert.DoesNotContain(YeniParola, kayit.Details, StringComparison.OrdinalIgnoreCase);
        Assert.All(duzenek.Denetim.Kayitlar, item =>
            Assert.DoesNotContain(YeniParola, item.Details, StringComparison.OrdinalIgnoreCase));
    }

    // --- 4) Tek kullanimlik giris baglantisi ---

    [Fact]
    public async Task SSO_belirteci_BIR_KEZ_calisir()
    {
        await using var duzenek = await Duzenek.OlusturAsync();

        var bag = await duzenek.Servis.CreateSsoLinkAsync(duzenek.Musteri.Id, duzenek.Yonetici.Id, null);
        var belirtec = Belirtec(bag.Url);

        var ilk = await duzenek.Servis.TryRedeemSsoTokenAsync(belirtec, "203.0.113.10");
        Assert.NotNull(ilk);
        Assert.Equal(duzenek.Musteri.Id, ilk!.Id);
    }

    [Fact]
    public async Task SSO_belirteci_IKINCI_kullanimda_REDDEDILIR()
    {
        // Belirtec ADRESTE gider: sunucu gunlugune ve tarayici gecmisine duser. Tek kullanimlik
        // olmasaydi, gunlugu okuyan biri kullanicinin hesabina girebilirdi.
        await using var duzenek = await Duzenek.OlusturAsync();

        var bag = await duzenek.Servis.CreateSsoLinkAsync(duzenek.Musteri.Id, duzenek.Yonetici.Id, null);
        var belirtec = Belirtec(bag.Url);

        Assert.NotNull(await duzenek.Servis.TryRedeemSsoTokenAsync(belirtec, null));
        Assert.Null(await duzenek.Servis.TryRedeemSsoTokenAsync(belirtec, null));
    }

    [Fact]
    public async Task SSO_belirteci_SURESI_gecince_REDDEDILIR()
    {
        await using var duzenek = await Duzenek.OlusturAsync();

        var bag = await duzenek.Servis.CreateSsoLinkAsync(duzenek.Musteri.Id, duzenek.Yonetici.Id, null);
        var belirtec = Belirtec(bag.Url);

        // Omur dakikalarla olculuyor; testin beklemesi yerine deponun ZAMANI geriye alinir.
        duzenek.BelirtecSuresiniGecir();

        Assert.Null(await duzenek.Servis.TryRedeemSsoTokenAsync(belirtec, null));

        // Kisa omur bir tercih degil sart: gerekce belgede degil, olculebilir bicimde burada.
        Assert.True(bag.ExpiresAtUtc - DateTime.UtcNow <= TimeSpan.FromMinutes(10));
    }

    [Fact]
    public async Task SSO_belirteci_YENISI_uretilince_ESKISI_duser()
    {
        await using var duzenek = await Duzenek.OlusturAsync();

        var eski = Belirtec((await duzenek.Servis.CreateSsoLinkAsync(duzenek.Musteri.Id, duzenek.Yonetici.Id, null)).Url);
        var yeni = Belirtec((await duzenek.Servis.CreateSsoLinkAsync(duzenek.Musteri.Id, duzenek.Yonetici.Id, null)).Url);

        Assert.Null(await duzenek.Servis.TryRedeemSsoTokenAsync(eski, null));
        Assert.NotNull(await duzenek.Servis.TryRedeemSsoTokenAsync(yeni, null));
    }

    [Fact]
    public async Task Bilinmeyen_belirtec_REDDEDILIR()
    {
        await using var duzenek = await Duzenek.OlusturAsync();

        Assert.Null(await duzenek.Servis.TryRedeemSsoTokenAsync("uydurma-belirtec", null));
        Assert.Null(await duzenek.Servis.TryRedeemSsoTokenAsync(string.Empty, null));
    }

    [Fact]
    public async Task Bayi_BASKA_bayinin_musterisi_icin_SSO_bagi_URETEMEZ()
    {
        await using var duzenek = await Duzenek.OlusturAsync();

        var hata = await Assert.ThrowsAsync<AppException>(() => duzenek.Servis.CreateSsoLinkAsync(
            duzenek.YabanciMusteri.Id, duzenek.Bayi.Id, null));

        Assert.Equal("users.forbidden", hata.MessageKey);
    }

    [Fact]
    public async Task PASIF_hesap_icin_SSO_bagi_URETILMEZ()
    {
        // Normal giris yolu pasif kullaniciyi reddediyor; burada uretmek, hesabi kapatmanin
        // etrafindan dolasan ikinci bir giris kapisi acmak olurdu.
        await using var duzenek = await Duzenek.OlusturAsync();
        duzenek.Musteri.IsActive = false;
        await duzenek.DbContext.SaveChangesAsync();

        var hata = await Assert.ThrowsAsync<AppException>(() => duzenek.Servis.CreateSsoLinkAsync(
            duzenek.Musteri.Id, duzenek.Yonetici.Id, null));

        Assert.Equal("auth.user_inactive", hata.MessageKey);
    }

    [Fact]
    public async Task Baglanti_uretildikten_SONRA_hesap_kapatilirsa_belirtec_REDDEDILIR()
    {
        await using var duzenek = await Duzenek.OlusturAsync();
        var belirtec = Belirtec((await duzenek.Servis.CreateSsoLinkAsync(duzenek.Musteri.Id, duzenek.Yonetici.Id, null)).Url);

        duzenek.Musteri.IsActive = false;
        await duzenek.DbContext.SaveChangesAsync();

        Assert.Null(await duzenek.Servis.TryRedeemSsoTokenAsync(belirtec, null));
    }

    [Fact]
    public async Task ASKIDAKI_abonelik_icin_SSO_bagi_URETILIR()
    {
        // BILINCLI URUN KARARI: panelde aski girisi degil SITELERI kapatir — musteri panele girip
        // neden askida oldugunu, kotasini ve faturasini gorebilmeli, cozum orada. Tam da odemesi
        // geciken musteri icin "Panele git" dugmesini olu birakmak askinin amacina ters duserdi.
        // Bu test, ileride buraya bir aski denetimi eklenirse KIRILIR ve karari hatirlatir.
        await using var duzenek = await Duzenek.OlusturAsync();
        duzenek.Kapsam.Askiyaal(duzenek.Musteri.Id);

        var bag = await duzenek.Servis.CreateSsoLinkAsync(duzenek.Musteri.Id, duzenek.Yonetici.Id, null);

        Assert.NotEmpty(Belirtec(bag.Url));
    }

    [Fact]
    public async Task Baglanti_PANEL_adresini_ve_belirteci_tasir()
    {
        await using var duzenek = await Duzenek.OlusturAsync();

        var bag = await duzenek.Servis.CreateSsoLinkAsync(duzenek.Musteri.Id, duzenek.Yonetici.Id, null);

        Assert.StartsWith("https://panel.ornek.test:8890/api/v1/auth/sso?token=", bag.Url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Panel_adresi_BOSSA_bile_baglantiya_ISTEMCI_adresi_GIRMEZ()
    {
        // ESKI DAVRANIS (kaldirildi): panel adresi bosken baglanti isteğin `Host` basligindan
        // kuruluyordu ve bu test "panel.local:9443" gorunmesini SART kosuyordu. Gerekce "yer
        // tutuculu olu baglanti gondermeyelim"di; olculdu ve gerekce cokdu -- panel adresi
        // uretimde asla bos olmuyor (PanelPreferencesService, bkz. asagidaki nobetci test), yani
        // o yedek hic calismiyordu. Duran tek sey, TEK KULLANIMLIK GIRIS BELIRTECINI saldirganin
        // sectigi bir ana ada goturebilen bir kapiydi.
        //
        // Yeni sozlesme: adres YALNIZ ayardan gelir. Ayar (yalniz testte kurulabilen) bos bir deger
        // tasiyorsa baglanti yer tutucuyla OLU gider -- acilmayan baglanti, saldirgana giden
        // calisan baglantidan iyidir.
        await using var duzenek = await Duzenek.OlusturAsync(panelAdresi: string.Empty);

        var bag = await duzenek.Servis.CreateSsoLinkAsync(duzenek.Musteri.Id, duzenek.Yonetici.Id, null);

        Assert.StartsWith($"https://{PanelAddress.UnknownHostPlaceholder}/api/v1/auth/sso?token=", bag.Url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Belirtec_denetim_kaydina_YAZILMAZ()
    {
        await using var duzenek = await Duzenek.OlusturAsync();

        var bag = await duzenek.Servis.CreateSsoLinkAsync(duzenek.Musteri.Id, duzenek.Yonetici.Id, "203.0.113.10");
        var belirtec = Belirtec(bag.Url);
        await duzenek.Servis.TryRedeemSsoTokenAsync(belirtec, "198.51.100.7");

        Assert.Contains(duzenek.Denetim.Kayitlar, item => item.Action == "auth.sso_link.created");
        Assert.Contains(duzenek.Denetim.Kayitlar, item => item.Action == "auth.sso_link.redeemed");
        Assert.All(duzenek.Denetim.Kayitlar, item =>
            Assert.DoesNotContain(belirtec, item.Details, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Reddedilen_belirtec_de_denetim_kaydina_DUSER()
    {
        // Sessiz red, "musteri panele giremiyor" sikayetinin sebebini cevapsiz birakirdi.
        await using var duzenek = await Duzenek.OlusturAsync();

        await duzenek.Servis.TryRedeemSsoTokenAsync("uydurma-belirtec", "198.51.100.7");

        Assert.Contains(duzenek.Denetim.Kayitlar, item => item.Action == "auth.sso_link.rejected");
    }

    /// <summary>Bağlantı adresinden belirteci ayıklar (modülün yaptığının aynısı: adrese gidilir).</summary>
    private static string Belirtec(string url)
    {
        var isaret = url.IndexOf("token=", StringComparison.Ordinal);
        Assert.True(isaret > 0, $"Baglanti belirtec tasimiyor: {url}");
        return Uri.UnescapeDataString(url[(isaret + "token=".Length)..]);
    }

    // --- duzenek ---

    private sealed class Duzenek : IAsyncDisposable
    {
        private readonly SqliteConnection baglanti;
        private readonly string veriKoku;

        private Duzenek(SqliteConnection baglanti, AdaPanelDbContext dbContext, AuthService servis, string veriKoku)
        {
            this.baglanti = baglanti;
            this.veriKoku = veriKoku;
            DbContext = dbContext;
            Servis = servis;
        }

        public AdaPanelDbContext DbContext { get; }

        public AuthService Servis { get; }

        public PanelUser Yonetici { get; private set; } = null!;

        public PanelUser Bayi { get; private set; } = null!;

        public PanelUser Musteri { get; private set; } = null!;

        /// <summary>BAŞKA bir bayinin müşterisi — kiracı sınırının öbür tarafı.</summary>
        public PanelUser YabanciMusteri { get; private set; } = null!;

        public SahteKapsam Kapsam { get; private set; } = null!;

        public SayanAnahtarServisi Anahtarlar { get; private set; } = null!;

        public KayitEdenDenetim Denetim { get; private set; } = null!;

        public static async Task<Duzenek> OlusturAsync(string panelAdresi = "panel.ornek.test")
        {
            var baglanti = new SqliteConnection("Data Source=:memory:");
            await baglanti.OpenAsync();
            var secenekler = new DbContextOptionsBuilder<AdaPanelDbContext>().UseSqlite(baglanti).Options;
            var dbContext = new AdaPanelDbContext(secenekler);
            await dbContext.Database.EnsureCreatedAsync();

            var hasher = new PasswordHasher<PanelUser>();
            var yonetici = Kullanici(hasher, "yonetici", UserRole.Administrator);
            var bayi = Kullanici(hasher, "bayi", UserRole.Reseller);
            var otekiBayi = Kullanici(hasher, "oteki-bayi", UserRole.Reseller);
            var musteri = Kullanici(hasher, "musteri", UserRole.Customer);
            var yabanciMusteri = Kullanici(hasher, "yabanci-musteri", UserRole.Customer);
            dbContext.Users.AddRange(yonetici, bayi, otekiBayi, musteri, yabanciMusteri);
            await dbContext.SaveChangesAsync();

            var veriKoku = Path.Combine(Path.GetTempPath(), $"ada-sso-{Guid.NewGuid():N}");
            Directory.CreateDirectory(veriKoku);
            var runtimeOptions = Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = veriKoku });

            var kapsam = new SahteKapsam();
            kapsam.SahipAta(musteri.Id, bayi.Id);
            kapsam.SahipAta(yabanciMusteri.Id, otekiBayi.Id);

            var anahtarlar = new SayanAnahtarServisi();
            var denetim = new KayitEdenDenetim();

            var servis = new AuthService(
                dbContext,
                hasher,
                new ThrowingSecretProtector(),
                new ThrowingTotpService(),
                denetim,
                new CountingBruteForceProtection(),
                new ThrowingNotificationService(),
                new SahteTercihler(panelAdresi),
                new SahteGuvenlik(),
                kapsam,
                anahtarlar,
                new JsonFileStore(runtimeOptions),
                runtimeOptions,
                NullLogger<AuthService>.Instance);

            return new Duzenek(baglanti, dbContext, servis, veriKoku)
            {
                Yonetici = yonetici,
                Bayi = bayi,
                Musteri = musteri,
                YabanciMusteri = yabanciMusteri,
                Kapsam = kapsam,
                Anahtarlar = anahtarlar,
                Denetim = denetim
            };
        }

        /// <summary>
        /// Belirtecin ömrünü doldurur: depodaki bitiş anını geçmişe alır. Testin iki dakika
        /// beklemesinin alternatifi, ömrü test için kısaltılabilir yapmaktı — o da ürünün
        /// davranışını test uğruna değiştirmek olurdu.
        /// </summary>
        public void BelirtecSuresiniGecir()
        {
            var yol = Path.Combine(veriKoku, "config", "sso-tokens.json");
            var kok = JsonNode.Parse(File.ReadAllText(yol))!;
            foreach (var kayit in kok["entries"]!.AsArray())
            {
                kayit!["expiresUtc"] = DateTime.UtcNow.AddMinutes(-1);
            }

            File.WriteAllText(yol, kok.ToJsonString());
        }

        /// <summary>
        /// Panelin GERÇEK çerez doğrulayıcısını çalıştırır. Damgayı elle karşılaştırmak, yalnız
        /// hesaplamanın kendisini ölçerdi; asıl soru çerezin REDDEDİLİP reddedilmediği.
        /// </summary>
        public async Task<CookieValidatePrincipalContext> CerezDogrulaAsync(PanelUser kullanici, string damga)
        {
            var kimlik = new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, kullanici.Id.ToString()),
                    new Claim(ClaimTypes.Name, kullanici.UserName),
                    new Claim(ClaimTypes.Role, kullanici.Role.ToString()),
                    new Claim(CookieSessionValidator.CredentialStampClaim, damga),
                    OturumTestOrtami.OturumKimligiTalebi()
                ],
                CookieAuthenticationDefaults.AuthenticationScheme);

            var servisler = new ServiceCollection();
            servisler.AddSingleton(DbContext);
            servisler.AddSingleton(OturumTestOrtami.BosKaraListe());
            servisler.AddSingleton<IAuthenticationService>(new SessizKimlikServisi());
            var http = new DefaultHttpContext { RequestServices = servisler.BuildServiceProvider() };

            var baglam = new CookieValidatePrincipalContext(
                http,
                new AuthenticationScheme(
                    CookieAuthenticationDefaults.AuthenticationScheme,
                    displayName: null,
                    handlerType: typeof(CookieAuthenticationHandler)),
                new CookieAuthenticationOptions(),
                new AuthenticationTicket(
                    new ClaimsPrincipal(kimlik),
                    new AuthenticationProperties(),
                    CookieAuthenticationDefaults.AuthenticationScheme));

            await CookieSessionValidator.OnValidatePrincipalAsync(baglam);
            return baglam;
        }

        private static PanelUser Kullanici(IPasswordHasher<PanelUser> hasher, string ad, UserRole rol)
        {
            var kullanici = new PanelUser
            {
                Id = Guid.NewGuid(),
                UserName = ad,
                Email = $"{ad}@ornek.test",
                Role = rol,
                PreferredLanguage = "tr",
                IsActive = true,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            };
            kullanici.PasswordHash = hasher.HashPassword(kullanici, "Baslangic-Parolasi-2026");
            return kullanici;
        }

        public async ValueTask DisposeAsync()
        {
            await DbContext.DisposeAsync();
            await baglanti.DisposeAsync();
            try
            {
                Directory.Delete(veriKoku, recursive: true);
            }
            catch (IOException)
            {
                // Gecici klasor silinemezse test sonucunu etkilemez.
            }
        }
    }

    /// <summary>Denetim kaydının AYRINTISINI da tutar: "parola kayda düşmedi" ancak böyle ölçülebilir.</summary>
    internal sealed class KayitEdenDenetim : IAuditService
    {
        public List<(string Action, string Details, Guid? UserId, string? Ip)> Kayitlar { get; } = [];

        public Task WriteAsync(string action, string resource, string details, Guid? userId = null, string? ipAddress = null, CancellationToken cancellationToken = default)
            => WriteForSubscriptionAsync(action, resource, details, userId, null, ipAddress, cancellationToken);

        public Task WriteForSubscriptionAsync(string action, string resource, string details, Guid? userId, Guid? subscriptionUserId, string? ipAddress, CancellationToken cancellationToken = default)
        {
            Kayitlar.Add((action, details, userId, ipAddress));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyCollection<AuditLogDto>> GetRecentAsync(int take, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyCollection<AuditLogDto>>([]);

        public Task<AuditLogPageDto> SearchAsync(AuditLogQuery query, AuditVisibility visibility, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<AuditLogDto> StreamAsync(AuditLogQuery query, AuditVisibility visibility, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AuditVisibility> ResolveVisibilityAsync(Guid userId, UserRole role, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AuditRetentionSettingsDto> GetRetentionAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AuditRetentionSettingsDto> SetRetentionAsync(int retentionDays, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<int> PurgeAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    /// <summary>Anahtar iptalinin KAÇ KEZ ve KİM için çağrıldığını sayar.</summary>
    internal sealed class SayanAnahtarServisi : IApiTokenService
    {
        public int ActiveTokens { get; set; }

        public int RevokeAllCallCount { get; private set; }

        public Guid? SonKullanici { get; private set; }

        public Task<int> RevokeAllForUserAsync(Guid userId, CancellationToken cancellationToken = default)
        {
            RevokeAllCallCount++;
            SonKullanici = userId;
            return Task.FromResult(ActiveTokens);
        }

        public Task<IReadOnlyList<ApiTokenDto>> GetForUserAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CreatedApiTokenDto> CreateAsync(Guid userId, CreateApiTokenRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RevokeAsync(Guid userId, Guid tokenId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<PanelApiToken?> ValidateAsync(string presentedToken, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    /// <summary>Sahiplik (bayi → müşteri) ve askı durumunu tutan asgari kapsam servisi.</summary>
    internal sealed class SahteKapsam : ICustomerScopeService
    {
        private readonly Dictionary<Guid, Guid> sahipler = [];
        private readonly HashSet<Guid> askidakiler = [];

        public void SahipAta(Guid userId, Guid ownerId) => sahipler[userId] = ownerId;

        public void Askiyaal(Guid userId) => askidakiler.Add(userId);

        public Task<Guid?> GetOwnerAsync(Guid userId, CancellationToken cancellationToken = default)
            => Task.FromResult(sahipler.TryGetValue(userId, out var sahip) ? sahip : (Guid?)null);

        public Task<CustomerScope> GetScopeAsync(Guid userId, CancellationToken cancellationToken = default)
            => Task.FromResult(new CustomerScope(
                [], 0, 0, 0, 0, 0,
                SuspendedAtUtc: askidakiler.Contains(userId) ? DateTime.UtcNow : null,
                SuspendReason: askidakiler.Contains(userId) ? "manual" : string.Empty));

        public Task<Guid?> GetParentSubscriptionAsync(Guid userId, CancellationToken cancellationToken = default)
            => Task.FromResult<Guid?>(null);

        public Task<IReadOnlyCollection<string>?> GetSubUserSectionsAsync(Guid userId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyCollection<string>?>(null);

        public Task<IReadOnlyCollection<string>> GetAssignedSitesAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetScopeAsync(Guid userId, IEnumerable<string> siteNames, int maxSites, int maxDiskMb, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetExtraLimitsAsync(Guid userId, int maxDatabases, int maxMailboxes, int maxScheduledTasks, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RemoveUserAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RemoveSiteFromAllScopesAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<string>> GetAllowedMailDomainsAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DiskUsage> GetDiskUsageMbAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetOwnerAsync(Guid userId, Guid? ownerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<Guid>> GetOwnedUserIdsAsync(Guid ownerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<string>> GetResellerSitesAsync(Guid resellerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<string>> GetResellerMailDomainsAsync(Guid resellerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Guid?> GetSiteOwnerUserIdAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetParentSubscriptionAsync(Guid userId, Guid? parentUserId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<Guid>> GetSubUserIdsAsync(Guid parentUserId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetSubUserSectionsAsync(Guid userId, IReadOnlyCollection<string>? sections, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetPlanAsync(Guid userId, string? planId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetExpiryAsync(Guid userId, DateTime? expiresAtUtc, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SuspendAsync(Guid userId, string reason, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ResumeAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<Guid>> GetSuspendedUserIdsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<Guid>> GetSubscriptionUserIdsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class SahteTercihler(string panelAdresi) : IPanelPreferencesService
    {
        public Task<PanelPreferencesDto> GetAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new PanelPreferencesDto { ServerAddress = panelAdresi, PreferredHttpsPort = 8890 });

        public Task<PanelPreferencesDto> UpdateAsync(UpdatePanelPreferencesRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class SahteGuvenlik : IPanelSecurityService
    {
        public Task<string> GetSecureEntryPathAsync(CancellationToken cancellationToken = default) => Task.FromResult("gizliyol");

        public Task<SecuritySettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SecuritySettingsDto> UpdateSettingsAsync(UpdateSecuritySettingsRequest request, Guid actorUserId, string? ipAddress, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> IsIpAllowedAsync(string? ipAddress, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    /// <summary>Çerez doğrulayıcısı reddettiğinde SignOutAsync çağırır; burada sessizce yutulur.</summary>
    private sealed class SessizKimlikServisi : IAuthenticationService
    {
        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme) => throw new NotSupportedException();
        public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => throw new NotSupportedException();
        public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => throw new NotSupportedException();
        public Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal principal, AuthenticationProperties? properties) => throw new NotSupportedException();
        public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
    }
}
