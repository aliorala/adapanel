using System.Reflection;
using System.Security.Claims;
using AdaPanel.Api.Controllers;
using AdaPanel.Api.Security;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Logs;
using AdaPanel.Core.Dtos.Php;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Logs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// DALGA 3 — MÜŞTERİ SELF-SERVİS: müşteri KENDİ sitesinin günlüğünü, PHP ayarını ve hosting
/// ayarını yönetir; BAŞKASININ sitesine ve sunucu geneline ulaşamaz.
/// </summary>
/// <remarks>
/// Rol eklemek tek başına yetmez (sınıf+eylem rolleri VE ile birleşir, kapsam denetimi eylem
/// gövdesinde): bu testler (1) davranışı gerçek denetleyiciyle, (2) yapıyı yansımayla ölçer.
/// </remarks>
public sealed class CustomerSelfServiceAuthorizationTests
{
    private static readonly Guid Musteri = Guid.Parse("11111111-1111-1111-1111-111111111111");

    // ---------- düzenek: arayüz sahtesi (DispatchProxy) ----------

    public class Sahte : DispatchProxy
    {
        internal readonly List<string> Cagrilar = [];
        internal Func<string, object?[], object?>? Yanit;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            Cagrilar.Add(targetMethod!.Name);
            var sonuc = Yanit?.Invoke(targetMethod.Name, args ?? []);
            var donus = targetMethod.ReturnType;
            if (donus == typeof(Task))
            {
                return Task.CompletedTask;
            }

            if (donus.IsGenericType && donus.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var t = donus.GetGenericArguments()[0];
                var deger = sonuc ?? (t.IsValueType ? Activator.CreateInstance(t) : null);
                return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(t).Invoke(null, [deger]);
            }

            return sonuc;
        }
    }

    private static (T Arayuz, Sahte Kayit) Yap<T>(Func<string, object?[], object?>? yanit = null)
        where T : class
    {
        var proxy = DispatchProxy.Create<T, Sahte>();
        var kayit = (Sahte)(object)proxy;
        kayit.Yanit = yanit;
        return (proxy, kayit);
    }

    private static ICustomerScopeService Kapsam(params string[] siteler) =>
        Yap<ICustomerScopeService>((ad, _) => ad switch
        {
            "GetAssignedSitesAsync" => (IReadOnlyCollection<string>)siteler,
            "GetResellerSitesAsync" => (IReadOnlyCollection<string>)siteler,
            _ => null
        }).Arayuz;

    private static ControllerContext Baglam(string rol) => new()
    {
        HttpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, Musteri.ToString()),
                new Claim(ClaimTypes.Role, rol)
            ], "test"))
        }
    };

    private static async Task<AppException> Reddedilir(Func<Task> eylem)
    {
        var hata = await Assert.ThrowsAsync<AppException>(eylem);
        Assert.Equal(403, hata.HttpStatusCode);
        return hata;
    }

    // ---------- A5: günlük ----------

    private static (LogsController Denetleyici, Sahte Okuyucu) Gunluk(string rol, params string[] siteler)
    {
        var (okuyucu, kayit) = Yap<ILogViewerService>((ad, _) =>
            ad == "TailSiteLogAsync"
                ? new LogTailDto { Name = "x", Path = @"C:\inetpub\logs\LogFiles\W3SVC7\u_ex1.log", Lines = ["satir"] }
                : null);
        var denetleyici = new LogsController(
            okuyucu,
            Yap<ILogRotationService>().Arayuz,
            Kapsam(siteler),
            Yap<IAuditService>().Arayuz)
        {
            ControllerContext = Baglam(rol)
        };
        return (denetleyici, kayit);
    }

    [Fact]
    public async Task Musteri_KENDI_sitesinin_gunlugunu_okur_ve_sunucu_yolu_gorunmez()
    {
        var (denetleyici, _) = Gunluk("Customer", "benim.test");

        var yanit = await denetleyici.GetSiteLogAsync("benim.test", 100, CancellationToken.None);

        var govde = Assert.IsType<OkObjectResult>(yanit.Result);
        var log = ((AdaPanel.Core.Dtos.Common.ApiResponse<LogTailDto>)govde.Value!).Data!;
        Assert.Equal(["satir"], log.Lines);
        Assert.Equal(string.Empty, log.Path);
    }

    [Fact]
    public async Task Yonetici_sunucu_yolunu_gormeye_devam_eder()
    {
        var (denetleyici, _) = Gunluk("Administrator");

        var yanit = await denetleyici.GetSiteLogAsync("herhangi.test", 100, CancellationToken.None);

        var log = ((AdaPanel.Core.Dtos.Common.ApiResponse<LogTailDto>)((OkObjectResult)yanit.Result!).Value!).Data!;
        Assert.Contains("W3SVC7", log.Path, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("baskasi.test")]
    [InlineData(@"..\..\benim.test")]
    [InlineData("../benim.test")]
    [InlineData(@"benim.test\..\..\Windows")]
    [InlineData(@"C:\Windows\System32\LogFiles")]
    [InlineData("")]
    public async Task Musteri_baskasinin_sitesine_ya_da_yol_gecisine_ULASAMAZ_dosya_okunmaz(string site)
    {
        var (denetleyici, okuyucu) = Gunluk("Customer", "benim.test");

        await Reddedilir(() => denetleyici.GetSiteLogAsync(site, 100, CancellationToken.None));

        // Kapsam reddi dosya okunmadan ÖNCE: okuyucu hiç çağrılmadı.
        Assert.DoesNotContain("TailSiteLogAsync", okuyucu.Cagrilar);
    }

    [Fact]
    public async Task Bayi_kendi_musterisinin_disindaki_siteyi_okuyamaz()
    {
        var (denetleyici, okuyucu) = Gunluk("Reseller", "musterisi.test");

        await Reddedilir(() => denetleyici.GetSiteLogAsync("baska-bayinin.test", 100, CancellationToken.None));
        Assert.DoesNotContain("TailSiteLogAsync", okuyucu.Cagrilar);
    }

    [Fact]
    public void Panel_gunlugu_ve_rotasyon_YALNIZ_yonetici_site_gunlugu_musteriye_acik()
    {
        Assert.Contains("Customer", Roller(typeof(LogsController), "GetSiteLogAsync"));
        foreach (var ad in new[] { "GetPanelLogAsync", "GetRotationAsync", "UpdateRotationAsync", "RunRotationAsync" })
        {
            Assert.Equal("Administrator", Roller(typeof(LogsController), ad));
        }

        // Müşteri günlük DOSYASINI silen döndürmeyi çalıştıramaz.
        Assert.DoesNotContain("Customer", Roller(typeof(LogsController), "RunSiteRotationAsync"));
    }

    // ---------- A6: PHP ----------

    private static (PhpController Denetleyici, Sahte Php) Php(string rol, params string[] siteler)
    {
        var (php, kayit) = Yap<IPhpService>((ad, _) => ad switch
        {
            "GetInstalledVersionsAsync" => (IReadOnlyCollection<PhpVersionDto>)
            [
                new PhpVersionDto { ComponentId = "php-8.3", DisplayName = "PHP 8.3", Version = "8.3", InstallDir = @"C:\ada\php83", ExecutablePath = @"C:\ada\php83\php-cgi.exe" }
            ],
            "UpdateSitePhpSettingsAsync" => new SitePhpSettingsDto(),
            "SetSitePhpVersionAsync" => new SitePhpAssignmentDto { ComponentId = "php-8.3", ExecutablePath = @"C:\ada\php83\php-cgi.exe" },
            _ => null
        });
        var denetleyici = new PhpController(php, Kapsam(siteler), Yap<IAuditService>().Arayuz)
        {
            ControllerContext = Baglam(rol)
        };
        return (denetleyici, kayit);
    }

    [Fact]
    public async Task Musteri_PHP_surumlerini_gorur_ama_sunucu_yollari_gorunmez()
    {
        var (denetleyici, _) = Php("Customer");

        var yanit = await denetleyici.GetInstalledVersionsAsync(CancellationToken.None);

        var surum = ((AdaPanel.Core.Dtos.Common.ApiResponse<IReadOnlyCollection<PhpVersionDto>>)((OkObjectResult)yanit.Result!).Value!).Data!.Single();
        Assert.Equal("php-8.3", surum.ComponentId);
        Assert.Equal(string.Empty, surum.InstallDir);
        Assert.Equal(string.Empty, surum.ExecutablePath);
    }

    [Fact]
    public async Task Musteri_KENDI_sitesinin_PHP_surumunu_secer_exe_yolu_donmez()
    {
        var (denetleyici, _) = Php("Customer", "benim.test");

        var yanit = await denetleyici.SetSitePhpVersionAsync(
            "benim.test", new SetSitePhpVersionRequest { ComponentId = "php-8.3" }, CancellationToken.None);

        var atama = ((AdaPanel.Core.Dtos.Common.ApiResponse<SitePhpAssignmentDto>)((OkObjectResult)yanit.Result!).Value!).Data!;
        Assert.Equal("php-8.3", atama.ComponentId);
        Assert.Equal(string.Empty, atama.ExecutablePath);
    }

    [Fact]
    public async Task Musteri_baskasinin_sitesinde_PHP_degistiremez_servis_cagrilmaz()
    {
        var (denetleyici, php) = Php("Customer", "benim.test");

        await Reddedilir(() => denetleyici.SetSitePhpVersionAsync(
            "baskasi.test", new SetSitePhpVersionRequest { ComponentId = "php-8.3" }, CancellationToken.None));
        await Reddedilir(() => denetleyici.UpdateSitePhpSettingsAsync(
            "baskasi.test", new UpdateSitePhpSettingsRequest(), CancellationToken.None));
        await Reddedilir(() => denetleyici.GetSitePhpSettingsAsync("baskasi.test", CancellationToken.None));

        Assert.DoesNotContain("SetSitePhpVersionAsync", php.Cagrilar);
        Assert.DoesNotContain("UpdateSitePhpSettingsAsync", php.Cagrilar);
        Assert.DoesNotContain("GetSitePhpSettingsAsync", php.Cagrilar);
    }

    [Theory]
    [InlineData("-1", 120, true)]    // memory_limit=-1
    [InlineData("256M", 0, true)]    // max_execution_time=0
    [InlineData("256M", 120, false)] // open_basedir kapatma
    public async Task Musteri_sinirsiz_limit_ve_izolasyon_kapatma_yazamaz(string bellek, int sure, bool openBasedirAcik)
    {
        var (denetleyici, php) = Php("Customer", "benim.test");
        var istek = new UpdateSitePhpSettingsRequest
        {
            MemoryLimit = bellek,
            MaxExecutionTime = sure,
            OpenBasedirEnabled = openBasedirAcik
        };

        await Reddedilir(() => denetleyici.UpdateSitePhpSettingsAsync("benim.test", istek, CancellationToken.None));
        Assert.DoesNotContain("UpdateSitePhpSettingsAsync", php.Cagrilar);
    }

    [Fact]
    public async Task Musteri_sinirli_normal_PHP_ayarini_kendi_sitesine_yazar()
    {
        var (denetleyici, php) = Php("Customer", "benim.test");

        await denetleyici.UpdateSitePhpSettingsAsync(
            "benim.test",
            new UpdateSitePhpSettingsRequest { MemoryLimit = "512M", MaxExecutionTime = 300, OpenBasedirEnabled = true },
            CancellationToken.None);

        Assert.Contains("UpdateSitePhpSettingsAsync", php.Cagrilar);
    }

    [Fact]
    public void PHP_kurulum_kaldirma_redis_ve_genel_ini_YALNIZ_yonetici()
    {
        foreach (var ad in new[] { "EnableRedisAsync", "InstallVersionAsync", "GetGlobalPhpSettingsAsync", "UpdateGlobalPhpSettingsAsync" })
        {
            Assert.Equal("Administrator", Roller(typeof(PhpController), ad));
        }

        Assert.Contains("Customer", ClassRoles(typeof(PhpController)));
        Assert.NotNull(typeof(PhpController).GetCustomAttribute<RequireSubUserSectionAttribute>());
    }

    // ---------- A6: hosting ayarı ----------

    [Fact]
    public void Hosting_ayari_musteriye_YALNIZ_okuma_ve_ayar_yazma_komut_ve_goc_uclari_kapali()
    {
        var acik = new[] { "GetAsync", "UpdateAsync" };
        var metotlar = typeof(SiteHostingSettingsController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(item => item.GetCustomAttributes<Microsoft.AspNetCore.Mvc.Routing.HttpMethodAttribute>().Any())
            .ToArray();

        Assert.True(metotlar.Length > 10);
        foreach (var metot in metotlar)
        {
            var roller = metot.GetCustomAttribute<AuthorizeAttribute>()?.Roles ?? string.Empty;
            if (acik.Contains(metot.Name))
            {
                Assert.Equal(string.Empty, roller);
            }
            else
            {
                Assert.True(
                    roller == "Administrator,Reseller",
                    $"{metot.Name}: müşteriye KAPALI olmalı (eylem rolü 'Administrator,Reseller'), bulunan '{roller}'.");
            }
        }

        Assert.NotNull(typeof(SiteHostingSettingsController).GetCustomAttribute<RequireSubUserSectionAttribute>());
    }

    [Fact]
    public async Task Musteri_hosting_ayarinda_bant_genisligi_ve_baglanti_sinirini_DEGISTIREMEZ()
    {
        UpdateSiteHostingSettingsRequest? yazilan = null;
        var (servis, _) = Yap<ISiteHostingSettingsService>((ad, args) =>
        {
            if (ad == "GetAsync")
            {
                return new SiteHostingSettingsDto { MaximumBandwidthBytesPerSecond = 1000, MaximumConnections = 50 };
            }

            if (ad == "UpdateAsync")
            {
                yazilan = (UpdateSiteHostingSettingsRequest)args[1]!;
                return new SiteHostingSettingsDto();
            }

            return null;
        });
        var denetleyici = new SiteHostingSettingsController(
            servis,
            Kapsam("benim.test"),
            Yap<IFileMigrationService>().Arayuz,
            Yap<IAuditService>().Arayuz)
        {
            ControllerContext = Baglam("Customer")
        };

        await denetleyici.UpdateAsync(
            "benim.test",
            new UpdateSiteHostingSettingsRequest { MaximumBandwidthBytesPerSecond = 0, MaximumConnections = 0, HstsEnabled = true },
            CancellationToken.None);

        Assert.NotNull(yazilan);
        Assert.Equal(1000, yazilan!.MaximumBandwidthBytesPerSecond);
        Assert.Equal(50, yazilan.MaximumConnections);
        Assert.True(yazilan.HstsEnabled);
    }

    [Fact]
    public async Task Musteri_baskasinin_hosting_ayarini_okuyamaz_yazamaz()
    {
        var (servis, kayit) = Yap<ISiteHostingSettingsService>();
        var denetleyici = new SiteHostingSettingsController(
            servis,
            Kapsam("benim.test"),
            Yap<IFileMigrationService>().Arayuz,
            Yap<IAuditService>().Arayuz)
        {
            ControllerContext = Baglam("Customer")
        };

        await Reddedilir(() => denetleyici.GetAsync("baskasi.test", CancellationToken.None));
        await Reddedilir(() => denetleyici.UpdateAsync("baskasi.test", new UpdateSiteHostingSettingsRequest(), CancellationToken.None));
        Assert.Empty(kayit.Cagrilar);
    }

    // ---------- düzenek ----------

    private static string Roller(Type tip, string metot)
    {
        var bulunan = tip.GetMethods(BindingFlags.Public | BindingFlags.Instance).First(item => item.Name == metot);
        return bulunan.GetCustomAttribute<AuthorizeAttribute>()?.Roles ?? string.Empty;
    }

    private static string ClassRoles(Type tip) => tip.GetCustomAttribute<AuthorizeAttribute>()?.Roles ?? string.Empty;
}
