using System.Net;
using System.Reflection;
using System.Security.Claims;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.SiteBuilder;
using AdaPanel.Core.Entities;
using AdaPanel.Core.Enums;
using AdaPanel.Core.Interfaces;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Iis;
using AdaPanel.Infrastructure.ReverseProxy;
using AdaPanel.Infrastructure.Services;
using AdaPanel.Infrastructure.SiteBuilder;
using AdaPanel.Infrastructure.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>G6 tur 2: ters vekil rewrite hedefi, WP-CLI yapılandırma/URL, iletişim formu alıcı politikası.</summary>
public sealed class G6Tur2Tests
{
    private static readonly OutboundAddressGuard.OutboundHostResolver GenelDns =
        (host, _) => Task.FromResult(IPAddress.TryParse(host, out var ip) ? new[] { ip } : new[] { IPAddress.Parse("8.8.8.8") });

    private static ReverseProxyService Vekil(bool yonetici, int panelPortu = 5050)
    {
        var baglam = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.Role, yonetici ? "Administrator" : "Reseller")], "test"))
        };
        baglam.Connection.LocalPort = panelPortu;
        return new ReverseProxyService(null!, null!, null!, NullLogger<ReverseProxyService>.Instance, new HttpContextAccessor { HttpContext = baglam })
        {
            DestinationResolver = GenelDns
        };
    }

    // ---------- G9 ikizi + port kara listesi ----------

    [Theory]
    [InlineData("http://169.254.169.254/latest/meta-data")]
    [InlineData("http://10.0.0.5/x")]
    [InlineData("//169.254.169.254/x")]
    [InlineData("http://127.0.0.1:5050/api")]      // panelin kendi portu
    [InlineData("http://localhost:8888/")]         // panel HTTPS portu
    [InlineData("http://127.0.0.1:3306/")]         // MariaDB
    [InlineData("http://127.0.0.1:6379/")]         // Redis
    [InlineData("http://127.0.0.1:8080/")]         // Stalwart yönetim
    [InlineData("http://127.0.0.1:8896/")]         // dbtools
    [InlineData("http://{HTTP_HOST}/x")]           // ölçülemeyen konak
    [InlineData("http://{R:1}.evil/x")]
    public async Task Rewrite_hedefi_yonetici_olmayan_icin_reddedilir(string rewriteUrl)
    {
        var hata = await Assert.ThrowsAsync<AppException>(() => Vekil(yonetici: false).EnsureRewriteTargetAllowedAsync(rewriteUrl, CancellationToken.None));
        Assert.Equal("proxy.destination_forbidden", hata.MessageKey);
    }

    [Theory]
    [InlineData("/index.php?x={R:1}")]
    [InlineData("index.php")]
    [InlineData("http://127.0.0.1:3000/{R:1}")]   // kendi Node uygulaması
    [InlineData("https://example.com/{R:1}")]
    public async Task Rewrite_hedefi_mesru_olanlar_gecer(string rewriteUrl)
    {
        await Vekil(yonetici: false).EnsureRewriteTargetAllowedAsync(rewriteUrl, CancellationToken.None);
    }

    [Fact]
    public async Task Rewrite_hedefi_yonetici_icin_serbest()
    {
        await Vekil(yonetici: true).EnsureRewriteTargetAllowedAsync("http://169.254.169.254/x", CancellationToken.None);
        await Vekil(yonetici: true).EnsureRewriteTargetAllowedAsync("http://127.0.0.1:3306/", CancellationToken.None);
    }

    [Fact]
    public async Task Vekil_hedefi_iki_yoldan_da_ayni_kapidan_gecer()
    {
        var hata = await Assert.ThrowsAsync<AppException>(() => Vekil(false).EnableProxyAsync(
            "site", new AdaPanel.Core.Dtos.Proxy.EnableReverseProxyRequest { DestinationUrl = "http://127.0.0.1:3306", MountPath = "/" },
            (_, _) => Task.CompletedTask));
        Assert.Equal("proxy.destination_forbidden", hata.MessageKey);
        hata = await Assert.ThrowsAsync<AppException>(() => Vekil(false).AddRewriteRuleAsync(
            "site", new AdaPanel.Core.Dtos.Proxy.AddRewriteRuleRequest { Name = "kural", MatchPattern = "^a$", RewriteUrl = "http://169.254.169.254/" },
            (_, _) => Task.CompletedTask));
        Assert.Equal("proxy.destination_forbidden", hata.MessageKey);
    }

    // ---------- G24: wp-cli.yml ve URL ----------

    private static string Gecici() => Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "ada-wp-" + Guid.NewGuid().ToString("N"))).FullName;

    [Theory]
    [InlineData("require:\n  - evil.php")]
    [InlineData("path: ../baska")]
    [InlineData("exec: |\n  system('calc');")]
    [InlineData("ssh: kullanici@host")]
    [InlineData("http: baska")]
    [InlineData("@prod:\n  ssh: x")]
    [InlineData("REQUIRE: x.php")]
    public void WpCli_tehlikeli_yml_reddedilir(string icerik)
    {
        var kok = Gecici();
        File.WriteAllText(Path.Combine(kok, "wp-cli.yml"), icerik);
        Assert.NotNull(SiteHostingSettingsService.FindWpCliProjectConfigViolation(kok));
    }

    [Fact]
    public void WpCli_ust_dizindeki_local_yml_de_taranir()
    {
        var kok = Gecici();
        var alt = Directory.CreateDirectory(Path.Combine(kok, "wp", "blog")).FullName;
        File.WriteAllText(Path.Combine(kok, "wp-cli.local.yml"), "require:\n  - x.php");
        Assert.Contains("wp-cli.local.yml", SiteHostingSettingsService.FindWpCliProjectConfigViolation(alt));
    }

    [Fact]
    public void WpCli_zararsiz_yml_ve_yml_olmayan_dizin_gecer()
    {
        var kok = Gecici();
        Assert.Null(SiteHostingSettingsService.FindWpCliProjectConfigViolation(kok));
        File.WriteAllText(Path.Combine(kok, "wp-cli.yml"), "apache_modules:\n  - mod_rewrite\nskip-plugins: true\n");
        Assert.Null(SiteHostingSettingsService.FindWpCliProjectConfigViolation(kok));
    }

    [Theory]
    [InlineData("plugin install http://169.254.169.254/x.zip")]
    [InlineData("theme install https://10.0.0.1/t.zip")]
    [InlineData("media import http://127.0.0.1:6379/a.png")]
    [InlineData("core download --locale=tr_TR http://192.168.1.1/wp.zip")]
    public async Task WpCli_indirme_komutlarinda_ic_ag_adresi_reddedilir(string komut)
    {
        var hata = await Assert.ThrowsAsync<AppException>(() =>
            SiteHostingSettingsService.EnsureWpCliUrlTargetsAllowedAsync(komut, GenelDns, CancellationToken.None));
        Assert.Equal("wpcli.url_forbidden", hata.MessageKey);
    }

    [Theory]
    [InlineData("plugin install https://downloads.wordpress.org/plugin/x.zip")]
    [InlineData("plugin install akismet --activate")]
    [InlineData("search-replace http://169.254.169.254 https://a.com")]   // metin değiştirme: indirme değil
    [InlineData("option update siteurl http://localhost")]
    public async Task WpCli_indirme_olmayan_ya_da_genel_adresler_gecer(string komut)
    {
        await SiteHostingSettingsService.EnsureWpCliUrlTargetsAllowedAsync(komut, GenelDns, CancellationToken.None);
    }

    // ---------- G21: alıcı politikası ----------

    private static ICustomerScopeService SahipServisi(Guid? sahip)
    {
        var proxy = DispatchProxy.Create<ICustomerScopeService, PostgresDatabaseIdentityConsumersTests.StrictProxy>();
        ((PostgresDatabaseIdentityConsumersTests.StrictProxy)(object)proxy).Handler = (m, _) =>
            m.Name == "GetSiteOwnerUserIdAsync" ? Task.FromResult(sahip) : throw new NotSupportedException(m.Name);
        return proxy;
    }

    private static async Task<(AdaPanelDbContext Db, SqliteConnection Baglanti, PanelUser Sahip, PanelUser Baska)> DbKur()
    {
        var baglanti = new SqliteConnection("Data Source=:memory:");
        await baglanti.OpenAsync();
        var db = new AdaPanelDbContext(new DbContextOptionsBuilder<AdaPanelDbContext>().UseSqlite(baglanti).Options);
        await db.Database.EnsureCreatedAsync();
        var sahip = new PanelUser { UserName = "sahip", Email = "sahip@gmail.com", Role = UserRole.Customer, IsActive = true };
        var baska = new PanelUser { UserName = "baska", Email = "baska@gmail.com", Role = UserRole.Customer, IsActive = true };
        db.Users.AddRange(sahip, baska);
        await db.SaveChangesAsync();
        return (db, baglanti, sahip, baska);
    }

    [Fact]
    public async Task Alici_politikasi_sahibin_hesap_adresini_kabul_baskasininkini_reddeder()
    {
        var (db, baglanti, sahip, baska) = await DbKur();
        await using var _ = baglanti;
        using var __ = db;
        var politika = new SiteBuilderContactRecipientPolicy(db, SahipServisi(sahip.Id));

        Assert.True(await politika.IsAllowedAsync("ornek.com", "Sahip@Gmail.com", CancellationToken.None));
        Assert.True(await politika.IsAllowedAsync("ornek.com", "info@ornek.com", CancellationToken.None));
        Assert.False(await politika.IsAllowedAsync("ornek.com", "baska@gmail.com", CancellationToken.None));
        Assert.False(await politika.IsAllowedAsync("ornek.com", "kurban@hotmail.com", CancellationToken.None));
    }

    [Fact]
    public async Task Alici_politikasi_sahipsiz_sitede_yalniz_yonetici_adresini_kabul_eder()
    {
        var (db, baglanti, _, baska) = await DbKur();
        await using var _b = baglanti;
        using var _d = db;
        db.Users.Add(new PanelUser { UserName = "yon", Email = "yonetici@panel.test", Role = UserRole.Administrator, IsActive = true });
        await db.SaveChangesAsync();
        var politika = new SiteBuilderContactRecipientPolicy(db, SahipServisi(null));

        Assert.True(await politika.IsAllowedAsync("ornek.com", "yonetici@panel.test", CancellationToken.None));
        Assert.False(await politika.IsAllowedAsync("ornek.com", baska.Email, CancellationToken.None));
    }

    [Fact]
    public async Task Kayit_yaniti_dogrulanamayan_alici_icin_UYARI_dondurur_dogrulananda_dondurmez()
    {
        await using var ortam = await SiteBuilderTestOrtami.KurAsync();

        async Task<SiteBuilderProjectDto> Kaydet(string adres) => await ortam.Servis.SaveProjectAsync(SiteBuilderTestOrtami.SiteAdi, new SaveSiteBuilderProjectRequest
        {
            Sections = [new SiteBuilderSectionDto { Type = "contact", Enabled = true, ContactFormEnabled = true, Email = adres }]
        });

        Assert.Equal("sitebuilder.form_recipient_not_verified", (await Kaydet("kurban@gmail.com")).ContactFormWarningKey);
        Assert.Equal(string.Empty, (await Kaydet("info@ornek.com")).ContactFormWarningKey);

        // Yayın planı da aynı sebebi söyler (form basılmaz).
        await Kaydet("kurban@gmail.com");
        Assert.Equal("sitebuilder.form_recipient_not_verified", (await ortam.Servis.PlanPublishAsync(SiteBuilderTestOrtami.SiteAdi)).ContactFormBlockedReasonKey);
    }
}
