using System.Reflection;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Databases;
using AdaPanel.Core.Dtos.Ftp;
using AdaPanel.Core.Dtos.Mail;
using AdaPanel.Core.Dtos.Proxy;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Iis;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Services;
using AdaPanel.Infrastructure.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// CASCADE SILME, VERITABANININ SAHIBINI ADINDAN VARSAYAMAZ.
/// <para>
/// Eski kural: "adi siteyle esleşen (ya da DatabaseNameFromSite ile turetilene esit olan) her
/// veritabanini DROP et". Bu, geri alinamaz bir isi DOGRULANMAMIS bir varsayima baglamakti.
/// Panel hangi veritabaninin hangi siteye ait oldugunu ZATEN biliyor: acik site-DB bagi
/// (ISiteDatabaseLinkService) ve sahiplik kaydi (IDatabaseOwnershipService, site adini da tutar).
/// Silmeden hemen sonra RemoveBySiteAsync ile bu kayitlar temizleniyordu -- yani otorite elimizde
/// olmasina ragmen SILME KARARINDA kullanilmiyordu.
/// </para>
/// <para>
/// SitesController.ResolveLinkableDatabasesAsync dogru kurali zaten uyguluyordu; artik silme yolu
/// da ayni otoriteyi kullaniyor. Ad tahminiyle bulunanlar SILINMEZ, yalnizca bildirilir.
/// </para>
/// </summary>
public sealed class SiteCascadeDatabaseOwnershipTests
{
    private const string Site = "musteri.com";

    /// <summary>Ayni adi tasiyan ama HICBIR kaydi olmayan veritabani ASLA silinmemeli.</summary>
    [Fact]
    public void Adi_esleşen_ama_sahipsiz_veritabani_silinmez()
    {
        var silinecek = SiteCascadeDeletionService.SelectDeletableDatabases(Site, [], []);

        Assert.Empty(silinecek);
    }

    /// <summary>
    /// ...ve "yok sayilmis" gibi de davranilmaz: kullanici eskiden bunun silinecegini
    /// varsayiyordu, o yuzden "silmedik ve NEDEN" bilgisi uretilir.
    /// </summary>
    [Fact]
    public void Sahipsiz_veritabani_muhtemel_olarak_bildirilir()
    {
        var muhtemel = SiteCascadeDeletionService.SelectProbableDatabases(
            Site,
            [("mariadb", "musteri.com"), ("mysql", "baskasinin_db")],
            owned: []);

        Assert.Equal(["mariadb:musteri.com"], muhtemel);
    }

    /// <summary>Acik site-DB bagi olan veritabani silinir -- adi siteyle hic ilgisiz olsa bile.</summary>
    [Fact]
    public void Acik_bagi_olan_veritabani_silinir()
    {
        var silinecek = SiteCascadeDeletionService.SelectDeletableDatabases(
            Site,
            [new SiteDatabaseLinkDto { Engine = "mariadb", DatabaseName = "karsilastir", SiteName = Site }],
            []);

        Assert.Equal(["mariadb:karsilastir"], silinecek);
    }

    /// <summary>Sahiplik kaydi da otoritedir (DB olusturulurken hangi siteye ait oldugu yazilir).</summary>
    [Fact]
    public void Sahiplik_kaydi_olan_veritabani_silinir()
    {
        var silinecek = SiteCascadeDeletionService.SelectDeletableDatabases(
            Site,
            [],
            [("mysql", "musteri_db", Guid.NewGuid(), Site)]);

        Assert.Equal(["mysql:musteri_db"], silinecek);
    }

    /// <summary>BASKA bir siteye ait kayit bu silmeye karismaz -- capraz-kiraci DROP'un kapisi budur.</summary>
    [Fact]
    public void Baska_siteye_ait_kayit_bu_silmede_kullanilmaz()
    {
        var silinecek = SiteCascadeDeletionService.SelectDeletableDatabases(
            Site,
            [],
            [("mysql", "kurban_db", Guid.NewGuid(), "kurban.com")]);

        Assert.Empty(silinecek);
    }

    /// <summary>Ayni veritabani hem bagli hem sahipliyse iki kez silinmeye calisilmamali.</summary>
    [Fact]
    public void Ayni_veritabani_iki_kez_listelenmez()
    {
        var silinecek = SiteCascadeDeletionService.SelectDeletableDatabases(
            Site,
            [new SiteDatabaseLinkDto { Engine = "mariadb", DatabaseName = "ortak", SiteName = Site }],
            [("mariadb", "ortak", Guid.NewGuid(), Site)]);

        Assert.Equal(["mariadb:ortak"], silinecek);
    }

    /// <summary>Sahipli olan bir DB "muhtemel" listesine DUSMEZ (yoksa hem silinir hem uyarilirdi).</summary>
    [Fact]
    public void Sahipli_veritabani_muhtemel_sayilmaz()
    {
        var muhtemel = SiteCascadeDeletionService.SelectProbableDatabases(
            Site,
            [("mariadb", "musteri.com")],
            owned: ["mariadb:musteri.com"]);

        Assert.Empty(muhtemel);
    }

    /// <summary>
    /// KABLOLAMA: silme dongusu otorite kumesini kullanmali. Saf islev dogru olsa da cagrilmiyorsa
    /// hicbir sey degismezdi.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Silme_dongusu_otorite_kumesini_kullanir(bool explicitLink)
    {
        const string engine = "postgresql";
        const string owned = "explicit_database";
        var owner = Guid.NewGuid();
        var databases = new HashSet<string>(StringComparer.Ordinal) { owned, Site };
        var deleted = new List<string>();
        var ownershipExists = true;
        var links = new List<SiteDatabaseLinkDto>();
        if (explicitLink) links.Add(new SiteDatabaseLinkDto { Engine = engine, DatabaseName = owned, SiteName = Site });

        var ownership = Fake<IDatabaseOwnershipService>((method, args) => method.Name switch
        {
            "GetAllAsync" => Task.FromResult<IReadOnlyCollection<(string, string, Guid, string)>>(ownershipExists
                ? [(engine, owned, owner, explicitLink ? "" : Site)] : []),
            "GetOwnerAsync" => Task.FromResult<Guid?>(ownershipExists && (string)args[1]! == owned ? owner : null),
            "RemoveAsync" => RemoveOwnership(),
            _ => throw new NotSupportedException(method.Name)
        });
        Task RemoveOwnership() { ownershipExists = false; return Task.CompletedTask; }

        var databaseService = Fake<IDatabaseService>((method, args) => method.Name switch
        {
            "GetWorkspaceAsync" => Task.FromResult(new DatabaseWorkspaceDto
            {
                Engine = (string)args[0]!,
                Databases = (string)args[0]! == engine ? databases.Select(name => new DatabaseRecordDto { Name = name }).ToArray() : []
            }),
            "DeleteDatabaseAsync" => Drop(args),
            _ => throw new NotSupportedException(method.Name)
        });
        async Task Drop(object?[] args)
        {
            if (args.Length == 5) await ((Func<CancellationToken, Task>)args[2]!)(default);
            var name = (string)args[1]!;
            deleted.Add(name);
            databases.Remove(name);
            if (args.Length == 5) await ((Func<CancellationToken, Task>)args[3]!)(default);
        }

        var linkService = Fake<ISiteDatabaseLinkService>((method, args) => method.Name switch
        {
            "GetForSiteAsync" => Task.FromResult<IReadOnlyList<SiteDatabaseLinkDto>>(links.Where(link => link.SiteName == (string)args[0]!).ToArray()),
            "UnlinkAsync" => Unlink((string)args[0]!, (string)args[1]!),
            _ => throw new NotSupportedException(method.Name)
        });
        Task Unlink(string requestedEngine, string name)
        {
            links.RemoveAll(link => DatabaseResourceIdentity.Same(link.Engine, link.DatabaseName, requestedEngine, name));
            return Task.CompletedTask;
        }

        // The root is unique and absent; cascade only reads fallback JSON and never deletes real files.
        var options = Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions
        {
            DataRoot = Path.Combine(Path.GetTempPath(), "AdaPanel-cascade-authority-" + Guid.NewGuid().ToString("N"))
        });
        var service = new SiteCascadeDeletionService(
            Fake<IIisSiteService>((method, _) => method.Name switch
            {
                "GetSiteAsync" => Task.FromResult(new SiteDto { Name = Site }),
                "DeleteSiteAsync" => Task.CompletedTask,
                _ => throw new NotSupportedException(method.Name)
            }), null!,
            Fake<IFtpService>((_, _) => Task.FromResult<IReadOnlyCollection<FtpUserDto>>([])),
            Fake<IMailService>((_, _) => Task.FromResult(new MailWorkspaceDto())),
            databaseService, linkService, ownership,
            Fake<IReverseProxyService>((_, _) => Task.FromResult(new ReverseProxySiteConfigurationDto())),
            Fake<IDiskQuotaService>((_, _) => Task.FromResult(new SiteDiskQuotaDto())),
            Fake<ICustomerScopeService>((_, _) => Task.CompletedTask),
            Fake<ISslService>((_, _) => Task.CompletedTask),
            Fake<ISiteHostingSettingsService>((_, _) => Task.CompletedTask),
            Fake<IWafService>((_, _) => Task.CompletedTask),
            Fake<IDirectoryProtectionService>((_, _) => Task.CompletedTask),
            null!, null!, options, new JsonFileStore(options), NullLogger<SiteCascadeDeletionService>.Instance, new ConcurrencyLock());

        var result = await service.DeleteAsync(Site, deleteFiles: false, deleteDatabases: true);

        Assert.Equal([owned], deleted);
        Assert.Equal([Site], databases);
        Assert.False(ownershipExists);
        var leftover = Assert.Single(result.Leftovers, item => item.Kind == SiteLeftoverKinds.Database);
        Assert.Equal($"{engine}:{Site}", leftover.Name);
        Assert.True(leftover.Deliberate);
        Assert.Contains(result.Warnings, warning => warning.Contains($"{engine}:{Site}", StringComparison.Ordinal));
    }

    private static T Fake<T>(Func<MethodInfo, object?[], object?> handler) where T : class
    {
        var proxy = DispatchProxy.Create<T, StrictProxy>();
        ((StrictProxy)(object)proxy).Handler = handler;
        return proxy;
    }

    public class StrictProxy : DispatchProxy
    {
        public Func<MethodInfo, object?[], object?> Handler { get; set; } = null!;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handler(targetMethod!, args ?? []);
    }

    /// <summary>Yikici sorgu parametreleri OPT-IN olmali: gondermeyen bir istemci veri kaybetmemeli.</summary>
    [Fact]
    public void Yikici_silme_parametreleri_varsayilan_olarak_kapali()
    {
        var kaynak = OkuKaynak("src", "AdaPanel.Api", "Controllers", "SitesController.cs");

        Assert.Contains("[FromQuery] bool deleteFiles = false", kaynak, StringComparison.Ordinal);
        Assert.Contains("[FromQuery] bool deleteDatabases = false", kaynak, StringComparison.Ordinal);
    }

    private static string OkuKaynak(params string[] parcalar)
    {
        var kok = AppContext.BaseDirectory;
        while (kok is not null && !File.Exists(Path.Combine(kok, "AdaPanel.sln")))
        {
            kok = Path.GetDirectoryName(kok);
        }

        Assert.NotNull(kok);
        return File.ReadAllText(Path.Combine([kok!, .. parcalar]));
    }
}
