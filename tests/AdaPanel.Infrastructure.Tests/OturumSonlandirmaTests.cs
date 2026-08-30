using System.Security.Claims;
using AdaPanel.Api.Security;
using AdaPanel.Core.Entities;
using AdaPanel.Core.Enums;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// ÇIKIŞ ARTIK SUNUCU TARAFINDA DA GEÇERSİZ KILIYOR + OTURUM SABİTLEME ÖLÇÜMÜ.
/// </summary>
/// <remarks>
/// <para>
/// <b>Kapatılan açık:</b> panelin oturum çerezi kendi kendine yeterlidir (kimlik ve bitiş anı
/// çerezin içinde şifrelidir, sunucuda kayıt yoktur). Bu yüzden <c>SignOutAsync</c> yalnız
/// TARAYICIDAKİ kopyayı siliyordu; çerezin kendisi 12 saat (kalıcı seçilmişse 14 gün) geçerli
/// kalmaya devam ediyordu. Yani çerezi önceden ele geçirmiş biri için "Çıkış" düğmesinin hiçbir
/// karşılığı yoktu.
/// </para>
/// <para>
/// <b>Oturum sabitleme (session fixation):</b> her girişte YENİ bir oturum kimliği üretilmesi, bu
/// dosyada bir yorum değil bir ölçüm hâline gelir — girişten önceki ve sonraki oturum aynı olamaz.
/// </para>
/// </remarks>
public sealed class OturumSonlandirmaTests
{
    // --- 1) Girişte oturum kimliği ---

    [Fact]
    public async Task Giriste_cereze_oturum_kimligi_yazilir()
    {
        await using var duzenek = await Duzenek.OlusturAsync();

        var kimlik = duzenek.YeniKimlik();
        await CookieSessionValidator.OnSigningInAsync(duzenek.GirisBaglami(kimlik));

        var oturum = kimlik.FindFirst(SessionRevocationStore.SessionIdClaim)?.Value;
        Assert.False(string.IsNullOrWhiteSpace(oturum));
    }

    /// <summary>
    /// OTURUM SABİTLEME: saldırganın kurbana önceden verdiği bir oturum, girişten sonra ARTIK O
    /// OTURUM DEĞİLDİR. Kimlik her girişte yeniden üretilir ve eskisi çereze taşınmaz.
    /// </summary>
    [Fact]
    public async Task Her_giriste_YENI_oturum_kimligi_uretilir()
    {
        await using var duzenek = await Duzenek.OlusturAsync();

        var birinci = duzenek.YeniKimlik();
        await CookieSessionValidator.OnSigningInAsync(duzenek.GirisBaglami(birinci));
        var ikinci = duzenek.YeniKimlik();
        await CookieSessionValidator.OnSigningInAsync(duzenek.GirisBaglami(ikinci));

        Assert.NotEqual(
            birinci.FindFirst(SessionRevocationStore.SessionIdClaim)?.Value,
            ikinci.FindFirst(SessionRevocationStore.SessionIdClaim)?.Value);
    }

    /// <summary>
    /// Saldırganın ÖNCEDEN yerleştirdiği bir oturum kimliği girişte KORUNMAZ, üzerine yazılır.
    /// Tek bir talep kalır: iki talep bırakmak "hangisi geçerli" sorusunu belirsiz yapardı.
    /// </summary>
    [Fact]
    public async Task Girisde_ONCEDEN_yerlestirilmis_oturum_kimligi_atilir()
    {
        await using var duzenek = await Duzenek.OlusturAsync();

        var kimlik = duzenek.YeniKimlik();
        kimlik.AddClaim(new Claim(SessionRevocationStore.SessionIdClaim, "saldirganin-secdigi-deger"));

        await CookieSessionValidator.OnSigningInAsync(duzenek.GirisBaglami(kimlik));

        var talepler = kimlik.FindAll(SessionRevocationStore.SessionIdClaim).Select(talep => talep.Value).ToList();
        Assert.Single(talepler);
        Assert.NotEqual("saldirganin-secdigi-deger", talepler[0]);
    }

    // --- 2) Çıkış ---

    [Fact]
    public async Task Cikis_yapilan_cerez_REDDEDILIR()
    {
        await using var duzenek = await Duzenek.OlusturAsync();

        await duzenek.KaraListe.RevokeAsync(duzenek.OturumKimligi);

        var baglam = await duzenek.DogrulaAsync();

        Assert.Null(baglam.Principal);
        Assert.True(duzenek.CikisYapildi);
    }

    /// <summary>
    /// FAZLA KAPATMA NÖBETÇİSİ: BAŞKA bir oturumun çıkış yapması bu oturumu düşürmemeli. Telefondan
    /// çıkmak masaüstündeki oturumu da kapatsaydı düzeltme, açığın kendisinden kötü olurdu.
    /// </summary>
    [Fact]
    public async Task Cikis_yapmayan_oturum_CALISMAYA_DEVAM_eder()
    {
        await using var duzenek = await Duzenek.OlusturAsync();

        await duzenek.KaraListe.RevokeAsync(SessionRevocationStore.NewSessionId());

        var baglam = await duzenek.DogrulaAsync();

        Assert.NotNull(baglam.Principal);
        Assert.False(duzenek.CikisYapildi);
    }

    /// <summary>
    /// ÇIKIŞ 60 SANİYELİK DOĞRULAMA ARALIĞINI BEKLEMEZ. Kimlik damgası denetimi (parola değişimi,
    /// rol düşürme) bilerek aralıklıdır; çıkış olamaz — "çıkış yaptım" diyen kullanıcının çerezi
    /// bir dakika daha çalışıyorsa çıkış, tam da anlamlı olduğu durumda işe yaramaz.
    /// </summary>
    [Fact]
    public async Task Cikis_ARALIK_beklemeden_etkili_olur()
    {
        await using var duzenek = await Duzenek.OlusturAsync();

        // Önce bir kez doğrula: çerezin özelliklerine "az önce doğrulandı" damgası yazılır.
        var ozellikler = new AuthenticationProperties();
        var ilk = await duzenek.DogrulaAsync(ozellikler);
        Assert.NotNull(ilk.Principal);
        Assert.NotEmpty(ozellikler.Items);

        await duzenek.KaraListe.RevokeAsync(duzenek.OturumKimligi);

        var ikinci = await duzenek.DogrulaAsync(ozellikler);
        Assert.Null(ikinci.Principal);
        Assert.True(duzenek.CikisYapildi);
    }

    /// <summary>
    /// KİMLİĞİ OLMAYAN (bu sürümden önceki) ÇEREZ REDDEDİLİR. "Kimlik yoksa geçerli say" demek,
    /// çıkışı sunucu tarafında hiç uygulayamamak demekti; kimlik damgasındaki kuralın aynısı.
    /// </summary>
    [Fact]
    public async Task Oturum_kimligi_TASIMAYAN_cerez_reddedilir()
    {
        await using var duzenek = await Duzenek.OlusturAsync();

        var baglam = await duzenek.DogrulaAsync(oturumKimligiEkle: false);

        Assert.Null(baglam.Principal);
        Assert.True(duzenek.CikisYapildi);
    }

    /// <summary>
    /// KARA LİSTE YENİDEN BAŞLATMAYI ATLATIR. Çerez biletleri veri koruma anahtarları kalıcı
    /// olduğu için yeniden başlatmadan sağ çıkar; liste yalnız bellekte tutulsaydı panelin yeniden
    /// başlaması çıkış yapılmış BÜTÜN oturumları diriltirdi.
    /// </summary>
    [Fact]
    public async Task Kara_liste_YENIDEN_BASLATMAYI_atlatir()
    {
        await using var duzenek = await Duzenek.OlusturAsync();

        await duzenek.KaraListe.RevokeAsync(duzenek.OturumKimligi);

        // Yeni bir örnek = panelin yeniden başlaması. Aynı veri kökünü okur.
        var yenidenBaslatilan = OturumTestOrtami.KaraListe(duzenek.VeriKoku);
        Assert.True(await yenidenBaslatilan.IsRevokedAsync(duzenek.OturumKimligi));
    }

    /// <summary>
    /// Kara liste süresiz büyümez: kaydın ömrü, çerezin ulaşabileceği EN UZUN ömürden (kalıcı
    /// giriş = 14 gün) fazladır. Aksi hâlde listeden düşen bir kimliğin çerezi hâlâ canlı olurdu.
    /// </summary>
    [Fact]
    public void Kayit_omru_en_uzun_cerez_omrunden_UZUN()
    {
        Assert.True(SessionRevocationStore.Retention > TimeSpan.FromDays(14));
    }

    // --- 3) Denetleyici bağlantısı: /auth/logout gerçekten kara listeye yazıyor mu ---

    /// <summary>
    /// ÇIKIŞ UCUNUN KENDİSİ ÖLÇÜLÜYOR. Kara listenin çalıştığını göstermek yetmez; asıl soru
    /// "çıkış düğmesi onu ÇAĞIRIYOR MU". Bu bağlantı kopuk olsaydı yukarıdaki bütün testler yeşil
    /// yanar ve panel yine hiçbir oturumu düşürmezdi — bu depoda defalarca ölçülmüş hata sınıfı
    /// ("arka uç hazır, teslim yolu kopuk").
    /// </summary>
    [Fact]
    public async Task Cikis_ucu_oturum_kimligini_KARA_LISTEYE_yazar()
    {
        var karaListe = OturumTestOrtami.KaraListe(
            Path.Combine(Path.GetTempPath(), $"adapanel-cikis-{Guid.NewGuid():N}"));
        var oturum = SessionRevocationStore.NewSessionId();
        var denetleyici = CikisDenetleyicisi(karaListe, oturum, out var denetim);

        await denetleyici.LogoutAsync(CancellationToken.None);

        Assert.True(await karaListe.IsRevokedAsync(oturum));
        Assert.Contains("auth.logout.succeeded", denetim.Actions);
    }

    private static AdaPanel.Api.Controllers.AuthController CikisDenetleyicisi(
        SessionRevocationStore karaListe,
        string oturumKimligi,
        out RecordingAuditService denetim)
    {
        denetim = new RecordingAuditService();
        var denetleyici = new AdaPanel.Api.Controllers.AuthController(
            new PatlayanAntiforgery(),
            new PatlayanKimlikServisi(),
            denetim,
            new ThrowingSecurityService(),
            karaListe);

        var servisler = new ServiceCollection();
        servisler.AddSingleton<IAuthenticationService>(new CikisiKaydedenServis(() => { }));
        var http = new DefaultHttpContext
        {
            RequestServices = servisler.BuildServiceProvider(),
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                    new Claim(ClaimTypes.Name, "admin"),
                    new Claim(SessionRevocationStore.SessionIdClaim, oturumKimligi)
                ],
                CookieAuthenticationDefaults.AuthenticationScheme))
        };

        denetleyici.ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext { HttpContext = http };
        return denetleyici;
    }

    // --- düzenek ---

    private sealed class Duzenek : IAsyncDisposable
    {
        private readonly SqliteConnection baglanti;
        private readonly string girisAnindakiKarma;

        private Duzenek(SqliteConnection baglanti, AdaPanelDbContext dbContext, PanelUser kullanici, string veriKoku)
        {
            this.baglanti = baglanti;
            DbContext = dbContext;
            Kullanici = kullanici;
            VeriKoku = veriKoku;
            girisAnindakiKarma = kullanici.PasswordHash;
            KaraListe = OturumTestOrtami.KaraListe(veriKoku);
        }

        public AdaPanelDbContext DbContext { get; }

        public PanelUser Kullanici { get; }

        public string VeriKoku { get; }

        public SessionRevocationStore KaraListe { get; }

        public string OturumKimligi { get; } = SessionRevocationStore.NewSessionId();

        public bool CikisYapildi { get; private set; }

        public static async Task<Duzenek> OlusturAsync()
        {
            var baglanti = new SqliteConnection("Data Source=:memory:");
            await baglanti.OpenAsync();
            var secenekler = new DbContextOptionsBuilder<AdaPanelDbContext>().UseSqlite(baglanti).Options;
            var dbContext = new AdaPanelDbContext(secenekler);
            await dbContext.Database.EnsureCreatedAsync();

            var kullanici = new PanelUser
            {
                Id = Guid.NewGuid(),
                UserName = "admin",
                Email = "admin@ornek.test",
                Role = UserRole.Administrator,
                PasswordHash = "PAROLA-KARMASI",
                IsActive = true
            };

            dbContext.Users.Add(kullanici);
            await dbContext.SaveChangesAsync();

            var veriKoku = Path.Combine(Path.GetTempPath(), $"adapanel-oturum-{Guid.NewGuid():N}");
            Directory.CreateDirectory(veriKoku);

            return new Duzenek(baglanti, dbContext, kullanici, veriKoku);
        }

        /// <summary>Girişte oluşturulan HAM kimlik (henüz damga/oturum kimliği yok).</summary>
        public ClaimsIdentity YeniKimlik() => new(
            [
                new Claim(ClaimTypes.NameIdentifier, Kullanici.Id.ToString()),
                new Claim(ClaimTypes.Name, Kullanici.UserName),
                new Claim(ClaimTypes.Role, Kullanici.Role.ToString())
            ],
            CookieAuthenticationDefaults.AuthenticationScheme);

        public CookieSigningInContext GirisBaglami(ClaimsIdentity kimlik) => new(
            HttpBaglami(),
            Sema(),
            new CookieAuthenticationOptions(),
            new ClaimsPrincipal(kimlik),
            new AuthenticationProperties(),
            new CookieOptions());

        public async Task<CookieValidatePrincipalContext> DogrulaAsync(
            AuthenticationProperties? ozellikler = null,
            bool oturumKimligiEkle = true)
        {
            var kimlik = YeniKimlik();
            kimlik.AddClaim(new Claim(
                CookieSessionValidator.CredentialStampClaim,
                CookieSessionValidator.ComputeStamp(girisAnindakiKarma)));

            if (oturumKimligiEkle)
            {
                kimlik.AddClaim(new Claim(SessionRevocationStore.SessionIdClaim, OturumKimligi));
            }

            var baglam = new CookieValidatePrincipalContext(
                HttpBaglami(),
                Sema(),
                new CookieAuthenticationOptions(),
                new AuthenticationTicket(
                    new ClaimsPrincipal(kimlik),
                    ozellikler ?? new AuthenticationProperties(),
                    CookieAuthenticationDefaults.AuthenticationScheme));

            await CookieSessionValidator.OnValidatePrincipalAsync(baglam);
            return baglam;
        }

        private static AuthenticationScheme Sema() => new(
            CookieAuthenticationDefaults.AuthenticationScheme,
            displayName: null,
            handlerType: typeof(CookieAuthenticationHandler));

        private HttpContext HttpBaglami()
        {
            var servisler = new ServiceCollection();
            servisler.AddSingleton(DbContext);
            servisler.AddSingleton(KaraListe);
            servisler.AddSingleton<IAuthenticationService>(new CikisiKaydedenServis(() => CikisYapildi = true));

            return new DefaultHttpContext { RequestServices = servisler.BuildServiceProvider() };
        }

        public async ValueTask DisposeAsync()
        {
            await DbContext.DisposeAsync();
            await baglanti.DisposeAsync();

            // Geçici klasör BİLEREK silinmiyor; gerekçe GirisDuzenegi'nde yazılı.
        }
    }

    /// <summary>Çıkış akışında CSRF servisine dokunulmamalı; dokunulursa test patlar.</summary>
    private sealed class PatlayanAntiforgery : Microsoft.AspNetCore.Antiforgery.IAntiforgery
    {
        public Microsoft.AspNetCore.Antiforgery.AntiforgeryTokenSet GetAndStoreTokens(HttpContext httpContext) => throw new NotSupportedException();

        public Microsoft.AspNetCore.Antiforgery.AntiforgeryTokenSet GetTokens(HttpContext httpContext) => throw new NotSupportedException();

        public Task<bool> IsRequestValidAsync(HttpContext httpContext) => throw new NotSupportedException();

        public void SetCookieTokenAndHeader(HttpContext httpContext) => throw new NotSupportedException();

        public Task ValidateRequestAsync(HttpContext httpContext) => throw new NotSupportedException();
    }

    /// <summary>Çıkış akışı kimlik servisine hiç uğramamalı (çerezi ara katman siler).</summary>
    private sealed class PatlayanKimlikServisi : Core.Interfaces.IAuthService
    {
        public Task<Core.Dtos.Auth.LoginResultDto> LoginAsync(Core.Dtos.Auth.LoginRequest request, string? ipAddress, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Core.Dtos.Auth.CurrentSessionDto> GetCurrentSessionAsync(ClaimsPrincipal principal, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Core.Dtos.Auth.CurrentUserDto> GetUserByIdAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Core.Dtos.Auth.TwoFactorSetupDto> BeginTwoFactorSetupAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Core.Dtos.Auth.TwoFactorConfirmDto> ConfirmTwoFactorSetupAsync(Guid userId, string code, string? ipAddress, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Core.Dtos.Auth.CurrentUserDto> DisableTwoFactorAsync(Guid userId, string password, string? ipAddress, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Core.Dtos.Auth.RecoveryCodesDto> RegenerateRecoveryCodesAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task RequestPasswordResetAsync(string email, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task ResetPasswordAsync(string token, string newPassword, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Core.Dtos.Auth.ChangePasswordResultDto> ChangePasswordAsync(Guid userId, Core.Dtos.Auth.ChangePasswordRequest request, string? ipAddress, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Core.Dtos.Auth.SetUserPasswordResultDto> SetPasswordForUserAsync(Guid targetUserId, Core.Dtos.Auth.SetUserPasswordRequest request, Guid actorUserId, string? ipAddress, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Core.Dtos.Auth.SsoLinkDto> CreateSsoLinkAsync(Guid targetUserId, Guid actorUserId, string? requestHost, string? ipAddress, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Core.Dtos.Auth.CurrentUserDto?> TryRedeemSsoTokenAsync(string token, string? ipAddress, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class CikisiKaydedenServis(Action cikisYapildi) : IAuthenticationService
    {
        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme) => throw new NotSupportedException();

        public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => throw new NotSupportedException();

        public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => throw new NotSupportedException();

        public Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal principal, AuthenticationProperties? properties) => throw new NotSupportedException();

        public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties)
        {
            cikisYapildi();
            return Task.CompletedTask;
        }
    }
}
