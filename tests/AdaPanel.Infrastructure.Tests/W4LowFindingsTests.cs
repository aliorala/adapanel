using System.Net;
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
using AdaPanel.Infrastructure.Services;
using AdaPanel.Infrastructure.Support;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Dalga 4 "düşük bulgular": API anahtarı IP izin listesi + salt okunur kapsam, oturum hız sınırı
/// bölümünün sahte çerezle çoğaltılamaması, brute-force IPv6 /64 + loopback izleme.
/// </summary>
public sealed class W4LowFindingsTests
{
    // ------------------------------------------------------------------ IP izin listesi

    [Theory]
    [InlineData("203.0.113.5", "203.0.113.5", true)]
    [InlineData("203.0.113.5", "203.0.113.6", false)]
    [InlineData("198.51.100.0/24", "198.51.100.77", true)]
    [InlineData("198.51.100.0/24", "198.51.101.1", false)]
    [InlineData("2001:db8:1::/48", "2001:db8:1:ffff::9", true)]
    [InlineData("2001:db8:1::/48", "2001:db8:2::1", false)]
    [InlineData("203.0.113.5", "::ffff:203.0.113.5", true)]   // IPv4-eşlemeli adres
    [InlineData("10.0.0.1, 10.0.0.2", "10.0.0.2", true)]
    [InlineData("", "8.8.8.8", true)]                          // liste boş = kısıt yok
    public void Izin_listesi_eslesmesi(string liste, string uzak, bool beklenen)
    {
        var normal = ApiTokenIpAllowList.Normalize(liste);
        Assert.NotNull(normal);
        Assert.Equal(beklenen, ApiTokenIpAllowList.IsAllowed(normal, IPAddress.Parse(uzak)));
    }

    [Fact]
    public void Dolu_liste_adres_bilinmiyorsa_reddeder()
    {
        Assert.False(ApiTokenIpAllowList.IsAllowed("203.0.113.5", null));
    }

    [Theory]
    [InlineData("not-an-ip")]
    [InlineData("10.0.0.0/33")]
    [InlineData("2001:db8::/129")]
    [InlineData("10.0.0.1/abc")]
    public void Gecersiz_girdi_reddedilir(string girdi) => Assert.Null(ApiTokenIpAllowList.Normalize(girdi));

    [Fact]
    public void Normallestirme_agi_tabana_indirir_ve_tekrari_atar()
    {
        Assert.Equal("10.0.0.0/8,203.0.113.5", ApiTokenIpAllowList.Normalize("10.1.2.3/8\n203.0.113.5; 203.0.113.5"));
    }

    [Fact]
    public async Task Anahtar_listede_olmayan_IPden_reddedilir_listedekinden_kabul_edilir()
    {
        await using var d = await Duzenek.OlusturAsync();
        var deger = await d.UretAsync(ips: "203.0.113.0/24");

        Assert.True((await d.DogrulaAsync(deger, "203.0.113.9")).Succeeded);
        var red = await d.DogrulaAsync(deger, "198.51.100.1");
        Assert.False(red.Succeeded);
        Assert.Equal("apitoken.ip_not_allowed", red.Failure?.Message);
    }

    [Fact]
    public async Task Kisitsiz_mevcut_anahtar_her_IPden_calisir()
    {
        await using var d = await Duzenek.OlusturAsync();
        var deger = await d.UretAsync();
        Assert.True((await d.DogrulaAsync(deger, "198.51.100.1")).Succeeded);
    }

    [Fact]
    public async Task Gecersiz_IP_listesi_olusturmada_400()
    {
        await using var d = await Duzenek.OlusturAsync();
        var hata = await Assert.ThrowsAsync<AppException>(() => d.UretAsync(ips: "bu-bir-ip-degil"));
        Assert.Equal("apitoken.invalid_ip_list", hata.MessageKey);
    }

    // ------------------------------------------------------------------ salt okunur kapsam

    [Fact]
    public async Task Salt_okunur_anahtar_yikici_ve_terminal_istese_bile_kapatilir()
    {
        await using var d = await Duzenek.OlusturAsync();
        var deger = await d.UretAsync(readOnly: true, yikici: true, terminal: true);
        var sonuc = await d.DogrulaAsync(deger, "198.51.100.1");

        Assert.Equal("false", sonuc.Principal!.FindFirst(PanelTokenAuthenticationHandler.AllowDestructiveClaim)!.Value);
        Assert.Equal("false", sonuc.Principal.FindFirst(PanelTokenAuthenticationHandler.AllowTerminalClaim)!.Value);
        Assert.Equal("true", sonuc.Principal.FindFirst(PanelTokenAuthenticationHandler.ReadOnlyScopeClaim)!.Value);
    }

    [Theory]
    [InlineData("GET", false)]
    [InlineData("HEAD", false)]
    [InlineData("POST", true)]
    [InlineData("PUT", true)]
    [InlineData("DELETE", true)]
    public void Salt_okunur_kapsam_GET_disini_403_verir_yikici_acik_olsa_bile(string yontem, bool reddedilir)
    {
        var kimlik = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                new Claim(ClaimTypes.Role, "Administrator"),
                new Claim(PanelTokenAuthenticationHandler.AllowDestructiveClaim, "true"),
                new Claim(PanelTokenAuthenticationHandler.AllowTerminalClaim, "false"),
                new Claim(PanelTokenAuthenticationHandler.ReadOnlyScopeClaim, "true")
            ],
            PanelTokenAuthenticationHandler.SchemeName));

        if (reddedilir)
        {
            var hata = Assert.Throws<AppException>(() => PanelTokenCapabilities.EnsureAllowed(kimlik, yontem, "/api/v1/sites/x"));
            Assert.Equal(403, hata.HttpStatusCode);
            Assert.Equal(PanelTokenCapabilities.ReadOnlyScopeReason, hata.Details);
        }
        else
        {
            PanelTokenCapabilities.EnsureAllowed(kimlik, yontem, "/api/v1/sites");
        }
    }

    [Fact]
    public void Kapsamsiz_mevcut_yikici_anahtar_tam_yetkili_kalir()
    {
        var kimlik = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                new Claim(PanelTokenAuthenticationHandler.AllowDestructiveClaim, "true"),
                new Claim(PanelTokenAuthenticationHandler.AllowTerminalClaim, "false")
            ],
            PanelTokenAuthenticationHandler.SchemeName));

        PanelTokenCapabilities.EnsureAllowed(kimlik, "POST", "/api/v1/sites");
    }

    // ------------------------------------------------------------------ hız sınırı bölümü (madde 3)

    [Fact]
    public void Rastgele_cerez_oturum_bolumu_acmaz_IPye_duser()
    {
        var a = Istek("203.0.113.4", "kesinlikle-gecerli-bir-bilet-degil-1");
        var b = Istek("203.0.113.4", "kesinlikle-gecerli-bir-bilet-degil-2");

        Assert.Equal("ip:203.0.113.4", PanelRateLimits.SessionPartitionKey(a, "AdaPanel.Auth"));
        Assert.Equal(PanelRateLimits.SessionPartitionKey(a, "AdaPanel.Auth"), PanelRateLimits.SessionPartitionKey(b, "AdaPanel.Auth"));
    }

    [Fact]
    public void Baska_anahtarla_uretilmis_bilet_oturum_sayilmaz()
    {
        var yabanci = PanelRateLimitTests.GercekCerez("x", PanelRateLimitTests.YeniCerezHizmetleri());
        var context = Istek("203.0.113.4", yabanci);

        Assert.StartsWith("ip:", PanelRateLimits.SessionPartitionKey(context, "AdaPanel.Auth"), StringComparison.Ordinal);
    }

    [Fact]
    public void Gecerli_bilet_oturum_bolumu_verir()
    {
        var context = Istek("203.0.113.4", PanelRateLimitTests.GercekCerez("kullanici-1"));
        Assert.StartsWith("session:", PanelRateLimits.SessionPartitionKey(context, "AdaPanel.Auth"), StringComparison.Ordinal);
    }

    private static DefaultHttpContext Istek(string ip, string cerez)
    {
        var context = new DefaultHttpContext { RequestServices = PanelRateLimitTests.CerezHizmetleri };
        context.Connection.RemoteIpAddress = IPAddress.Parse(ip);
        context.Request.Headers.Cookie = "AdaPanel.Auth=" + cerez;
        return context;
    }

    // ------------------------------------------------------------------ brute-force (madde 8)

    [Theory]
    [InlineData("203.0.113.9", "203.0.113.9")]
    [InlineData("::ffff:203.0.113.9", "203.0.113.9")]
    [InlineData("2001:db8:1:2:aaaa:bbbb:cccc:dddd", "2001:db8:1:2::/64")]
    [InlineData("2001:db8:1:2::/64", "2001:db8:1:2::/64")]
    [InlineData("127.0.0.1", "127.0.0.1")]
    [InlineData("::1", "::1")]
    public void Brute_force_anahtari_IPv6da_slash64dur_loopback_artik_izlenir(string girdi, string beklenen)
    {
        Assert.Equal(beklenen, BruteForceProtectionService.Normalize(girdi));
    }

    [Fact]
    public async Task IPv6_yasagi_ayni_64_icindeki_baska_adresi_de_kapsar()
    {
        using var k = new BfKurulum();
        k.TohumYasak("2001:db8:1:2::/64");
        var servis = k.Servis();

        Assert.True(await servis.IsBannedAsync("2001:db8:1:2:1111:2222:3333:4444"));
        Assert.True(await servis.IsBannedAsync("2001:db8:1:2:ffff::1"));
        Assert.False(await servis.IsBannedAsync("2001:db8:1:3::1"));
    }

    [Fact]
    public async Task Loopback_basarisiz_denemeler_artik_sayilir_ve_kisa_sureli_yasak_dogar()
    {
        using var k = new BfKurulum();
        var servis = k.Servis();

        for (var i = 0; i < 10; i++)
        {
            await servis.RegisterFailedAttemptAsync("127.0.0.1");
        }

        Assert.True(await servis.IsBannedAsync("127.0.0.1"));
        var kalan = (k.OkuYasakBitisi("127.0.0.1") - DateTime.UtcNow).TotalMinutes;
        Assert.InRange(kalan, 0, BruteForceProtectionService.LoopbackMaxBanMinutes + 0.5);
    }

    [Fact]
    public async Task Loopback_beyaz_listedeyse_yasaklanmaz()
    {
        using var k = new BfKurulum();
        k.TohumBeyazListe("127.0.0.1");
        var servis = k.Servis();

        for (var i = 0; i < 15; i++)
        {
            await servis.RegisterFailedAttemptAsync("127.0.0.1");
        }

        Assert.False(await servis.IsBannedAsync("127.0.0.1"));
    }

    [Theory]
    [InlineData("127.0.0.1", 0, 5)]
    [InlineData("127.0.0.1", 60, 5)]
    [InlineData("127.0.0.1", 2, 2)]
    [InlineData("203.0.113.9", 60, 60)]
    public void Yasak_suresi_loopbackte_sinirlidir(string anahtar, int dakika, int beklenen)
    {
        var simdi = DateTime.UtcNow;
        Assert.Equal(simdi.AddMinutes(beklenen), BruteForceProtectionService.BanExpiry(anahtar, dakika, simdi));
    }

    // ------------------------------------------------------------------ DÜZELT turu

    private static ClaimsPrincipal SaltOkunurAnahtar() => new(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new Claim(ClaimTypes.Role, "Administrator"),
            new Claim(PanelTokenAuthenticationHandler.AllowDestructiveClaim, "false"),
            new Claim(PanelTokenAuthenticationHandler.ReadOnlyScopeClaim, "true")
        ],
        PanelTokenAuthenticationHandler.SchemeName));

    [Fact]
    public async Task Salt_okunur_anahtar_MCP_yazma_kapisindan_gecemez()
    {
        // site_start gibi "yikici sayilmayan" araclar da dahil: kapi kapsami ILK kontrol eder.
        var hata = await Assert.ThrowsAsync<ModelContextProtocol.McpProtocolException>(
            () => AdaPanel.Api.Mcp.McpCapability.EnsureWriteAllowedAsync(null!, SaltOkunurAnahtar(), "sites", CancellationToken.None));
        Assert.Contains("salt okunur", hata.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Tum_MCP_yazma_araclari_ortak_yazma_kapisindan_gecer()
    {
        var kaynak = File.ReadAllText(Path.Combine(ScriptTestHost.RepoRoot, "src", "AdaPanel.Api", "Mcp", "AdaPanelWriteTools.cs"));
        var araclar = System.Text.RegularExpressions.Regex.Matches(kaynak, @"\[McpServerTool\(").Count;
        var kapilar = System.Text.RegularExpressions.Regex.Matches(kaynak, @"McpCapability\.EnsureWriteAllowedAsync\(").Count;
        Assert.True(araclar >= 4);
        Assert.Equal(araclar, kapilar);
    }

    [Theory]
    [InlineData("TRACE")]
    [InlineData("POST")]
    public void Salt_okunur_kapsamda_TRACE_da_reddedilir(string yontem)
    {
        Assert.Throws<AppException>(() => PanelTokenCapabilities.EnsureAllowed(SaltOkunurAnahtar(), yontem, "/api/v1/sites"));
    }

    [Fact]
    public void Otuz_iki_IPv6_CIDR_girdisi_sinira_sigar()
    {
        var liste = string.Join(',', Enumerable.Range(0, ApiTokenIpAllowList.MaxEntries)
            .Select(i => $"2001:db8:{i:x}:ffff:ffff:ffff:ffff:ffff/127"));
        var normal = ApiTokenIpAllowList.Normalize(liste);
        Assert.NotNull(normal);
        Assert.True(normal!.Length <= ApiTokenIpAllowList.MaxLength);
        Assert.Null(ApiTokenIpAllowList.Normalize(liste + ",10.0.0.1"));
    }

    [Fact]
    public void DbTools_vekili_yalniz_cerez_semasini_kabul_eder()
    {
        var yetki = typeof(AdaPanel.Api.Controllers.DbToolsProxyController)
            .GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), true)
            .Cast<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>().Single();
        Assert.Equal(Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationDefaults.AuthenticationScheme, yetki.AuthenticationSchemes);
    }

    [Theory]
    [InlineData("::ffff:203.0.113.9", "203.0.113.9")]
    [InlineData("2001:db8:1:2:aaaa:bbbb:cccc:dddd", "2001:db8:1:2::/64")]
    public async Task Eski_bicimli_yasak_uygulanir_ve_kaldirilabilir(string eskiAnahtar, string yeniAnahtar)
    {
        using var k = new BfKurulum();
        k.TohumYasak(eskiAnahtar);
        var servis = k.Servis();

        Assert.True(await servis.IsBannedAsync(eskiAnahtar.Replace("aaaa:bbbb:cccc:dddd", "1:2:3:4")));
        var durum = await servis.GetStatusAsync();
        Assert.Equal(yeniAnahtar, Assert.Single(durum.BannedIps).IpAddress);

        await servis.UnbanAsync(eskiAnahtar);
        Assert.Empty((await servis.GetStatusAsync()).BannedIps);
        Assert.False(await servis.IsBannedAsync(eskiAnahtar.Replace("aaaa:bbbb:cccc:dddd", "1:2:3:4")));
    }

    [Fact]
    public async Task Ayni_anahtara_dusen_eski_yasaklardan_en_uzunu_kalir_ve_kurallar_izlenir()
    {
        using var k = new BfKurulum();
        k.TohumIkiYasak();
        var servis = k.Servis();

        var yasaklar = (await servis.GetStatusAsync()).BannedIps;
        var tek = Assert.Single(yasaklar);
        Assert.Null(tek.ExpiresAtUtc); // süresiz olan kazanır
    }

    [Fact]
    public async Task Unban_yasak_bulunamazsa_basari_donmez()
    {
        using var k = new BfKurulum();
        var hata = await Assert.ThrowsAsync<AppException>(() => k.Servis().UnbanAsync("203.0.113.77"));
        Assert.Equal(404, hata.HttpStatusCode);
    }

    // ------------------------------------------------------------------ yardımcılar

    private sealed class BfKurulum : IDisposable
    {
        private readonly string kok = Path.Combine(Path.GetTempPath(), "w4-bf-" + Guid.NewGuid().ToString("N"));

        public BfKurulum() => Directory.CreateDirectory(Path.Combine(kok, "config"));

        private string Yol => Path.Combine(kok, "config", "bruteforce.json");

        public BruteForceProtectionService Servis() => new(
            new JsonFileStore(Microsoft.Extensions.Options.Options.Create(new AdaPanel.Infrastructure.Options.PanelRuntimeOptions { DataRoot = kok })),
            new ConcurrencyLock(),
            NullLogger<BruteForceProtectionService>.Instance);

        public void TohumYasak(string anahtar) => File.WriteAllText(Yol,
            "{\"enabled\":true,\"maxFailedAttempts\":10,\"windowMinutes\":15,\"banMinutes\":60,\"whitelistedIps\":[],\"attempts\":[],\"bans\":[{\"ipAddress\":\""
            + anahtar + "\",\"bannedAtUtc\":\"" + DateTime.UtcNow.ToString("o") + "\",\"expiresAtUtc\":\"" + DateTime.UtcNow.AddHours(1).ToString("o")
            + "\",\"failedAttempts\":10,\"firewallRuleName\":\"x\"}]}");

        public void TohumIkiYasak() => File.WriteAllText(Yol,
            "{\"enabled\":true,\"maxFailedAttempts\":10,\"windowMinutes\":15,\"banMinutes\":60,\"whitelistedIps\":[],\"attempts\":[],\"bans\":["
            + "{\"ipAddress\":\"::ffff:203.0.113.9\",\"bannedAtUtc\":\"" + DateTime.UtcNow.ToString("o") + "\",\"expiresAtUtc\":null,\"failedAttempts\":10,\"firewallRuleName\":\"kural-a\"},"
            + "{\"ipAddress\":\"203.0.113.9\",\"bannedAtUtc\":\"" + DateTime.UtcNow.ToString("o") + "\",\"expiresAtUtc\":\"" + DateTime.UtcNow.AddHours(1).ToString("o") + "\",\"failedAttempts\":10,\"firewallRuleName\":\"kural-b\"}]}");

        public void TohumBeyazListe(string ip) => File.WriteAllText(Yol,
            "{\"enabled\":true,\"maxFailedAttempts\":10,\"windowMinutes\":15,\"banMinutes\":60,\"whitelistedIps\":[\"" + ip + "\"],\"attempts\":[],\"bans\":[]}");

        public DateTime OkuYasakBitisi(string anahtar)
        {
            using var belge = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Yol));
            foreach (var yasak in belge.RootElement.GetProperty("bans").EnumerateArray())
            {
                if (yasak.GetProperty("ipAddress").GetString() == anahtar)
                {
                    return yasak.GetProperty("expiresAtUtc").GetDateTime().ToUniversalTime();
                }
            }

            throw new InvalidOperationException("yasak yok");
        }

        public void Dispose()
        {
            try { Directory.Delete(kok, true); } catch { /* geçici dizin */ }
        }
    }

    private sealed class Duzenek : IAsyncDisposable
    {
        private readonly SqliteConnection baglanti;
        private readonly AdaPanelDbContext dbContext;
        private readonly IApiTokenService servis;
        private readonly Guid kullaniciId = Guid.NewGuid();

        private Duzenek(SqliteConnection baglanti, AdaPanelDbContext dbContext)
        {
            this.baglanti = baglanti;
            this.dbContext = dbContext;
            servis = new ApiTokenService(dbContext);
        }

        public static async Task<Duzenek> OlusturAsync()
        {
            var baglanti = new SqliteConnection("Data Source=:memory:");
            await baglanti.OpenAsync();
            var db = new AdaPanelDbContext(new DbContextOptionsBuilder<AdaPanelDbContext>().UseSqlite(baglanti).Options);
            await db.Database.EnsureCreatedAsync();
            var d = new Duzenek(baglanti, db);
            db.Users.Add(new PanelUser
            {
                Id = d.kullaniciId, UserName = "w4", Email = "w4@ornek.test", Role = UserRole.Administrator,
                PasswordHash = "KARMA", IsActive = true
            });
            await db.SaveChangesAsync();
            return d;
        }

        public async Task<string> UretAsync(string? ips = null, bool readOnly = false, bool yikici = false, bool terminal = false)
        {
            var uretilen = await servis.CreateAsync(kullaniciId, new CreateApiTokenRequest
            {
                Name = "w4-anahtar", AllowedIps = ips, ReadOnly = readOnly, AllowDestructive = yikici, AllowTerminal = terminal
            });
            return uretilen.Value;
        }

        public async Task<AuthenticateResult> DogrulaAsync(string anahtar, string uzakIp)
        {
            var handler = new PanelTokenAuthenticationHandler(
                new Secenekler(), NullLoggerFactory.Instance, UrlEncoder.Default, servis, dbContext);
            var http = new DefaultHttpContext { RequestServices = new ServiceCollection().AddOptions().AddLogging().BuildServiceProvider() };
            http.Connection.RemoteIpAddress = IPAddress.Parse(uzakIp);
            http.Request.Headers.Authorization = "Bearer " + anahtar;
            await handler.InitializeAsync(
                new AuthenticationScheme(PanelTokenAuthenticationHandler.SchemeName, null, typeof(PanelTokenAuthenticationHandler)), http);
            return await handler.AuthenticateAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await dbContext.DisposeAsync();
            await baglanti.DisposeAsync();
        }
    }

    private sealed class Secenekler : IOptionsMonitor<AuthenticationSchemeOptions>
    {
        public AuthenticationSchemeOptions CurrentValue { get; } = new();

        public AuthenticationSchemeOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<AuthenticationSchemeOptions, string?> listener) => null;
    }
}
