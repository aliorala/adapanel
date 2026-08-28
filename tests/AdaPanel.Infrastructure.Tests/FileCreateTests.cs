using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Files;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Files;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// "YENİ DOSYA" AKIŞININ VERİ KAYBINA DÖNÜŞMEMESİ.
///
/// <para>
/// <b>Neden var — yaşanmış eksik:</b> panelde dosya oluşturma HİÇ YOKTU; kullanıcı sitesine
/// <c>.htaccess</c> koyamadığı için bildirdi. Arka uç aslında hazırdı (<c>PUT /files</c> var
/// olmayan dosyayı yazıyor), yalnız arayüzde düğme yoktu.
/// </para>
/// <para>
/// Ama düğmeyi o uca doğrudan bağlamak yeni bir tehlike açardı: aynı uç var olan dosyanın
/// ÜSTÜNE de yazar. Yani "yeni dosya" demek, adı tutan bir dosya varsa onu SIFIRLAMAK olurdu.
/// Arayüz listeye bakarak uyarıyor, ama o denetim istemcide: liste eskimişse ya da ikinci bir
/// oturum dosyayı aynı anda oluşturmuşsa uyarı hiç çalışmaz.
/// </para>
/// <para>
/// Çözüm <c>FailIfExists</c>: sunucu <c>FileMode.CreateNew</c> kullanır, yani "var mı?" sorusu
/// ile yaratma işletim sisteminde TEK işlemdir ve arada yarış kalmaz.
/// </para>
/// </summary>
public class FileCreateTests : IDisposable
{
    private readonly string kok;
    private readonly string siteKoku;
    private readonly FileService servis;

    public FileCreateTests()
    {
        kok = Path.Combine(Path.GetTempPath(), "adapanel-create-" + Guid.NewGuid().ToString("N"));
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
            // Temizlik başarısızlığı testin sonucunu DEĞİŞTİRMEZ.
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>Asıl işlev: olmayan dosya yaratılır — <c>.htaccess</c> gibi nokta dosyası dahil.</summary>
    [Fact]
    public async Task Olmayan_dosya_YARATILIR()
    {
        await servis.WriteAsync(new WriteFileRequest
        {
            Path = "sites/a.test/.htaccess",
            Content = "RewriteEngine On",
            FailIfExists = true
        });

        Assert.Equal("RewriteEngine On", File.ReadAllText(Path.Combine(siteKoku, ".htaccess")));
    }

    /// <summary>
    /// ASIL KORUMA: var olan dosyanın üstüne YAZILMAZ ve içeriği BOZULMAZ.
    /// </summary>
    /// <remarks>
    /// İçeriğin korunduğunu ayrıca ölçmek şart: yalnız istisnaya bakan bir test, dosyayı önce
    /// açıp SIFIRLAYAN sonra hata veren bir uygulamada da YEŞİL kalırdı — ki kullanıcının verisi
    /// tam olarak o an gitmiş olurdu.
    /// </remarks>
    [Fact]
    public async Task Var_olan_dosyanin_UZERINE_YAZILMAZ_ve_icerik_BOZULMAZ()
    {
        var yol = Path.Combine(siteKoku, ".htaccess");
        File.WriteAllText(yol, "ONEMLI KURALLAR");

        var hata = await Assert.ThrowsAsync<AppException>(() => servis.WriteAsync(new WriteFileRequest
        {
            Path = "sites/a.test/.htaccess",
            Content = "",
            FailIfExists = true
        }));

        Assert.Equal("files.already_exists", hata.Message);
        Assert.Equal("ONEMLI KURALLAR", File.ReadAllText(yol));
    }

    /// <summary>
    /// Aynı adda bir KLASÖR varsa da reddedilir — sessizce başarısız olmaz.
    /// </summary>
    /// <remarks>
    /// Bu ayrı bir yol: dosya yoktur, dolayısıyla "dosya var mı?" diye bakan bir uygulama
    /// yazmaya kalkar ve işletim sisteminden ham bir hata alır. Kullanıcının gördüğü mesajın
    /// "zaten var" olması, "beklenmeyen hata" olmasından çok daha kullanışlıdır.
    /// </remarks>
    [Fact]
    public async Task Ayni_adda_KLASOR_varsa_reddedilir()
    {
        Directory.CreateDirectory(Path.Combine(siteKoku, "yedek"));

        var hata = await Assert.ThrowsAsync<AppException>(() => servis.WriteAsync(new WriteFileRequest
        {
            Path = "sites/a.test/yedek",
            Content = "veri",
            FailIfExists = true
        }));

        Assert.Equal("files.already_exists", hata.Message);
        Assert.True(Directory.Exists(Path.Combine(siteKoku, "yedek")));
    }

    /// <summary>
    /// Bayrak YOKKEN üstüne yazma DEVAM ETMELİ: düzenleyicinin "kaydet" akışı aynı ucu kullanıyor
    /// ve orada üstüne yazmak İSTENEN davranıştır. Korumayı varsayılan yapmak kaydetmeyi bozardı.
    /// </summary>
    [Fact]
    public async Task Bayraksiz_cagri_ESKISI_GIBI_uzerine_yazar()
    {
        var yol = Path.Combine(siteKoku, "notlar.txt");
        File.WriteAllText(yol, "eski");

        await servis.WriteAsync(new WriteFileRequest
        {
            Path = "sites/a.test/notlar.txt",
            Content = "yeni"
        });

        Assert.Equal("yeni", File.ReadAllText(yol));
    }

    /// <summary>
    /// Koruma sanal alan denetiminin YERİNE GEÇMEZ: yeni uç da aynı kapıdan geçer.
    /// </summary>
    [Fact]
    public async Task Yeni_dosya_SANAL_ALANDAN_cikamaz()
    {
        await Assert.ThrowsAsync<AppException>(() => servis.WriteAsync(new WriteFileRequest
        {
            Path = "sites/a.test/../../disari.txt",
            Content = "kotu",
            FailIfExists = true
        }));

        Assert.False(File.Exists(Path.Combine(kok, "disari.txt")));
    }

    /// <summary>
    /// Kaynağa bakan nöbetçi: koruma <c>File.Exists</c> ile İKİ ADIMA bölünürse davranış
    /// testleri yine yeşil kalır (tek iş parçacığında fark yoktur), ama iki eşzamanlı istek
    /// arasındaki yarış geri gelir ve kullanıcının dosyası sıfırlanır.
    /// </summary>
    [Fact]
    public void Varlik_denetimi_ISLETIM_SISTEMINDE_tek_islem()
    {
        var kaynak = File.ReadAllText(Path.Combine(KaynakKoku(),
            "src", "AdaPanel.Infrastructure", "Files", "FileService.cs"));

        Assert.Contains("FileMode.CreateNew", kaynak);
    }

    private static string KaynakKoku()
    {
        var yol = AppContext.BaseDirectory;
        while (yol is not null && !File.Exists(Path.Combine(yol, "AdaPanel.sln")))
        {
            yol = Path.GetDirectoryName(yol);
        }

        Assert.NotNull(yol);
        return yol!;
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
