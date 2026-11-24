using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Ssl;
using AdaPanel.Core.Entities;
using AdaPanel.Core.Enums;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Ssl;
using AdaPanel.Infrastructure.Support;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// M1: site silinince win-acme yenilemesi IPTAL edilir ve sertifika, yalniz kullanan yoksa depodan cikarilir.
/// Eskiden yalniz ssl.json kaydi siliniyordu: yenileme her gun sitesiz basarisiz oluyordu.
/// </summary>
public sealed class SslSiteCleanupTests : IDisposable
{
    private const string RemovedOutput =
        """{"status":"removed","bindingsMeasured":true,"storesMeasured":true,"stores":["My"],"usedBy":[]}""";
    private const string InUseOutput =
        """{"status":"in_use","bindingsMeasured":true,"storesMeasured":false,"stores":[],"usedBy":["other (*:443:x)"]}""";

    private readonly BulutAlani area = new();
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly AdaPanelDbContext db;
    private readonly string wacsDir = Path.Combine(Path.GetTempPath(), "wacs-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> commands = [];
    private readonly List<string> certificateScripts = [];

    public SslSiteCleanupTests()
    {
        connection.Open();
        db = new AdaPanelDbContext(new DbContextOptionsBuilder<AdaPanelDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();
        Directory.CreateDirectory(wacsDir);
        File.WriteAllText(Path.Combine(wacsDir, "wacs.exe"), "x");
    }

    public void Dispose()
    {
        db.Dispose(); connection.Dispose(); area.Dispose();
        if (Directory.Exists(wacsDir)) Directory.Delete(wacsDir, true);
    }

    /// <summary>win-acme'nin cancel SONRASI listesinde kalan kimlikler (cancelOk ve kalan = "komut başarılı ama kalktı sayılmaz").</summary>
    private readonly List<string> stillListed = [];

    private WinAcmeSslService Create(bool cancelOk, bool installed = true, string certificateOutput = RemovedOutput)
    {
        if (installed)
        {
            db.InstalledComponents.Add(new InstalledComponent
            {
                ComponentId = "win-acme", DisplayName = "win-acme", InstallDir = wacsDir, Status = ComponentInstallStatus.Installed
            });
            db.SaveChanges();
        }
        return new WinAcmeSslService(db, null!, null!, area.DosyaDeposu, null!, NullLogger<WinAcmeSslService>.Instance)
        {
            RenewalCommand = (_, args, _) =>
            {
                commands.Add(args);
                return Task.FromResult(new CommandResult { ExitCode = cancelOk ? 0 : 1, StandardError = cancelOk ? "" : "boom" });
            },
            RenewalRecordsAsync = (_, _, _) => Task.FromResult<IReadOnlyList<System.Text.Json.JsonElement>>(
                stillListed.Select(id => System.Text.Json.JsonDocument.Parse("{\"Id\":\"" + id + "\"}").RootElement.Clone()).ToArray()),
            RemoveCertificateCommand = (script, _) =>
            {
                certificateScripts.Add(script);
                return Task.FromResult(new CommandResult { ExitCode = 0, StandardOutput = certificateOutput });
            }
        };
    }

    private Task Seed(params SslCertificateDto[] records) => area.DosyaDeposu.WriteAsync("config/ssl.json", records);

    private static SslCertificateDto Rec(string site, string thumb, string? renewal, bool staging = false, string source = "manual") => new()
    {
        SiteName = site, HostName = site, Port = 443, Thumbprint = thumb, ManagedRenewalId = renewal,
        ManagedRenewalStaging = staging, Source = source
    };

    [Fact]
    public async Task DeletingASite_CancelsItsRenewal_AndRemovesTheUnusedCertificate()
    {
        var service = Create(cancelOk: true);
        await Seed(Rec("a.com", "AAA", "ren-1"), Rec("b.com", "BBB", "ren-2"));

        await service.RemoveSiteAsync("a.com");

        Assert.Single(commands);
        Assert.Contains("--cancel --id \"ren-1\"", commands[0]);
        Assert.Single(certificateScripts);
        Assert.Contains("AAA", certificateScripts[0]);
        var kalan = Assert.Single(await area.DosyaDeposu.ReadAsync("config/ssl.json", Array.Empty<SslCertificateDto>()));
        Assert.Equal("b.com", kalan.SiteName);
    }

    [Fact]
    public async Task ACertificateUsedByAnotherSiteRecord_IsKeptInTheStore()
    {
        var service = Create(cancelOk: true);
        await Seed(Rec("a.com", "AAA", "ren-1"), Rec("b.com", "AAA", "ren-2"));

        await service.RemoveSiteAsync("a.com");

        Assert.Single(commands);
        Assert.Empty(certificateScripts);
    }

    [Fact]
    public async Task ARenewalSharedWithAnotherSite_IsNotCancelled_AndIsReported()
    {
        var service = Create(cancelOk: true);
        await Seed(Rec("a.com", "AAA", "ren-1"), Rec("b.com", "BBB", "ren-1"));

        var hata = await Assert.ThrowsAsync<AppException>(() => service.RemoveSiteAsync("a.com"));

        Assert.Equal("ssl.site_cleanup_incomplete", hata.MessageKey);
        Assert.Empty(commands);
        Assert.Empty(certificateScripts);
    }

    [Fact]
    public async Task WhenCancelFails_TheLeftoverIsReported_AndTheCertificateIsNotDeleted()
    {
        var service = Create(cancelOk: false);
        await Seed(Rec("a.com", "AAA", "ren-1"));

        var hata = await Assert.ThrowsAsync<AppException>(() => service.RemoveSiteAsync("a.com"));

        Assert.Equal("ssl.site_cleanup_incomplete", hata.MessageKey);
        Assert.Contains("ren-1", hata.Details);
        Assert.Empty(certificateScripts);
    }

    [Fact]
    public async Task WhenWinAcmeIsMissing_TheLeftoverIsReported()
    {
        var service = Create(cancelOk: true, installed: false);
        await Seed(Rec("a.com", "AAA", "ren-1"));

        var hata = await Assert.ThrowsAsync<AppException>(() => service.RemoveSiteAsync("a.com"));

        Assert.Equal("ssl.site_cleanup_incomplete", hata.MessageKey);
        Assert.Empty(commands);
    }

    [Fact]
    public async Task ACertificateStillUsedByABinding_IsNotRemoved_WithoutBeingAFailure()
    {
        var service = Create(cancelOk: true, certificateOutput: InUseOutput);
        await Seed(Rec("a.com", "AAA", "ren-1"));

        await service.RemoveSiteAsync("a.com");

        Assert.Single(certificateScripts);
    }

    [Fact]
    public async Task ImportedCertificates_WithoutManagedRenewal_AreNeverTouched()
    {
        var service = Create(cancelOk: true);
        await Seed(Rec("a.com", "AAA", null));

        await service.RemoveSiteAsync("a.com");

        Assert.Empty(commands);
        Assert.Empty(certificateScripts);
        Assert.Empty(await area.DosyaDeposu.ReadAsync("config/ssl.json", Array.Empty<SslCertificateDto>()));
    }

    [Fact]
    public async Task ACancelCommandThatSucceedsButLeavesTheIdListed_IsNotTrusted()
    {
        var service = Create(cancelOk: true);
        stillListed.Add("ren-1");
        await Seed(Rec("a.com", "AAA", "ren-1"));

        var hata = await Assert.ThrowsAsync<AppException>(() => service.RemoveSiteAsync("a.com"));

        Assert.Equal("ssl.site_cleanup_incomplete", hata.MessageKey);
        Assert.Contains("ren-1", hata.Details);
        Assert.Empty(certificateScripts);
    }

    [Fact]
    public async Task AStagingRenewal_IsCancelledAgainstTheStagingServer()
    {
        var service = Create(cancelOk: true);
        await Seed(Rec("a.com", "AAA", "ren-1", staging: true));

        await service.RemoveSiteAsync("a.com");

        Assert.Contains("acme-staging-v02", Assert.Single(commands));
    }

    [Fact]
    public async Task ACertificateUsedBySmtpTls_IsNotDeleted()
    {
        var service = Create(cancelOk: true);
        await Seed(Rec("a.com", "AAA", "ren-1"));
        await area.DosyaDeposu.WriteAsync("config/mail-cert.json", new { source = "windows-store", thumbprint = "AAA" });

        await service.RemoveSiteAsync("a.com");

        Assert.Single(commands);
        Assert.Empty(certificateScripts);
    }

    [Fact]
    public async Task ALetsEncryptRecordWithoutRenewalId_IsReportedNotGuessed()
    {
        var service = Create(cancelOk: true);
        await Seed(Rec("a.com", "AAA", null, source: "letsencrypt"));

        var hata = await Assert.ThrowsAsync<AppException>(() => service.RemoveSiteAsync("a.com"));

        Assert.Contains("a.com", hata.Details);
        Assert.Empty(commands);
        Assert.Empty(certificateScripts);
    }

    [Fact]
    public void TheCascade_RemovesAliasesBeforeTheIisSite_AndSkipsSslWhenIisDeletionFailed()
    {
        var kaynak = Oku("src", "AdaPanel.Infrastructure", "Iis", "SiteCascadeDeletionService.cs");
        var alias = kaynak.IndexOf("siteAliasService.RemoveAliasAsync", StringComparison.Ordinal);
        var iis = kaynak.IndexOf("iisSiteService.DeleteSiteAsync(siteName", StringComparison.Ordinal);
        Assert.True(alias > 0 && alias < iis, "Takma adlar IIS sitesinden once temizlenmeli.");
        Assert.Contains("if (!result.Leftovers.Any(l => l.Kind == SiteLeftoverKinds.Site))", kaynak, StringComparison.Ordinal);
        Assert.Contains("RunExclusiveAsync(AdaPanel.Core.Interfaces.ConcurrencyKeys.Iis",
            Oku("src", "AdaPanel.Infrastructure", "Ssl", "WinAcmeSslService.cs"), StringComparison.Ordinal);
    }

    [Fact]
    public void TheCascade_RemovesSslOnlyAfterTheIisSiteIsGone_AndTheControllerIsHonest()
    {
        var kaynak = Oku("src", "AdaPanel.Infrastructure", "Iis", "SiteCascadeDeletionService.cs");
        var iis = kaynak.IndexOf("iisSiteService.DeleteSiteAsync(siteName", StringComparison.Ordinal);
        var ssl = kaynak.IndexOf("sslService.RemoveSiteAsync", StringComparison.Ordinal);
        Assert.True(iis > 0 && ssl > iis, "SSL temizligi IIS sitesi silinmeden once calisiyor.");

        var denetleyici = Oku("src", "AdaPanel.Api", "Controllers", "SitesController.cs");
        Assert.Contains("sites.delete.completed_with_leftovers", denetleyici, StringComparison.Ordinal);
        Assert.Contains("sites.delete.partial", denetleyici, StringComparison.Ordinal);
        Assert.Contains("sites.create.completed_with_warnings", denetleyici, StringComparison.Ordinal);
    }

    private static string Oku(params string[] parts)
    {
        var klasor = AppContext.BaseDirectory;
        for (var i = 0; i < 8; i++)
        {
            var aday = Path.Combine(new[] { klasor }.Concat(parts).ToArray());
            if (File.Exists(aday)) return File.ReadAllText(aday);
            klasor = Path.Combine(klasor, "..");
        }
        throw new FileNotFoundException(string.Join('/', parts));
    }
}
