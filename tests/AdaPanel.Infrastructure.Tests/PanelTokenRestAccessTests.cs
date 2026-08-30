using System.Security.Claims;
using System.Text.Encodings.Web;
using AdaPanel.Api.Security;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Ai;
using AdaPanel.Core.Entities;
using AdaPanel.Core.Enums;
using AdaPanel.Core.Interfaces;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Security;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// KISISEL API ANAHTARI REST API'DE CALISIYOR MU?
/// <para>
/// BULUNAN HATA: <c>PanelToken</c> semasi YALNIZCA <c>/mcp</c> ucuna bagliydi. <c>/api/v1/**</c>
/// controller'larinin hicbiri <c>AuthenticationSchemes</c> belirtmedigi icin varsayilan CEREZ semasi
/// kullaniliyor ve <c>Authorization: Bearer adap_…</c> basligi HIC OKUNMUYORDU. Yani panelin
/// "kisisel API anahtari" ozelligi fiilen yalnizca yapay zeka istemcisi icindi; WHMCS modulu bu
/// yuzden cerez oturumu acmak zorunda kalmis ve dakikada 5 istekle sinirli "login" politikasina
/// takilmisti.
/// </para>
/// <para>
/// KAPSAM NOTU: burada tam bir HTTP boru hatti ayaga kaldirilmiyor (Program.cs tepsi/veritabani
/// onyuklemesi yapan ust duzey bir programdir). Bunun yerine REST cagrisini mumkun kilan ZINCIRIN
/// her halkasi ayri ayri olculuyor -- sema secimi, anahtar dogrulama, CSRF karari, yetenek karari --
/// ve halkalarin Program.cs'te GERCEKTEN kablolandigi kaynak uzerinden dogrulaniyor. Kablolama
/// testleri olmasaydi butun bu kurallar dogru ama BAGLANMAMIS olabilirdi.
/// </para>
/// </summary>
public sealed class PanelTokenRestAccessTests
{
    // --- 1) Sema secimi: hangi istek hangi kimlik yoluna gider ---

    [Fact]
    public void Anahtar_tasiyan_istek_TOKEN_semasina_gider()
    {
        Assert.Equal(
            PanelTokenAuthenticationHandler.SchemeName,
            PanelAuthentication.SelectScheme(Istek("Bearer adap_abc123")));
    }

    [Theory]
    [InlineData(null)]                 // arayuz: Authorization basligi HIC gondermez
    [InlineData("Bearer eyJhbGci")]    // baska bir bearer turu bu semayi sahiplenmemeli
    [InlineData("Basic YWRtaW46eA==")]
    public void Anahtar_tasimayan_istek_CEREZ_semasinda_kalir(string? authorization)
    {
        // GERILEME KORUMASI: panel arayuzu cerez kullaniyor. Secici burada yanilsaydi arayuzun
        // TAMAMI 401 alirdi.
        Assert.Equal(
            CookieAuthenticationDefaults.AuthenticationScheme,
            PanelAuthentication.SelectScheme(Istek(authorization)));
    }

    // --- 2) Anahtarin kendisi: gecerli / iptal / suresi dolmus / sahibi pasif ---

    [Fact]
    public async Task Gecerli_anahtar_CEREZLE_AYNI_talepleri_uretir()
    {
        await using var duzenek = await Duzenek.OlusturAsync();
        var anahtar = await duzenek.AnahtarUretAsync();

        var sonuc = await duzenek.DogrulaAsync(anahtar);

        Assert.True(sonuc.Succeeded);
        // Mevcut yetkilendirme, kapsam denetimi ve denetim kaydi bu taleplere bakiyor; biri
        // eksik olsaydi anahtarla gelen istek yetkilendirmede sessizce reddedilirdi.
        Assert.Equal(duzenek.Kullanici.Id.ToString(), sonuc.Principal!.FindFirst(ClaimTypes.NameIdentifier)?.Value);
        Assert.Equal(duzenek.Kullanici.UserName, sonuc.Principal.Identity?.Name);
        Assert.Equal("Administrator", sonuc.Principal.FindFirst(ClaimTypes.Role)?.Value);
        Assert.True(PanelAuthentication.IsTokenAuthenticated(sonuc.Principal));
    }

    [Fact]
    public async Task Iptal_edilmis_anahtar_REDDEDILIR()
    {
        await using var duzenek = await Duzenek.OlusturAsync();
        var anahtar = await duzenek.AnahtarUretAsync();
        await duzenek.Anahtarlar.RevokeAllForUserAsync(duzenek.Kullanici.Id);

        var sonuc = await duzenek.DogrulaAsync(anahtar);

        Assert.False(sonuc.Succeeded);
        Assert.Null(sonuc.Principal);
    }

    [Fact]
    public async Task Uydurma_anahtar_REDDEDILIR()
    {
        await using var duzenek = await Duzenek.OlusturAsync();

        var sonuc = await duzenek.DogrulaAsync("adap_" + new string('0', 64));

        Assert.False(sonuc.Succeeded);
    }

    [Fact]
    public async Task Pasif_kullanicinin_anahtari_REDDEDILIR()
    {
        // Cerez tarafinda ayni denetim CookieSessionValidator'da var; iki kimlik yolu ayni
        // gercege bakmali, aksi halde hesabi kapatmak yalnizca yarim isi yapardi.
        await using var duzenek = await Duzenek.OlusturAsync();
        var anahtar = await duzenek.AnahtarUretAsync();

        duzenek.Kullanici.IsActive = false;
        await duzenek.DbContext.SaveChangesAsync();

        var sonuc = await duzenek.DogrulaAsync(anahtar);

        Assert.False(sonuc.Succeeded);
        Assert.Equal("apitoken.owner_inactive", sonuc.Failure?.Message);
    }

    [Fact]
    public async Task Baslik_yoksa_sema_SONUC_YOK_der()
    {
        // "Basarisiz" degil "sonuc yok" olmali: ayni istek cerezle dogrulanabilir ve buradan hata
        // donmek panel arayuzunu kirardi.
        await using var duzenek = await Duzenek.OlusturAsync();

        var sonuc = await duzenek.DogrulaAsync(null);

        Assert.False(sonuc.Succeeded);
        Assert.Null(sonuc.Failure);
        Assert.True(sonuc.None);
    }

    [Fact]
    public async Task Kimliksiz_istekte_401_JSON_ZARFI_doner()
    {
        // Varsayilan davranis govdesiz 401'dir; dis entegrasyon "bos 401" ile "sunucu coktu"yu
        // ayirt edemez. Cerez semasi zaten JSON yaziyor, anahtar semasi da yazmali.
        await using var duzenek = await Duzenek.OlusturAsync();

        var govde = await duzenek.MeydanOkuAsync();

        Assert.Contains("auth.unauthorized", govde, StringComparison.Ordinal);
    }

    // --- 3) CSRF: cerezde ISTENIR, anahtarda ISTENMEZ ---

    [Fact]
    public async Task Cerezle_gelen_YAZMA_istegi_CSRF_dogrulamasindan_gecer()
    {
        var antiforgery = new SayanAntiforgery();

        await CsrfCalistirAsync(antiforgery, "POST", CerezKimligi());

        Assert.Equal(1, antiforgery.DogrulamaSayisi);
    }

    [Fact]
    public async Task Anahtarla_gelen_YAZMA_istegi_CSRF_ISTEMEZ()
    {
        // CSRF korumasi CEREZE karsidir: Authorization basligi tarayici tarafindan kendiliginden
        // gonderilmez (capraz kaynakta ayrica CORS on-kontrolu ister ve panel CORS acmaz).
        // Anahtarli istekte antiforgery koruma degil yalnizca engeldir.
        var antiforgery = new SayanAntiforgery();

        await CsrfCalistirAsync(antiforgery, "POST", AnahtarKimligi());

        Assert.Equal(0, antiforgery.DogrulamaSayisi);
    }

    [Fact]
    public async Task Okuma_istegi_CSRF_ISTEMEZ()
    {
        var antiforgery = new SayanAntiforgery();

        await CsrfCalistirAsync(antiforgery, "GET", CerezKimligi());

        Assert.Equal(0, antiforgery.DogrulamaSayisi);
    }

    [Fact]
    public async Task Ucun_kendi_muafiyeti_KORUNUR()
    {
        // DbToolsProxyController (phpMyAdmin/Adminer POST'lari) ve GitController kancasi
        // [IgnoreAntiforgeryToken] tasiyor; yeni filtre bunu unutsaydi o araclar bozulurdu.
        var antiforgery = new SayanAntiforgery();

        await CsrfCalistirAsync(antiforgery, "POST", CerezKimligi(), muafUc: true);

        Assert.Equal(0, antiforgery.DogrulamaSayisi);
    }

    [Fact]
    public async Task Gecersiz_CSRF_token_yukari_birakilir()
    {
        // ApiExceptionHandlingMiddleware bunu yakalayip cevirisi olan "security.invalid_csrf_token"
        // ile 400 donduruyor; onceki oznitelik GOVDESIZ bir 400 uretiyordu.
        var antiforgery = new SayanAntiforgery { Patlat = true };

        await Assert.ThrowsAsync<AntiforgeryValidationException>(
            () => CsrfCalistirAsync(antiforgery, "POST", CerezKimligi()));
    }

    // --- 4) Anahtarin YETENEKLERI REST'te de baglayici ---

    [Fact]
    public void Salt_okur_anahtar_OKUYABILIR()
    {
        PanelTokenCapabilities.EnsureAllowed(AnahtarKimligi(), "GET", "/api/v1/sites");
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public void Salt_okur_anahtar_YAZAMAZ(string yontem)
    {
        // Bu denetim olmasaydi "yikici islemlere izin vermeyen" bir anahtar REST uzerinden silme
        // yapabilir, yani MCP'de kurulan yetenek modeli sessizce anlamsizlasirdi.
        var hata = Assert.Throws<AppException>(
            () => PanelTokenCapabilities.EnsureAllowed(AnahtarKimligi(), yontem, "/api/v1/sites/ornek"));

        Assert.Equal(403, hata.HttpStatusCode);
        Assert.Equal(PanelTokenCapabilities.ReadOnlyReason, hata.Details);
    }

    [Fact]
    public void Yikici_yetkili_anahtar_YAZABILIR()
    {
        PanelTokenCapabilities.EnsureAllowed(AnahtarKimligi(yikici: true), "POST", "/api/v1/sites");
    }

    [Fact]
    public void Terminal_yetkisi_olmayan_anahtar_KOMUT_CALISTIRAMAZ()
    {
        var hata = Assert.Throws<AppException>(
            () => PanelTokenCapabilities.EnsureAllowed(
                AnahtarKimligi(yikici: true),
                "POST",
                PanelTokenCapabilities.TerminalPathPrefix + "/execute"));

        Assert.Equal(PanelTokenCapabilities.TerminalReason, hata.Details);
    }

    [Fact]
    public void Terminal_yetkili_anahtar_KOMUT_CALISTIRABILIR()
    {
        PanelTokenCapabilities.EnsureAllowed(
            AnahtarKimligi(yikici: true, terminal: true),
            "POST",
            PanelTokenCapabilities.TerminalPathPrefix + "/execute");
    }

    [Fact]
    public void Cerezle_gelen_kullanici_yetenek_kuralindan_ETKILENMEZ()
    {
        // GERILEME KORUMASI: panel arayuzu cerez kullaniyor ve yetenek talepleri yalniz anahtarda
        // var. Kural cerezi de baglasaydi arayuzden hicbir yazma islemi yapilamazdi.
        PanelTokenCapabilities.EnsureAllowed(CerezKimligi(), "DELETE", "/api/v1/sites/ornek");
        PanelTokenCapabilities.EnsureAllowed(CerezKimligi(), "POST", PanelTokenCapabilities.TerminalPathPrefix + "/execute");
    }

    [Fact]
    public void Terminal_yolu_TerminalController_ile_ayni()
    {
        // Yol karsilastirmasi kirilgan: rota degistiginde kural sessizce olurdu.
        var kaynak = KaynakOku("src", "AdaPanel.Api", "Controllers", "TerminalController.cs");
        Assert.Contains($"[Route(\"{PanelTokenCapabilities.TerminalPathPrefix.TrimStart('/')}\")]", kaynak, StringComparison.Ordinal);
    }

    // --- 5) Kablolama: kurallar Program.cs'te GERCEKTEN bagli mi ---

    [Fact]
    public void Varsayilan_sema_YONLENDIRICI_olarak_kayitli()
    {
        var kaynak = KaynakOku("src", "AdaPanel.Api", "Program.cs");

        Assert.Contains("AddAuthentication(PanelAuthentication.SchemeName)", kaynak, StringComparison.Ordinal);
        Assert.Contains("ForwardDefaultSelector = PanelAuthentication.SelectScheme", kaynak, StringComparison.Ordinal);
        // Anahtar semasi hala kayitli olmali; yonlendirici onu bulamazsa istek sessizce 401 olurdu.
        Assert.Contains("PanelTokenAuthenticationHandler.SchemeName", kaynak, StringComparison.Ordinal);
    }

    [Fact]
    public void CSRF_ve_yetenek_filtreleri_KURESEL_olarak_kayitli()
    {
        var kaynak = KaynakOku("src", "AdaPanel.Api", "Program.cs");

        Assert.Contains("options.Filters.Add<PanelAntiforgeryFilter>()", kaynak, StringComparison.Ordinal);
        Assert.Contains("options.Filters.Add<PanelTokenCapabilityFilter>()", kaynak, StringComparison.Ordinal);
        // Eski kosulsuz oznitelik KALMAMALI: kalsaydi anahtarli istek yine CSRF token'i isterdi.
        Assert.DoesNotContain("new AutoValidateAntiforgeryTokenAttribute()", kaynak, StringComparison.Ordinal);
    }

    /// <summary>
    /// Anahtarli isteklerin tavani duruyor mu.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>BU TESTIN ILK HALI BIR ACIGI "BEKLENEN DAVRANIS" DIYE CIVILEMISTI.</b> Son satiri
    /// <c>RateLimitPartition.GetNoLimiter("cookie")</c> ariyordu, yani "cerez yolunda hicbir
    /// tavan olmasin" kuralini KORUYORDU. Sonucu: 66 denetleyicinin ~64'u ve kimliksiz uclarin
    /// besi — ikisi POSTA GONDEREN — hicbir sinir gormuyordu ve bu durumu bozmak testi
    /// kirmiziya dondururdu.
    /// </para>
    /// <para>
    /// Gerekce o gun makuldu (panelin tek sayfasi onlarca istek atar), ama karsiligi OLCULMEMISTI.
    /// Olculdugunde tek sekmenin en kotu hali 71 istek/dk cikti — yani "cok istek atiyor" dogruydu
    /// ama "bu yuzden tavan konamaz" yanlisti. Sinirlar artik o olcume gore secildi ve yoklama
    /// ayri bir kovaya alindi (bkz. <c>PanelRateLimitTests</c>).
    /// </para>
    /// <para>
    /// Bu test artik anahtar yolunun BOZULMADIGINI olcuyor: cerez tavani eklenirken anahtar
    /// bolumlemesinin kaybolmadigini. Cerez yolunun kendi kanitlari <c>PanelRateLimitTests</c>'te,
    /// davranis olarak (kaynak metniyle degil).
    /// </para>
    /// </remarks>
    [Fact]
    public void Anahtarli_isteklerin_hiz_siniri_var()
    {
        var kaynak = KaynakOku("src", "AdaPanel.Api", "Security", "PanelRateLimits.cs");

        Assert.Contains("PanelAuthentication.CarriesToken(context.Request)", kaynak, StringComparison.Ordinal);
        Assert.Contains("Dakikalik(TokenPermitPerMinute)", kaynak, StringComparison.Ordinal);

        // Kuresel sinirlayici Program.cs'te GERCEKTEN bagli olmali; yalniz tanimli olmasi,
        // "kural dogru ama baglanmamis" sinifina girerdi.
        var program = KaynakOku("src", "AdaPanel.Api", "Program.cs");
        Assert.Contains("AddRateLimiter(PanelRateLimits.Configure(", program, StringComparison.Ordinal);

        // ESKI DAVRANIS GERI GELEMEZ: cerez yolu "sinirsiz" isaretlenemez.
        //
        // YASAK KODUN SEKLINE KONUYOR, DIZGEYE DEGIL -- ve bunu testin ilk kosusu yazdirdi:
        // eski davranisin NEDEN kaldirildigini anlatan yorumun kendisi "GetNoLimiter" kelimesini
        // geciriyor ve duz bir arama HAKLI olarak kirmizi yandi. Ayni ders
        // PanelSecurityHeaderTests'te de yazili: gerekce serbestce yazilabilmeli, yasak yalniz
        // CAGRIYI kapsamali. Yorum satiri "//" ile baslar; kod satiri baslamaz.
        foreach (var (ad, metin) in new[] { ("PanelRateLimits.cs", kaynak), ("Program.cs", program) })
        {
            var canliCagri = metin
                .Split('\n')
                .Select(satir => satir.Trim())
                .Any(satir => !satir.StartsWith("//", StringComparison.Ordinal)
                              && !satir.StartsWith("*", StringComparison.Ordinal)
                              && satir.Contains("GetNoLimiter(", StringComparison.Ordinal));

            Assert.False(canliCagri, $"{ad}: cerez yolu yeniden 'sinirsiz' isaretlenmis.");
        }
    }

    [Fact]
    public void Anahtar_bolumleme_anahtari_anahtarin_KENDISINI_tasimaz()
    {
        var context = Istek("Bearer adap_gizli_deger_123456");

        var bolum = PanelAuthentication.RateLimitPartitionKey(context);

        Assert.StartsWith("token:", bolum, StringComparison.Ordinal);
        Assert.DoesNotContain("gizli", bolum, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Kimlik_bilgisi_yoneten_controller_lar_YALNIZ_CEREZ_kabul_eder()
    {
        // Yetki yukseltmesi: salt-okur bir anahtar buradan kendine tam yetkili YENI bir anahtar
        // basabilseydi ya da parolayi degistirebilseydi, anahtarin kisiti kagit uzerinde kalirdi.
        foreach (var dosya in new[] { "AuthController.cs", "ApiTokensController.cs" })
        {
            var kaynak = KaynakOku("src", "AdaPanel.Api", "Controllers", dosya);
            Assert.Contains(
                "[Authorize(AuthenticationSchemes = CookieAuthenticationDefaults.AuthenticationScheme)]",
                kaynak,
                StringComparison.Ordinal);
        }
    }

    // --- duzenek ---

    private static DefaultHttpContext Istek(string? authorization)
    {
        var context = new DefaultHttpContext();
        if (authorization is not null)
        {
            context.Request.Headers.Authorization = authorization;
        }

        return context;
    }

    private static ClaimsPrincipal AnahtarKimligi(bool yikici = false, bool terminal = false) =>
        new(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                new Claim(ClaimTypes.Role, "Administrator"),
                new Claim(PanelTokenAuthenticationHandler.AllowDestructiveClaim, yikici ? "true" : "false"),
                new Claim(PanelTokenAuthenticationHandler.AllowTerminalClaim, terminal ? "true" : "false")
            ],
            PanelTokenAuthenticationHandler.SchemeName));

    private static ClaimsPrincipal CerezKimligi() =>
        new(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()), new Claim(ClaimTypes.Role, "Administrator")],
            CookieAuthenticationDefaults.AuthenticationScheme));

    private static Task CsrfCalistirAsync(
        IAntiforgery antiforgery,
        string yontem,
        ClaimsPrincipal kullanici,
        bool muafUc = false)
    {
        var http = new DefaultHttpContext { User = kullanici };
        http.Request.Method = yontem;

        var metaVeri = muafUc
            ? new EndpointMetadataCollection(new IgnoreAntiforgeryTokenAttribute())
            : EndpointMetadataCollection.Empty;
        http.SetEndpoint(new Endpoint(_ => Task.CompletedTask, metaVeri, "test"));

        var baglam = new AuthorizationFilterContext(
            new ActionContext(http, new RouteData(), new ActionDescriptor()),
            []);

        return new PanelAntiforgeryFilter(antiforgery).OnAuthorizationAsync(baglam);
    }

    private static string KaynakOku(params string[] parcalar)
    {
        var kok = AppContext.BaseDirectory;
        while (kok is not null && !File.Exists(Path.Combine(kok, "AdaPanel.sln")))
        {
            kok = Path.GetDirectoryName(kok);
        }

        Assert.NotNull(kok);
        return File.ReadAllText(Path.Combine([kok!, .. parcalar]));
    }

    private sealed class Duzenek : IAsyncDisposable
    {
        private readonly SqliteConnection baglanti;

        private Duzenek(SqliteConnection baglanti, AdaPanelDbContext dbContext, PanelUser kullanici)
        {
            this.baglanti = baglanti;
            DbContext = dbContext;
            Kullanici = kullanici;
            Anahtarlar = new ApiTokenService(dbContext);
        }

        public AdaPanelDbContext DbContext { get; }

        public PanelUser Kullanici { get; }

        /// <summary>GERCEK servis kullaniliyor: sahte bir dogrulayici, kuralin kendisini degil
        /// taklidini olcerdi (iptal/sure/karma karsilastirmasi tam da burada yasiyor).</summary>
        public IApiTokenService Anahtarlar { get; }

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
                UserName = "entegrasyon",
                Email = "entegrasyon@ornek.test",
                Role = UserRole.Administrator,
                PasswordHash = "KARMA",
                IsActive = true
            };

            dbContext.Users.Add(kullanici);
            await dbContext.SaveChangesAsync();

            return new Duzenek(baglanti, dbContext, kullanici);
        }

        public async Task<string> AnahtarUretAsync(bool yikici = false, bool terminal = false)
        {
            var uretilen = await Anahtarlar.CreateAsync(
                Kullanici.Id,
                new CreateApiTokenRequest { Name = "wc-modulu", AllowDestructive = yikici, AllowTerminal = terminal });

            return uretilen.Value;
        }

        public async Task<AuthenticateResult> DogrulaAsync(string? anahtar)
        {
            var (handler, _) = await KurAsync(anahtar);
            return await handler.AuthenticateAsync();
        }

        public async Task<string> MeydanOkuAsync()
        {
            var (handler, http) = await KurAsync(null);
            var govde = new MemoryStream();
            http.Response.Body = govde;

            await handler.ChallengeAsync(new AuthenticationProperties());

            govde.Position = 0;
            return await new StreamReader(govde).ReadToEndAsync();
        }

        private async Task<(PanelTokenAuthenticationHandler Handler, DefaultHttpContext Http)> KurAsync(string? anahtar)
        {
            var handler = new PanelTokenAuthenticationHandler(
                new SabitSecenekler(),
                NullLoggerFactory.Instance,
                UrlEncoder.Default,
                Anahtarlar,
                DbContext);

            var servisler = new ServiceCollection();
            servisler.AddOptions();
            servisler.AddLogging();
            var http = new DefaultHttpContext { RequestServices = servisler.BuildServiceProvider() };
            if (anahtar is not null)
            {
                http.Request.Headers.Authorization = "Bearer " + anahtar;
            }

            await handler.InitializeAsync(
                new AuthenticationScheme(
                    PanelTokenAuthenticationHandler.SchemeName,
                    displayName: null,
                    handlerType: typeof(PanelTokenAuthenticationHandler)),
                http);

            return (handler, http);
        }

        public async ValueTask DisposeAsync()
        {
            await DbContext.DisposeAsync();
            await baglanti.DisposeAsync();
        }
    }

    private sealed class SabitSecenekler : IOptionsMonitor<AuthenticationSchemeOptions>
    {
        public AuthenticationSchemeOptions CurrentValue { get; } = new();

        public AuthenticationSchemeOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<AuthenticationSchemeOptions, string?> listener) => null;
    }

    /// <summary>CSRF dogrulamasinin CAGRILIP cagrilmadigini olcer — kararin konusu tam olarak bu.</summary>
    private sealed class SayanAntiforgery : IAntiforgery
    {
        public int DogrulamaSayisi { get; private set; }

        public bool Patlat { get; init; }

        public Task ValidateRequestAsync(HttpContext httpContext)
        {
            DogrulamaSayisi++;
            return Patlat
                ? throw new AntiforgeryValidationException("gecersiz")
                : Task.CompletedTask;
        }

        public AntiforgeryTokenSet GetAndStoreTokens(HttpContext httpContext) => throw new NotSupportedException();
        public AntiforgeryTokenSet GetTokens(HttpContext httpContext) => throw new NotSupportedException();
        public Task<bool> IsRequestValidAsync(HttpContext httpContext) => throw new NotSupportedException();
        public void SetCookieTokenAndHeader(HttpContext httpContext) => throw new NotSupportedException();
    }
}
