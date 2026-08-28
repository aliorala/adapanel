using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Files;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Files;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// WINDOWS AYRILMIŞ AYGIT ADLARININ (<c>CON</c>, <c>NUL</c>, <c>COM1</c>...) dosya yöneticisindeki
/// karşılığı.
/// </summary>
/// <remarks>
/// <para>
/// <b>DENETİM RAPORUNUN İDDİASI DOĞRULANMADI — ölçüm burada yazılı.</b> Rapor "aygıt adı 500
/// <c>common.unexpected_error</c> üretiyor" diyordu. Bu makinede (Windows 10 19045, .NET 8) gerçek
/// çağrılarla ölçüldü: uç 500 DEĞİL, <c>files.path_outside_root</c> (400) döndürüyordu. Sebep
/// <c>Path.GetFullPath(&lt;klasör&gt;\NUL)</c>'un <c>\\.\NUL</c> döndürmesi — yol sanal alanın
/// dışına çıkıyor ve mevcut kapı onu zaten eliyor. Aynısı <c>CON</c>, <c>COM1</c>, <c>LPT9</c>,
/// <c>AUX</c>, <c>PRN</c>, <c>con.json</c>, <c>NUL.txt</c> ve listede olmayan <c>CONIN$</c> için de
/// ölçüldü.
/// </para>
/// <para>
/// <b>O hâlde bu testler neyi koruyor:</b> düzeltilen şey çökme değil YALAN. Kendi klasöründe "NUL"
/// adlı klasör açmaya çalışan kullanıcıya "yol kök dizinin dışında" demek, onu var olmayan bir
/// yol/izin sorununu aramaya gönderir. Üstelik koruma KAZAYDI — bir <c>GetFullPath</c> yan
/// etkisine dayanıyordu. Ham <c>FileStream(&lt;klasör&gt;\NUL, CreateNew)</c> bu makinede HİÇ
/// istisna atmıyor (ölçüldü), yani o kaza bir gün ortadan kalkarsa arkasında SESSİZ VERİ KAYBI
/// var: veri aygıta gider, dosya hiç oluşmaz.
/// </para>
/// </remarks>
public sealed class FileReservedNameTests : IDisposable
{
    private readonly string kok;
    private readonly string siteKoku;
    private readonly FileService servis;

    public FileReservedNameTests()
    {
        kok = Path.Combine(Path.GetTempPath(), "adapanel-ayrilmis-" + Guid.NewGuid().ToString("N"));
        siteKoku = Path.Combine(kok, "a.test");
        Directory.CreateDirectory(siteKoku);

        servis = new FileService(new SahteSiteServisi(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["a.test"] = siteKoku
        }));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(kok, recursive: true);
        }
        catch
        {
            // Geçici klasör temizliği en iyi çaba; testin sonucunu DEĞİŞTİRMEZ.
        }
    }

    /// <summary>
    /// AYGIT ADIYLA KLASÖR AÇMAK ANLAŞILIR BİR HATA VERİR: "geçersiz ad", "yol kök dışında" değil.
    /// </summary>
    /// <remarks>
    /// Anahtar ayrımı önemli: <c>files.path_outside_root</c> kullanıcıya "yanlış yerdesin" der ve
    /// yolunu düzeltmeye çalıştırır; oysa yol doğrudur, ADI kullanılamaz.
    /// </remarks>
    [Theory]
    [InlineData("NUL")]
    [InlineData("CON")]
    [InlineData("COM1")]
    [InlineData("LPT9")]
    [InlineData("AUX")]
    [InlineData("PRN")]
    public async Task Aygit_adiyla_KLASOR_acilamaz(string ad)
    {
        var hata = await Assert.ThrowsAsync<AppException>(() => servis.CreateDirectoryAsync(
            new CreateDirectoryRequest { Path = $"sites/a.test/{ad}" }));

        Assert.Equal("files.invalid_name", hata.MessageKey);
        Assert.Equal(400, hata.HttpStatusCode);
        Assert.False(Directory.Exists(Path.Combine(siteKoku, ad)));
    }

    /// <summary>
    /// UZANTI EKLEMEK AYGIT ADINI KURTARMAZ: <c>con.json</c> da aygıttır.
    /// </summary>
    /// <remarks>
    /// Ayrılmışlık NOKTADAN ÖNCEKİ parçaya bakar (gerekçesi <c>WindowsReservedNames</c>'te). Bu
    /// ayrı bir test çünkü "uzantı varsa sorun yok" en yaygın yanlış varsayımdır ve o varsayımla
    /// yazılmış bir denetim burada kızarır.
    /// </remarks>
    [Theory]
    [InlineData("con.json")]
    [InlineData("NUL.txt")]
    [InlineData("con.a.b")]
    public async Task Uzantili_aygit_adiyla_DOSYA_yazilamaz(string ad)
    {
        var hata = await Assert.ThrowsAsync<AppException>(() => servis.WriteAsync(
            new WriteFileRequest { Path = $"sites/a.test/{ad}", Content = "veri", FailIfExists = true }));

        Assert.Equal("files.invalid_name", hata.MessageKey);
    }

    /// <summary>YENİ AD alanı da aynı kapıdan geçer: kopyalamada <c>NewName</c> aygıt olamaz.</summary>
    [Fact]
    public async Task Yeni_AD_aygit_adi_olamaz()
    {
        DosyaYaz(Path.Combine(siteKoku, "rapor.txt"), "veri");
        Directory.CreateDirectory(Path.Combine(siteKoku, "hedef"));

        var hata = await Assert.ThrowsAsync<AppException>(() => servis.CopyAsync(new CopyFilesRequest
        {
            Paths = ["sites/a.test/rapor.txt"],
            DestinationPath = "sites/a.test/hedef",
            NewName = "NUL"
        }));

        Assert.Equal("files.invalid_name", hata.MessageKey);
        Assert.Empty(Directory.GetFiles(Path.Combine(siteKoku, "hedef")));
    }

    /// <summary>YOLUN ORTASINDAKİ aygıt adı da yakalanır — yalnız son parçaya bakan denetim eksiktir.</summary>
    [Fact]
    public async Task Yolun_ORTASINDAKI_aygit_adi_da_yakalanir()
    {
        var hata = await Assert.ThrowsAsync<AppException>(() => servis.WriteAsync(
            new WriteFileRequest { Path = "sites/a.test/CON/alt/rapor.txt", Content = "veri" }));

        Assert.Equal("files.invalid_name", hata.MessageKey);
    }

    // ------------------------------------------------------------------
    // MUTASYON KONTROLÜ: DENETİM MEŞRU ADLARI VURMUYOR
    // ------------------------------------------------------------------

    /// <summary>
    /// AYGIT ADINA BENZEYEN ama AYRILMIŞ OLMAYAN adlar ÇALIŞMAYA DEVAM EDER.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Bu olmadan "içinde CON/COM geçen her adı reddet" gibi kaba bir denetim de yukarıdaki
    /// testleri geçerdi — ve <c>config.php</c>, <c>components.json</c>, <c>console.log</c> gibi her
    /// sitede bulunan dosyaları erişilemez yapardı. ÖLÇÜLDÜ: <c>COM0</c>, <c>COM10</c> ve
    /// <c>CONSOLE</c> Windows'ta ayrılmış DEĞİL, gerçekten oluşturulabiliyor.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("com0")]
    [InlineData("COM10")]
    [InlineData("console.log")]
    [InlineData("config.php")]
    [InlineData("aux-veri.txt")]
    public async Task Aygita_BENZEYEN_mesru_adlar_calismaya_devam_eder(string ad)
    {
        await servis.WriteAsync(new WriteFileRequest
        {
            Path = $"sites/a.test/{ad}",
            Content = "veri",
            FailIfExists = true
        });

        Assert.Equal("veri", File.ReadAllText(Path.Combine(siteKoku, ad)));
    }

    /// <summary>
    /// DİSKTE GERÇEKTEN DURAN bir <c>CLOCK$</c> dosyası GÖRÜNÜR ve SİLİNEBİLİR kalır.
    /// </summary>
    /// <remarks>
    /// <para>
    /// KARARIN ÖLÇÜMÜ. <see cref="WindowsReservedNames"/> listesinde <c>CLOCK$</c> da var ama o ad
    /// bu makinede ayrılmış DEĞİL — <c>CLOCK$</c> adlı dosya gerçekten oluşturulabiliyor (ölçüldü;
    /// kaynağın kendi belgesi de bunu dürüstçe yazıyor). Koşulsuz bir denetim, diskte duran böyle
    /// bir dosyayı dosya yöneticisinden görünmez, indirilemez ve SİLİNEMEZ yapardı: kullanıcının
    /// verisine erişimini kapatmak, düzeltmeye çalıştığımız yanlış hata mesajından ağır olurdu.
    /// </para>
    /// <para>
    /// Bu yüzden denetimin muafiyeti VARLIK üzerinedir. Gerçek aygıt adları bu muafiyetten
    /// yararlanamaz, çünkü onlar için varlık sorgusu FALSE döner (ölçüldü:
    /// <c>File.Exists(&lt;klasör&gt;\NUL)</c> = false) — yukarıdaki testler tam da bunu kanıtlıyor.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Diskte_DURAN_CLOCK_dosyasi_erisilebilir_kalir()
    {
        // Panelin üzerinden değil, doğrudan diske yazılır: "geriye dönük artık" senaryosu.
        DosyaYaz(Path.Combine(siteKoku, "clock$.txt"), "eski veri");

        var calisma = await servis.GetWorkspaceAsync("sites/a.test/clock$.txt");
        Assert.Equal("eski veri", calisma.Content);

        await servis.DeleteAsync(new DeleteFileRequest { Path = "sites/a.test/clock$.txt" });
        Assert.False(File.Exists(Path.Combine(siteKoku, "clock$.txt")));
    }

    // --- düzenek ---

    private static void DosyaYaz(string yol, string icerik)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(yol)!);
        File.WriteAllText(yol, icerik);
    }

    /// <summary>Site adını fiziksel köke çeviren sahte; başka hiçbir IIS işlemi kullanılmaz.</summary>
    private sealed class SahteSiteServisi(IReadOnlyDictionary<string, string> kokler) : IIisSiteService
    {
        public Task<SiteDto> GetSiteAsync(string siteName, CancellationToken cancellationToken = default)
        {
            if (!kokler.TryGetValue(siteName, out var siteYolu))
            {
                throw new AppException("sites.not_found", 404, 404);
            }

            return Task.FromResult(new SiteDto
            {
                Name = siteName,
                PhysicalPath = siteYolu,
                ApplicationRootPath = siteYolu
            });
        }

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
}
