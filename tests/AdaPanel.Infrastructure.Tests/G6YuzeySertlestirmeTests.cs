using System.Net;
using System.Security.Claims;
using AdaPanel.Api.Controllers;
using AdaPanel.Api.Middleware;
using AdaPanel.Api.Security;
using AdaPanel.Core.Common;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Git;
using AdaPanel.Infrastructure.Iis;
using AdaPanel.Infrastructure.Scheduler;
using AdaPanel.Infrastructure.SiteBuilder;
using AdaPanel.Infrastructure.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// G6 turu: git/webmail/dbtools/iletisim formu/WP-CLI/ters vekil/adres koruyucu/cron adresi/hata
/// eşlemesi/docker sertleştirmeleri. Her test KORUMANIN KALKMASI hâlinde kırılır.
/// </summary>
public sealed class G6YuzeySertlestirmeTests
{
    // ---------- G6: git kiracının .git/config dosyasıyla kod çalıştıramaz ----------

    [Fact]
    public void Git_komut_satiri_yurutucu_ayarlarini_kapatir()
    {
        var arguman = GitDeploymentService.BuildGitArguments("status");

        Assert.Contains("core.fsmonitor=false", arguman);
        Assert.Contains("core.hooksPath=NUL", arguman);
        Assert.Contains("core.sshCommand=ssh", arguman);
        Assert.Contains("protocol.file.allow=never", arguman);
        Assert.Contains("core.askPass=echo", arguman);
        Assert.EndsWith("status", arguman);
    }

    /// <summary>DAVRANIŞ: depodaki fsmonitor komutu panelin git çağrısında ÇALIŞMAZ.</summary>
    [Fact]
    public async Task Depo_icindeki_fsmonitor_komutu_CALISMAZ()
    {
        var kok = Path.Combine(Path.GetTempPath(), $"ada-g6-{Guid.NewGuid():N}");
        Directory.CreateDirectory(kok);
        var init = await GitDeploymentService.RunGitForTests("init -q", kok, CancellationToken.None);
        Assert.True(init.Success, init.StandardError);

        var isaret = Path.Combine(kok, "pwned.txt").Replace('\\', '/');
        File.AppendAllText(
            Path.Combine(kok, ".git", "config"),
            $"\n[core]\n\tfsmonitor = \"echo x > '{isaret}'; echo\"\n");

        await GitDeploymentService.RunGitForTests("status", kok, CancellationToken.None);

        Assert.False(File.Exists(Path.Combine(kok, "pwned.txt")), "fsmonitor komutu çalıştı.");
    }

    // ---------- G14: alan adsız webmail açma yalnız yönetici ----------

    [Theory]
    [InlineData("Customer")]
    [InlineData("Reseller")]
    public async Task Alan_adsiz_webmail_acma_musteri_ve_bayiye_KAPALI(string rol)
    {
        var denetleyici = new MailController(null!, null!, null!, null!, null!)
        {
            ControllerContext = new ControllerContext { HttpContext = Baglam(rol) }
        };

        var hata = await Assert.ThrowsAsync<AppException>(() =>
            denetleyici.OpenWebmailAsync(new AdaPanel.Core.Dtos.Mail.WebmailOpenRequest(), CancellationToken.None));
        Assert.Equal(403, hata.HttpStatusCode);
    }

    // ---------- G15: dbtools vekili panel kimliğini araca iletmez ----------

    [Fact]
    public void DbTools_panel_cerezlerini_atar_arac_cerezlerini_korur()
    {
        var sonuc = DbToolsProxyController.StripPanelCookies(
            "AdaPanel.Auth=SECRET; phpMyAdmin=abc; AdaPanel.AntiCsrf=x; .AspNetCore.Antiforgery.q=y; pmaUser-1=z",
            "AdaPanel.Auth");

        Assert.DoesNotContain("SECRET", sonuc);
        Assert.DoesNotContain("AdaPanel.AntiCsrf", sonuc);
        Assert.DoesNotContain("Antiforgery", sonuc);
        Assert.Equal("phpMyAdmin=abc; pmaUser-1=z", sonuc);
    }

    [Fact]
    public void DbTools_ozel_adli_oturum_cerezi_de_atilir()
    {
        var sonuc = DbToolsProxyController.StripPanelCookies("OzelOturum=gizli; a=b", "OzelOturum");
        Assert.Equal("a=b", sonuc);
    }

    [Fact]
    public void DbTools_csp_cerceveleme_ve_nesneyi_sinirlar()
    {
        Assert.Contains("frame-ancestors 'self'", PanelSecurityHeaders.DbToolsContentSecurityPolicy);
        Assert.Contains("object-src 'none'", PanelSecurityHeaders.DbToolsContentSecurityPolicy);
    }

    // ---------- G21: iletişim formu alıcısı ----------

    [Theory]
    [InlineData("ornek.com", "sahip@ornek.com", true)]
    [InlineData("ornek.com", "x@mail.ornek.com", true)]
    [InlineData("ornek.com", "kurban@gmail.com", false)]
    [InlineData("ornek.com", "x@evilornek.com", false)]
    [InlineData("ornek.com", "x@ornek.com.evil.net", false)]
    public void Iletisim_alicisi_site_alan_adinda_olmali(string site, string alici, bool beklenen)
    {
        Assert.Equal(beklenen, SiteBuilderContactService.IsRecipientInSiteDomain(site, alici));
    }

    [Fact]
    public async Task Iletisim_formu_site_disi_adrese_POSTA_YOLLAMAZ()
    {
        await using var ortam = await SiteBuilderTestOrtami.KurAsync();
        await ortam.Servis.SaveProjectAsync(SiteBuilderTestOrtami.SiteAdi, new AdaPanel.Core.Dtos.SiteBuilder.SaveSiteBuilderProjectRequest
        {
            Sections =
            [
                new AdaPanel.Core.Dtos.SiteBuilder.SiteBuilderSectionDto
                {
                    Type = "contact", Enabled = true, ContactFormEnabled = true, Email = "kurban@gmail.com"
                }
            ]
        });

        var sonuc = await ortam.FormServisi().SubmitAsync(
            SiteBuilderService.StorageKey(SiteBuilderTestOrtami.SiteAdi),
            new SiteBuilderContactSubmission("Ad", "z@example.org", "Konu", "Mesaj", null),
            "10.0.0.9");

        Assert.Equal(SiteBuilderContactOutcome.NotAvailable, sonuc.Outcome);
        Assert.Empty(ortam.Postaci.Gonderilenler);
    }

    [Fact]
    public void Alici_basina_gunluk_sinir_uygulanir()
    {
        var sinir = new SiteBuilderContactRateLimiter();
        var an = DateTime.UtcNow;

        // Farklı IP ve site anahtarlarıyla: IP/site sınırı değil ALICI sınırı ölçülür.
        for (var i = 0; i < SiteBuilderContactRateLimiter.MaxPerRecipientPerDay; i++)
        {
            Assert.True(sinir.TryAcquire($"10.1.{i / 250}.{i % 250}", $"site{i}", $"g{i}@x.com", an, "Sahip@ornek.com"));
        }

        Assert.False(sinir.TryAcquire("10.9.9.9", "siteX", "son@x.com", an, "sahip@ornek.com"));
        Assert.True(sinir.TryAcquire("10.9.9.9", "siteX", "son@x.com", an + SiteBuilderContactRateLimiter.RecipientWindow + TimeSpan.FromSeconds(1), "sahip@ornek.com"));
    }

    // ---------- G24: WP-CLI beyaz liste ----------

    [Theory]
    [InlineData("plugin list")]
    [InlineData("theme activate twentytwentyfour")]
    [InlineData("option get siteurl")]
    [InlineData("option update blogname Foo")]
    [InlineData("search-replace http://a https://a")]
    [InlineData("db export dump.sql")]
    [InlineData("cache flush")]
    public void WpCli_izinli_komutlar_gecer(string komut)
    {
        SiteHostingSettingsService.ValidateWpCliCommand(komut);
    }

    [Theory]
    [InlineData("eval phpinfo();")]
    [InlineData("eval-file x.php")]
    [InlineData("shell")]
    [InlineData("package install foo")]
    [InlineData("server")]
    [InlineData("--path=C:/baska plugin list")]
    [InlineData("plugin list --require=evil.php")]
    [InlineData("plugin list --exec=evil()")]
    [InlineData("plugin list --ssh=host")]
    [InlineData("plugin list --http=host")]
    [InlineData("db query SELECT")]
    [InlineData("option")]
    [InlineData("cli update")]
    [InlineData("bilinmeyen komut")]
    public void WpCli_tehlikeli_komutlar_REDDEDILIR(string komut)
    {
        var hata = Assert.Throws<AppException>(() => SiteHostingSettingsService.ValidateWpCliCommand(komut));
        Assert.Equal(400, hata.HttpStatusCode);
    }

    // ---------- G30: adres koruyucu gömülü IPv4'ü açar ----------

    [Theory]
    [InlineData("64:ff9b::7f00:1")]        // NAT64 -> 127.0.0.1
    [InlineData("64:ff9b::a9fe:a9fe")]     // NAT64 -> 169.254.169.254
    [InlineData("64:ff9b::c0a8:101")]      // NAT64 -> 192.168.1.1
    [InlineData("64:ff9b:1::1")]           // yerel NAT64 öneki
    [InlineData("2001:0:4136:e378:8000:63bf:80ff:fefe")] // Teredo
    [InlineData("2002:7f00:1::")]          // 6to4 -> 127.0.0.1
    [InlineData("::ffff:127.0.0.1")]       // IPv4-eşlemeli
    [InlineData("::5efe:10.0.0.1")]        // ISATAP -> 10.0.0.1
    public void Gomulu_ic_adresler_ENGELLENIR(string adres)
    {
        Assert.True(OutboundAddressGuard.IsBlockedAddress(IPAddress.Parse(adres)), adres);
    }

    [Theory]
    [InlineData("64:ff9b::808:808")]       // NAT64 -> 8.8.8.8 (genel)
    [InlineData("2002:808:808::")]         // 6to4 -> 8.8.8.8
    [InlineData("2606:4700:4700::1111")]
    public void Gomulu_genel_adresler_ENGELLENMEZ(string adres)
    {
        Assert.False(OutboundAddressGuard.IsBlockedAddress(IPAddress.Parse(adres)), adres);
    }

    // ---------- G9: ters vekil hedefi ----------

    [Fact]
    public async Task Ters_vekil_yonetici_olmayan_icin_ic_agi_reddeder()
    {
        var servis = new AdaPanel.Infrastructure.ReverseProxy.ReverseProxyService(
            null!, null!, null!, Microsoft.Extensions.Logging.Abstractions.NullLogger<AdaPanel.Infrastructure.ReverseProxy.ReverseProxyService>.Instance);

        foreach (var hedef in new[] { "http://169.254.169.254/latest", "http://10.0.0.5:8080", "http://[64:ff9b::a9fe:a9fe]/" })
        {
            var hata = await Assert.ThrowsAsync<AppException>(() =>
                servis.EnableProxyAsync("site", new AdaPanel.Core.Dtos.Proxy.EnableReverseProxyRequest { DestinationUrl = hedef, MountPath = "/" },
                    (_, _) => Task.CompletedTask));
            Assert.Equal("proxy.destination_forbidden", hata.MessageKey);
        }
    }

    // ---------- G34: cron adresi ----------

    [Theory]
    [InlineData("http://a.com/\" --evil")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ftp://a.com/")]
    [InlineData("not a url")]
    public void Cron_adresi_tirnak_ve_gecersiz_sema_reddedilir(string url)
    {
        Assert.Throws<AppException>(() => UrlTaskRunner.SafeUrlArgument(url));
    }

    [Fact]
    public void Cron_adresi_kanonik_biciminde_yazilir()
    {
        Assert.Equal("https://a.com/x%20y", UrlTaskRunner.SafeUrlArgument("  https://a.com/x y "));
        var arguman = UrlTaskRunner.BuildAction("https://a.com/p?q=1", "C:/p.exe").Arguments;
        Assert.EndsWith(" \"https://a.com/p?q=1\"", arguman);
        Assert.Equal(2, arguman.Count(c => c == '"'));
    }

    // ---------- G27: iç mesaj sızmaz ----------

    [Theory]
    [InlineData("setup.already_initialized", true)]
    [InlineData("common.unexpected_error", true)]
    [InlineData("Could not find file 'C:\\secret\\x.txt'.", false)]
    [InlineData("SELECT * FROM Users", false)]
    [InlineData("", false)]
    public void Hata_mesaji_yalniz_ceviri_anahtari_ise_dondurulur(string mesaj, bool anahtar)
    {
        Assert.Equal(anahtar, ApiExceptionHandlingMiddleware.IsMessageKey(mesaj));
    }

    // ---------- G31: docker yalnız sunucu sahibi ----------

    [Theory]
    [InlineData(false, false)] // sahip olmayan yönetici
    [InlineData(true, true)]   // sahip ama taklit oturumu
    public async Task Docker_sunucu_sahibi_olmayana_ve_taklide_KAPALI(bool sahipMi, bool taklit)
    {
        var denetim = new RecordingAuditService();
        var denetleyici = new DockerController(null!, new SabitSahip(sahipMi), denetim)
        {
            ControllerContext = new ControllerContext { HttpContext = Baglam("Administrator", taklit) }
        };

        var hata = await Assert.ThrowsAsync<AppException>(() => denetleyici.GetAsync(CancellationToken.None));
        Assert.Equal(403, hata.HttpStatusCode);
        Assert.Contains("docker.denied", denetim.Actions);
    }

    private static HttpContext Baglam(string rol, bool taklit = false)
    {
        var talepler = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new(ClaimTypes.Role, rol)
        };
        if (taklit) talepler.Add(new Claim("impersonator_id", Guid.NewGuid().ToString()));

        return new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(talepler, "test"))
        };
    }

    private sealed class SabitSahip(bool sahip) : IServerOwnerService
    {
        public Task<Guid?> GetServerOwnerIdAsync(CancellationToken cancellationToken = default) => Task.FromResult<Guid?>(null);

        public Task<bool> IsServerOwnerAsync(Guid userId, CancellationToken cancellationToken = default) => Task.FromResult(sahip);
    }
}
