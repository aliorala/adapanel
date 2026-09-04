using AdaPanel.Core.Dtos.Databases;
using AdaPanel.Core.Dtos.Scheduler;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.AppStore;
using AdaPanel.Infrastructure.Audit;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// <c>WordPressToolkitService</c> gözlemlenebilirliği + SIR SIZINTISI koruması.
/// </summary>
/// <remarks>
/// <para>
/// Bu servisin neredeyse her dış çağrısı BEST-EFFORT: wp-cli yoksa, PHP çalışmıyorsa ya da DB'ye
/// bağlanılamıyorsa akış devam ediyor. Tasarım doğru, sonucu değildi — ölçemediğimiz her şey
/// panelde "sorun yok" gibi görünüyordu ve sunucuda iz kalmıyordu.
/// </para>
/// <para>
/// Sır tarafı burada özellikle keskin: wp-cli, wp-config'i okuyarak veritabanına bağlanır, yani
/// çıktısı DB kimlik bilgisi içerebilir. Kod bu metni <c>AuditSecretRedactor</c>'dan geçirir;
/// aşağıdaki testlerden biri tam olarak bunu ölçer — çünkü ILogger denetim kaydının merkezî
/// geçidinden GEÇMEZ ve koruma çağrı yerinde unutulabilir bir şeydir.
/// </para>
/// </remarks>
public sealed class WordPressToolkitObservabilityTests
{
    // --- Adres değiştirme: sessiz başarısızlığın en pahalısı ---

    /// <summary>
    /// wp-cli <c>search-replace</c> sıfır dışı kod döndüğünde kullanıcı yalnızca "adres değişmedi"
    /// bayrağını görür; SEBEBİ göremez, çünkü wp-cli çıktısı bilerek yanıta konmuyor. O hâlde
    /// sebebin yaşayabileceği tek yer günlüktür.
    /// <para>
    /// Neden bu kadar önemli: WordPress site adresini VERİTABANINDAN okur. Adresi değişmemiş bir
    /// klon ÜRETİM adresini servis eder — staging'de yapılan her tıklama canlı veriyi değiştirir.
    /// </para>
    /// <para>
    /// MUTASYON: <c>TryRewriteSiteUrlAsync</c> içindeki "adres degistirme BASARISIZ" <c>LogError</c>
    /// çağrısı silinirse bu test düşer.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Adres_degistirme_basarisiz_olunca_SEBEP_gunluge_yazilir()
    {
        var kabuk = new SahteWpKabugu { HepsiBasarisiz = true, Hata = "Error: could not connect" };
        var gunlukcu = new KayitTutanGunlukcu<WordPressToolkitService>();
        var servis = Servis(kabuk, gunlukcu);

        var basarili = await servis.TryRewriteSiteUrlAsync("staging", "http://canli.test", "http://staging.test", CancellationToken.None);

        Assert.False(basarili);
        Assert.True(
            gunlukcu.Icerir(LogLevel.Error, "staging", "http://canli.test", "http://staging.test"),
            $"Adres degistirme hatasi yazilmadi. Yazilanlar:\n{gunlukcu.TumMetin}");
    }

    /// <summary>
    /// SIR DENETİMİ. wp-cli hata metni veritabanı parolası taşıyabilir; günlüğe HAM yazılamaz.
    /// <para>
    /// MUTASYON: <c>WpCliGunlukMetni</c> yerine ham <c>sonuc.Error</c> yazılırsa bu test düşer.
    /// Kural burada yeniden yazılmıyor, denetim kaydının kullandığı merkezî temizleyici
    /// (<see cref="AuditSecretRedactor"/>) ELLE çağrılıyor — ILogger o geçitten geçmediği için.
    /// </para>
    /// </summary>
    [Fact]
    public async Task wp_cli_ciktisindaki_parola_gunluge_MASKELENEREK_yazilir()
    {
        var kabuk = new SahteWpKabugu
        {
            HepsiBasarisiz = true,
            Hata = "Error: connection failed for db_user=wp_kullanici password=CokGizliParola123"
        };
        var gunlukcu = new KayitTutanGunlukcu<WordPressToolkitService>();
        var servis = Servis(kabuk, gunlukcu);

        await servis.TryRewriteSiteUrlAsync("staging", "http://canli.test", "http://staging.test", CancellationToken.None);

        Assert.DoesNotContain("CokGizliParola123", gunlukcu.TumMetin, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(AuditSecretRedactor.Mask, gunlukcu.TumMetin, StringComparison.Ordinal);
    }

    /// <summary>
    /// wp-cli HİÇ çalıştırılamadığında (istisna) da aynı sebep kaydı yazılmalı: "olmadı" bilgisi
    /// çağırana gidiyor ama NEDENİ (wp-cli kurulu değil mi, PHP mi patladı) yalnız burada kalır.
    /// <para>MUTASYON: <c>catch</c> bloğundaki <c>LogError</c> silinirse bu test düşer.</para>
    /// </summary>
    [Fact]
    public async Task Adres_degistirme_hic_calistirilamazsa_ISTISNA_gunluge_yazilir()
    {
        var kabuk = new SahteWpKabugu { Firlat = true };
        var gunlukcu = new KayitTutanGunlukcu<WordPressToolkitService>();
        var servis = Servis(kabuk, gunlukcu);

        var basarili = await servis.TryRewriteSiteUrlAsync("staging", "http://canli.test", "http://staging.test", CancellationToken.None);

        Assert.False(basarili);
        var satir = Assert.Single(gunlukcu.Seviyede(LogLevel.Error));
        Assert.NotNull(satir.Istisna);
    }

    /// <summary>Adres zaten aynıysa değiştirilecek bir şey yok — gürültü de üretilmemeli.</summary>
    [Fact]
    public async Task Adres_zaten_ayniysa_gunluge_HICBIR_SEY_yazilmaz()
    {
        var gunlukcu = new KayitTutanGunlukcu<WordPressToolkitService>();
        var servis = Servis(new SahteWpKabugu(), gunlukcu);

        Assert.True(await servis.TryRewriteSiteUrlAsync("staging", "http://ayni.test", "http://ayni.test", CancellationToken.None));
        Assert.Empty(gunlukcu.Kayitlar);
    }

    // --- Güncelleme: "ölçemedik" ile "güncel" ayrı şeylerdir ---

    /// <summary>
    /// Liste okunamadığında DTO'daki <c>Measured=false</c> bayrağı arayüze ulaşır ama sunucuda iz
    /// bırakmaz. Bir siteyi haftalarca "ölçülemedi" durumunda tutan kalıcı arıza (wp-cli silinmiş,
    /// PHP sürümü değişmiş) ancak günlükte birikirse fark edilir.
    /// <para>MUTASYON: <c>GetUpdatesAsync</c> içindeki "OLCULEMEDI" uyarısı silinirse test düşer.</para>
    /// </summary>
    [Fact]
    public async Task Guncelleme_durumu_olculemeyince_UYARI_yazilir()
    {
        var kabuk = new SahteWpKabugu { HepsiBasarisiz = true };
        var gunlukcu = new KayitTutanGunlukcu<WordPressToolkitService>();
        var servis = Servis(kabuk, gunlukcu);

        var sonuc = await servis.GetUpdatesAsync("ornek");

        Assert.False(sonuc.Measured);
        Assert.True(
            gunlukcu.Icerir(LogLevel.Warning, "OLCULEMEDI", "ornek"),
            $"Olculemedi uyarisi yazilmadi. Yazilanlar:\n{gunlukcu.TumMetin}");
    }

    /// <summary>
    /// KISMİ BAŞARI: bazı öğeler güncellendi, bazıları olmadı. Yanıt bunu taşıyor ama yanıt
    /// kaybolur; "site güncellemeden sonra bozuldu" denince bakılacak ilk kayıt budur.
    /// <para>
    /// MUTASYON: <c>GuncelleAsync</c> içindeki öğe uyarısı ya da <c>ApplyUpdatesAsync</c> sonundaki
    /// "KISMEN basarisiz" özeti silinirse bu test düşer.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Guncellemede_kismi_basarisizlik_hem_oge_hem_OZET_olarak_yazilir()
    {
        var kabuk = new SahteWpKabugu { BasarisizIceren = "jetpack" };
        var gunlukcu = new KayitTutanGunlukcu<WordPressToolkitService>();
        var servis = Servis(kabuk, gunlukcu);

        var sonuc = await servis.ApplyUpdatesAsync("ornek", new WordPressUpdateRequest { Plugins = ["akismet", "jetpack"] });

        Assert.True(sonuc.Results.Single(item => item.Name == "akismet").Success);
        Assert.False(sonuc.Results.Single(item => item.Name == "jetpack").Success);

        // Öğe bazında sebep...
        Assert.True(
            gunlukcu.Icerir(LogLevel.Warning, "jetpack", "cikis kodu"),
            $"Basarisiz oge uyarisi yazilmadi. Yazilanlar:\n{gunlukcu.TumMetin}");
        // ...ve "1/2 oge guncellenemedi" özeti.
        Assert.True(
            gunlukcu.Icerir(LogLevel.Warning, "KISMEN basarisiz", "ornek"),
            $"Kismi basarisizlik ozeti yazilmadi. Yazilanlar:\n{gunlukcu.TumMetin}");
    }

    /// <summary>Hepsi geçtiyse uyarı YOK — kural "her satırı logla" değil.</summary>
    [Fact]
    public async Task Guncellemenin_tamami_gecerse_UYARI_uretilmez()
    {
        var gunlukcu = new KayitTutanGunlukcu<WordPressToolkitService>();
        var servis = Servis(new SahteWpKabugu(), gunlukcu);

        await servis.ApplyUpdatesAsync("ornek", new WordPressUpdateRequest { Plugins = ["akismet"] });

        Assert.Empty(gunlukcu.Seviyede(LogLevel.Warning));
        Assert.NotEmpty(gunlukcu.Seviyede(LogLevel.Information));
    }

    // --- Sertleştirme: GÜVENLİK konusunda kısmi başarı ---

    /// <summary>
    /// <c>HardenAsync</c> yeşil bir sonuç döner ve listede hangi maddelerin geçtiğini gösterir; bir
    /// maddenin NEDEN listede olmadığı hiçbir yere yazılmıyordu. "file_edit" eksikse WordPress
    /// yönetici panelinden PHP dosyası düzenlemek AÇIK kalır — ele geçirilen bir yönetici hesabı
    /// doğrudan kod çalıştırabilir. Kullanıcının bunu listedeki eksikten çıkarması beklenemez.
    /// <para>
    /// MUTASYON: <c>HardenAsync</c> içindeki "sertlestirme EKSIK" uyarıları silinirse bu test düşer.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Sertlestirmenin_atlanan_maddesi_SEBEBIYLE_yazilir()
    {
        var kok = WordPressKokuKur();
        try
        {
            var kabuk = new SahteWpKabugu { BasarisizIceren = "DISALLOW_FILE_EDIT" };
            var gunlukcu = new KayitTutanGunlukcu<WordPressToolkitService>();
            var servis = Servis(kabuk, gunlukcu, kok);

            var sonuc = await servis.HardenAsync("ornek");

            // Madde sessizce listeden düştü — davranış korunuyor...
            Assert.DoesNotContain("file_edit", sonuc.Applied);
            Assert.Contains("salts", sonuc.Applied);
            // ...ama sebebi artık kayıtlı.
            Assert.True(
                gunlukcu.Icerir(LogLevel.Warning, "DISALLOW_FILE_EDIT", "ornek"),
                $"Eksik sertlestirme uyarisi yazilmadi. Yazilanlar:\n{gunlukcu.TumMetin}");
        }
        finally
        {
            Directory.Delete(kok, recursive: true);
        }
    }

    /// <summary>
    /// wp-cli HİÇ çalışmazsa iki madde de denenmemiş olur ve kullanıcı yine yeşil sonuç görür.
    /// <para>MUTASYON: <c>catch</c> bloğundaki uyarı silinirse bu test düşer.</para>
    /// </summary>
    [Fact]
    public async Task Sertlestirmede_wp_cli_hic_calismazsa_UYARI_yazilir()
    {
        var kok = WordPressKokuKur();
        try
        {
            var gunlukcu = new KayitTutanGunlukcu<WordPressToolkitService>();
            var servis = Servis(new SahteWpKabugu { Firlat = true }, gunlukcu, kok);

            var sonuc = await servis.HardenAsync("ornek");

            // mu-plugin önlemleri yine uygulandı; wp-cli maddeleri hiç denenemedi.
            Assert.Contains("hide_version", sonuc.Applied);
            Assert.DoesNotContain("salts", sonuc.Applied);
            Assert.True(
                gunlukcu.Icerir(LogLevel.Warning, "wp-cli adimlari CALISTIRILAMADI"),
                $"wp-cli calistirilamadi uyarisi yazilmadi. Yazilanlar:\n{gunlukcu.TumMetin}");
        }
        finally
        {
            Directory.Delete(kok, recursive: true);
        }
    }

    // --- Günlükçü isteğe bağlı ---

    /// <summary>
    /// Günlükçü İSTEĞE BAĞLI: <c>WordPressUpdateTests</c> bu servisi üç bağımlılıkla kuruyor.
    /// Zorunlu parametre o kurulumları kırardı; gerekçe <c>BackupService</c>'te yazılı.
    /// Günlükçüsüz hiçbir yol <c>NullReferenceException</c> atmamalı.
    /// </summary>
    [Fact]
    public async Task Gunlukcusuz_kurulum_calismaya_devam_eder()
    {
        var servis = new WordPressToolkitService(new SahteWpSiteServisi(string.Empty), new KullanilmayanVeritabani(), new SahteWpKabugu { HepsiBasarisiz = true });

        var sonuc = await servis.GetUpdatesAsync("ornek");

        Assert.False(sonuc.Measured);
    }

    // --- düzenek ---

    private static WordPressToolkitService Servis(
        SahteWpKabugu kabuk,
        KayitTutanGunlukcu<WordPressToolkitService> gunlukcu,
        string kok = "") =>
        new(new SahteWpSiteServisi(kok), new KullanilmayanVeritabani(), kabuk, gunlukcu);

    /// <summary>En küçük "bu bir WordPress" kanıtı: kökte wp-config.php.</summary>
    private static string WordPressKokuKur()
    {
        var kok = Path.Combine(Path.GetTempPath(), "ada-wp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(kok);
        File.WriteAllText(Path.Combine(kok, "wp-config.php"), "<?php define('DB_NAME','wp'); ?>");
        return kok;
    }

    private sealed class SahteWpKabugu : ISiteHostingSettingsService
    {
        public List<string> Komutlar { get; } = [];
        public string Cikti { get; set; } = "[]";
        public string Hata { get; set; } = "wp-cli hata";
        public string? BasarisizIceren { get; set; }
        public bool HepsiBasarisiz { get; set; }

        /// <summary>wp-cli/PHP HİÇ çalıştırılamadığı durum (çalıştırıcı istisnası).</summary>
        public bool Firlat { get; set; }

        public Task<ComposerRunResultDto> RunWpCliCommandAsync(string siteName, string command, CancellationToken cancellationToken = default)
        {
            Komutlar.Add(command);
            if (Firlat)
            {
                throw new InvalidOperationException("wp-cli bulunamadi");
            }

            var basarisiz = HepsiBasarisiz ||
                            (BasarisizIceren is { Length: > 0 } parca && command.Contains(parca, StringComparison.Ordinal));

            return Task.FromResult(new ComposerRunResultDto
            {
                ExitCode = basarisiz ? 1 : 0,
                Output = basarisiz ? string.Empty : (command.StartsWith("core version", StringComparison.Ordinal) ? "6.4" : Cikti),
                Error = basarisiz ? Hata : string.Empty
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

    private sealed class SahteWpSiteServisi(string kok) : IIisSiteService
    {
        public Task<SiteDto> GetSiteAsync(string siteName, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SiteDto { Name = siteName, PhysicalPath = kok, ApplicationRootPath = kok });

        public Task<IReadOnlyCollection<SiteDto>> GetSitesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDto> CreateSiteAsync(CreateSiteRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDto> UpdatePhysicalPathAsync(string siteName, string physicalPath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteSiteAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDto> StartSiteAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDto> StopSiteAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDto> AddBindingAsync(string siteName, SiteBindingRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDto> RemoveBindingAsync(string siteName, RemoveSiteBindingRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<SiteDto>> ImportExistingSitesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class KullanilmayanVeritabani : IDatabaseService
    {
        public Task<IReadOnlyCollection<DatabaseEngineStatusDto>> GetEnginesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DatabaseEngineStatusDto> RegisterCredentialsAsync(RegisterDatabaseCredentialsRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DatabaseWorkspaceDto> GetWorkspaceAsync(string engine, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DatabaseWorkspaceDto> CreateDatabaseAsync(CreateDatabaseRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DatabaseWorkspaceDto> ImportDatabaseAsync(ImportDatabaseRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DatabaseWorkspaceDto> CreateUserAsync(CreateDatabaseUserRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DatabaseWorkspaceDto> SetUserPasswordAsync(SetDatabaseUserPasswordRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DatabaseWorkspaceDto> GrantAccessAsync(GrantDatabaseAccessRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteDatabaseAsync(string engine, string name, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteUserAsync(string engine, string userName, string host, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DatabaseDumpResultDto> ExportDumpAsync(DatabaseDumpRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DatabaseDumpResultDto> ImportDumpAsync(DatabaseDumpRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DatabaseTransferResultDto> TransferDatabaseAsync(DatabaseTransferRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DatabaseDetailDto> GetDetailAsync(string engine, string databaseName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DatabaseCheckResultDto> CheckAndRepairAsync(string engine, string databaseName, IReadOnlyCollection<string> repairTables, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AdaPanel.Core.Dtos.Databases.DatabaseHardeningReport> HardenAdminAccountsAsync(string engine, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
