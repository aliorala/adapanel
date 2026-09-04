using AdaPanel.Core.Dtos.Databases;
using AdaPanel.Core.Dtos.Scheduler;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.AppStore;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// WordPress güncelleme yönetimi.
/// <para>
/// Panel bugüne kadar WordPress'i yalnız OKUYORDU. Güncelleme eklenirken iki şey kritik: eklenti
/// adı wp-cli KOMUT SATIRINA gidiyor (süzülmezse ek argüman/bayrak sokulabilir) ve durum
/// okunamadığında "her şey güncel" DENMEMELİ — açığı olan bir siteyi güvende göstermek olurdu.
/// </para>
/// </summary>
public sealed class WordPressUpdateTests
{
    [Theory]
    // Slug'a sızdırılmaya çalışılan argüman/bayraklar temizlenmeli.
    [InlineData("akismet --skip-plugins", "akismet--skip-plugins")]
    [InlineData("wp;calc", "wpcalc")]
    [InlineData("a b && del", "abdel")]
    [InlineData("--allow-root", "--allow-root")]
    [InlineData("normal-slug_2", "normal-slug_2")]
    public async Task Eklenti_adi_komut_satirina_ek_arguman_SOKAMAZ(string girdi, string beklenenParca)
    {
        var kabuk = new KomutKaydedici();
        var servis = new WordPressToolkitService(new KullanilmayanSite(), new KullanilmayanDb(), kabuk);

        await servis.ApplyUpdatesAsync("ornek", new WordPressUpdateRequest { Plugins = [girdi] });

        var komut = Assert.Single(kabuk.Komutlar);
        Assert.Equal("plugin update " + beklenenParca, komut);
        // Bosluk YOK demek, ikinci bir arguman YOK demek: en onemli guvence bu.
        Assert.Equal(2, komut.Count(ch => ch == ' '));
    }

    [Fact]
    public async Task Her_oge_AYRI_komutla_guncellenir()
    {
        // Tek komutta toplamak, biri patlayinca kalanlarin da yapilmamasina yol acardi ve
        // kullanici hangisinin gectigini goremezdi.
        var kabuk = new KomutKaydedici();
        var servis = new WordPressToolkitService(new KullanilmayanSite(), new KullanilmayanDb(), kabuk);

        var sonuc = await servis.ApplyUpdatesAsync("ornek", new WordPressUpdateRequest
        {
            Core = true,
            Plugins = ["akismet", "jetpack"],
            Themes = ["twentytwentyfour"]
        });

        Assert.Equal(4, kabuk.Komutlar.Count);
        Assert.Equal(4, sonuc.Results.Count);
        Assert.Contains("core update", kabuk.Komutlar);
        Assert.Contains("plugin update akismet", kabuk.Komutlar);
        Assert.Contains("theme update twentytwentyfour", kabuk.Komutlar);
    }

    [Fact]
    public async Task Basarisiz_oge_GIZLENMEZ_digerleri_yine_calisir()
    {
        var kabuk = new KomutKaydedici { BasarisizIceren = "jetpack" };
        var servis = new WordPressToolkitService(new KullanilmayanSite(), new KullanilmayanDb(), kabuk);

        var sonuc = await servis.ApplyUpdatesAsync("ornek", new WordPressUpdateRequest
        {
            Plugins = ["akismet", "jetpack"]
        });

        Assert.Equal(2, sonuc.Results.Count);
        Assert.True(sonuc.Results.Single(item => item.Name == "akismet").Success);

        var basarisiz = sonuc.Results.Single(item => item.Name == "jetpack");
        Assert.False(basarisiz.Success);
        Assert.NotEmpty(basarisiz.Message);
    }

    [Fact]
    public async Task Liste_okunamazsa_MEASURED_false_olur_ve_bos_liste_guncel_SAYILMAZ()
    {
        var kabuk = new KomutKaydedici { HepsiBasarisiz = true };
        var servis = new WordPressToolkitService(new KullanilmayanSite(), new KullanilmayanDb(), kabuk);

        var sonuc = await servis.GetUpdatesAsync("ornek");

        Assert.False(sonuc.Measured);
        Assert.NotEmpty(sonuc.Error);
    }

    [Fact]
    public async Task JSON_olmayan_cikti_BOS_LISTE_sayilmaz()
    {
        // wp-cli cikis kodu 0 dondurup uyari metni yazabilir. Bunu "guncelleme yok" saymak,
        // okunamayan bir siteyi "guncel" gostermek olurdu.
        var kabuk = new KomutKaydedici { Cikti = "Warning: something odd happened" };
        var servis = new WordPressToolkitService(new KullanilmayanSite(), new KullanilmayanDb(), kabuk);

        var sonuc = await servis.GetUpdatesAsync("ornek");

        Assert.False(sonuc.Measured);
    }

    [Fact]
    public async Task Gercek_JSON_ciktisi_dogru_ayristirilir()
    {
        var kabuk = new KomutKaydedici
        {
            Cikti = """[{"name":"akismet","title":"Akismet","status":"active","version":"5.0","update":"available","update_version":"5.3"}]"""
        };
        var servis = new WordPressToolkitService(new KullanilmayanSite(), new KullanilmayanDb(), kabuk);

        var sonuc = await servis.GetUpdatesAsync("ornek");

        Assert.True(sonuc.Measured);
        var eklenti = sonuc.Items.First(item => item.Kind == "plugin");
        Assert.Equal("akismet", eklenti.Name);
        Assert.Equal("Akismet", eklenti.Title);
        Assert.Equal("5.0", eklenti.CurrentVersion);
        Assert.Equal("5.3", eklenti.AvailableVersion);
        Assert.True(eklenti.UpdateAvailable);
    }

    // --- düzenek ---

    /// <summary>Çalıştırılan wp-cli komutlarını KAYDEDER; asıl güvence komutun şeklidir.</summary>
    private sealed class KomutKaydedici : ISiteHostingSettingsService
    {
        public List<string> Komutlar { get; } = [];
        public string Cikti { get; set; } = "[]";
        public string? BasarisizIceren { get; set; }
        public bool HepsiBasarisiz { get; set; }

        public Task<ComposerRunResultDto> RunWpCliCommandAsync(string siteName, string command, CancellationToken cancellationToken = default)
        {
            Komutlar.Add(command);
            var basarisiz = HepsiBasarisiz ||
                            (BasarisizIceren is { Length: > 0 } parca && command.Contains(parca, StringComparison.Ordinal));

            return Task.FromResult(new ComposerRunResultDto
            {
                ExitCode = basarisiz ? 1 : 0,
                Output = basarisiz ? string.Empty : (command.StartsWith("core version", StringComparison.Ordinal) ? "6.4" : Cikti),
                Error = basarisiz ? "wp-cli hata" : string.Empty
            });
        }

        public Task<SiteHostingSettingsDto> GetAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteHostingSettingsDto> UpdateAsync(string siteName, UpdateSiteHostingSettingsRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ComposerRunResultDto> RunComposerInstallAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ComposerRunResultDto> RunComposerCommandAsync(string siteName, string command, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ComposerRunResultDto> RunArtisanCommandAsync(string siteName, string command, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ComposerRunResultDto> RunNodeCommandAsync(string siteName, string command, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteImportResultDto> ImportSiteArchiveAsync(string siteName, string archiveUrl, bool cleanFirst, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<QueueWorkerStatusDto> GetQueueWorkerAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<QueueWorkerStatusDto> StartQueueWorkerAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<QueueWorkerStatusDto> StopQueueWorkerAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<QueueWorkerStatusDto> GetLaravelSchedulerAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<QueueWorkerStatusDto> StartLaravelSchedulerAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<QueueWorkerStatusDto> StopLaravelSchedulerAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RemoveSiteAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class KullanilmayanSite : IIisSiteService
    {
        public Task<IReadOnlyCollection<SiteDto>> GetSitesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDto> GetSiteAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDto> CreateSiteAsync(CreateSiteRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDto> UpdatePhysicalPathAsync(string siteName, string physicalPath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteSiteAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDto> StartSiteAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDto> StopSiteAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDto> AddBindingAsync(string siteName, SiteBindingRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDto> RemoveBindingAsync(string siteName, RemoveSiteBindingRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<SiteDto>> ImportExistingSitesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class KullanilmayanDb : IDatabaseService
    {
        public Task<IReadOnlyCollection<DatabaseEngineStatusDto>> GetEnginesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DatabaseEngineStatusDto> RegisterCredentialsAsync(RegisterDatabaseCredentialsRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DatabaseWorkspaceDto> GetWorkspaceAsync(string engine, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DatabaseWorkspaceDto> CreateDatabaseAsync(CreateDatabaseRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DatabaseWorkspaceDto> ImportDatabaseAsync(ImportDatabaseRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteDatabaseAsync(string engine, string name, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DatabaseWorkspaceDto> CreateUserAsync(CreateDatabaseUserRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteUserAsync(string engine, string userName, string host, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DatabaseWorkspaceDto> SetUserPasswordAsync(SetDatabaseUserPasswordRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DatabaseWorkspaceDto> GrantAccessAsync(GrantDatabaseAccessRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DatabaseDumpResultDto> ExportDumpAsync(DatabaseDumpRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DatabaseDumpResultDto> ImportDumpAsync(DatabaseDumpRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DatabaseTransferResultDto> TransferDatabaseAsync(DatabaseTransferRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DatabaseDetailDto> GetDetailAsync(string engine, string databaseName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DatabaseCheckResultDto> CheckAndRepairAsync(string engine, string databaseName, IReadOnlyCollection<string> repairTables, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AdaPanel.Core.Dtos.Databases.DatabaseHardeningReport> HardenAdminAccountsAsync(string engine, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

}
