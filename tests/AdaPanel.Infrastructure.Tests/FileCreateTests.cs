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

    // ------------------------------------------------------------------
    // YÜKLEME — SALDIRGANIN BELİRLEDİĞİ İKİNCİ DEĞER: DOSYA ADI
    // ------------------------------------------------------------------

    /// <summary>
    /// Yükleme, dosya ADI üzerinden sanal alandan çıkamaz.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Neden bu ayrı bir risk:</b> denetleyici kapsam denetimini (<c>EnsureFilesPathAsync</c>)
    /// yalnız <c>path</c> sorgu parametresine uygular; diske yazılan yol ise
    /// <c>path + "/" + file.FileName</c>'dir (FilesController.UploadAsync). Yani hedefin SON
    /// parçasını, kapsam kapısının HİÇ görmediği bir değer — çok parçalı istekteki dosya adı —
    /// belirler. Bu, bu depodaki tekrarlayan kalıbın ta kendisidir: denetim var, ama denetlenen
    /// değer ile KULLANILAN değer aynı değil.
    /// </para>
    /// <para>
    /// Kapı ikinci sette (<c>ResolveSandboxTarget</c>) tutuyor; ÖLÇÜLMEDİĞİ sürece tuttuğunu
    /// bilemeyiz — bu test tam olarak o ölçümdür.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Yuklemede_DOSYA_ADI_sanal_alandan_cikaramaz()
    {
        using var akis = new MemoryStream("<?php system($_GET[0]);"u8.ToArray());

        // FilesController.UploadAsync'in ürettiği birleşim: path + "/" + istemcinin verdiği ad.
        await Assert.ThrowsAsync<AppException>(() => servis.SaveUploadAsync(
            "sites/a.test/../../shell.php", akis));

        Assert.False(File.Exists(Path.Combine(kok, "shell.php")));
    }

    /// <summary>
    /// Yükleme KOMŞU KİRACININ köküne yazamaz (aynı yol, farklı sonuç: veri sızması değil, veri
    /// EKME).
    /// </summary>
    [Fact]
    public async Task Yukleme_KOMSU_KIRACIYA_yazamaz()
    {
        using var akis = new MemoryStream("<?php"u8.ToArray());

        await Assert.ThrowsAsync<AppException>(() => servis.SaveUploadAsync(
            "sites/a.test/../b.test/shell.php", akis));

        Assert.False(File.Exists(Path.Combine(kok, "b.test", "shell.php")));
    }

    /// <summary>
    /// MEŞRU yükleme çalışmaya devam eder — Türkçe harfli ve boşluklu ad dahil.
    /// </summary>
    /// <remarks>
    /// Yukarıdaki iki testin daraltmasının meşru kullanımı kırmadığını ölçer. Bu ayrım olmadan
    /// "kapıyı sıkılaştırdım" cümlesi, panelin Türkçe dosya adlarını reddettiğini gizlerdi.
    /// </remarks>
    [Fact]
    public async Task Mesru_yukleme_TURKCE_ve_bosluklu_adla_calisir()
    {
        using var akis = new MemoryStream("veri"u8.ToArray());

        var yol = await servis.SaveUploadAsync("sites/a.test/belgeler/güncel rapor.txt", akis);

        Assert.Equal("sites/a.test/belgeler/güncel rapor.txt", yol);
        Assert.Equal("veri", File.ReadAllText(Path.Combine(siteKoku, "belgeler", "güncel rapor.txt")));
    }

    // ------------------------------------------------------------------
    // YARIM KALAN YAZMA — "zaten var" YALANININ KÖKÜ
    // ------------------------------------------------------------------

    /// <summary>
    /// YARIDA KESİLEN YAZMA DİSKTE KALINTI BIRAKMAZ.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>ÖLÇÜLEN ESKİ DAVRANIŞ</b> (bu makinede, gerçek dosya sistemine yazarak): iptal edilmiş bir
    /// jetonla yapılan çağrı <c>TaskCanceledException</c> atıyor ve geriye <b>3 baytlık yarım bir
    /// dosya</b> bırakıyordu. Geri sarma yoktu.
    /// </para>
    /// <para>
    /// <b>Neden bu tek başına bir hata değil, İKİ hatanın birleşimi:</b> kalıntı kaldığı için
    /// kullanıcının SONRAKİ denemesi "bu dosya zaten var" (409) ile reddedilir — hiç
    /// oluşturamadığı bir dosyanın kalıntısı yüzünden. Kullanıcı için bu, "yeni dosya" düğmesinin
    /// kalıcı olarak bozulmasıdır ve panelde çıkışı yoktur (dosyayı listede görüp silmesi gerekir,
    /// ama içeriği hiç yazılmadığı için ne olduğunu bilemez).
    /// </para>
    /// <para>
    /// İptal, "yazma yarıda kaldı" durumunun testte ÜRETİLEBİLEN biçimidir; gerçek hayattaki
    /// karşılıkları disk dolması ve antivirüs kilididir. Geri sarma üçünde de aynı yolda çalışır.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Yarida_kesilen_yazma_diskte_KALINTI_birakmaz()
    {
        using var iptal = new CancellationTokenSource();
        await iptal.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => servis.WriteAsync(
            new WriteFileRequest { Path = "sites/a.test/rapor.txt", Content = "icerik", FailIfExists = true },
            iptal.Token));

        Assert.False(
            File.Exists(Path.Combine(siteKoku, "rapor.txt")),
            "Yarim yazma diskte KALINTI birakti: sonraki deneme 'zaten var' ile reddedilir.");
    }

    /// <summary>
    /// YARIM YAZMADAN SONRA AYNI DOSYA YENİDEN OLUŞTURULABİLİR — "zaten var" YALANI biter.
    /// </summary>
    /// <remarks>
    /// Yalnız "kalıntı yok" demek yetmez: ölçülmesi gereken, kullanıcının ASIL yaşadığı sonuçtur.
    /// Bu test kalıntının varlığını değil, kalıntının ÜRETTİĞİ arızayı ölçer — ve içeriği de
    /// doğrular, çünkü "dosya oluştu ama boş" da aynı ölçüde bozuk bir sonuçtur.
    /// </remarks>
    [Fact]
    public async Task Yarim_yazmadan_sonra_dosya_YENIDEN_olusturulabilir()
    {
        using var iptal = new CancellationTokenSource();
        await iptal.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => servis.WriteAsync(
            new WriteFileRequest { Path = "sites/a.test/.htaccess", Content = "RewriteEngine On", FailIfExists = true },
            iptal.Token));

        await servis.WriteAsync(new WriteFileRequest
        {
            Path = "sites/a.test/.htaccess",
            Content = "RewriteEngine On",
            FailIfExists = true
        });

        Assert.Equal("RewriteEngine On", File.ReadAllText(Path.Combine(siteKoku, ".htaccess")));
    }

    /// <summary>
    /// MUTASYON KONTROLÜ: geri sarma, BAŞARILI yazmayı silmez.
    /// </summary>
    /// <remarks>
    /// Bu olmadan "her yazmadan sonra dosyayı sil" gibi bir değişiklik de yukarıdaki iki testi
    /// geçerdi; yani testler korumanın çalıştığını değil, yalnızca dosyanın yokluğunu ölçerdi.
    /// </remarks>
    [Fact]
    public async Task Geri_sarma_BASARILI_yazmaya_dokunmaz()
    {
        await servis.WriteAsync(new WriteFileRequest
        {
            Path = "sites/a.test/web.config",
            Content = "<configuration/>",
            FailIfExists = true
        });

        Assert.Equal("<configuration/>", File.ReadAllText(Path.Combine(siteKoku, "web.config")));
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

    /// <summary>
    /// KAYNAĞA BAKAN NÖBETÇİ: "zaten var" süzgeci YAZMA adımını KAPSAMAZ.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Neden yapısal:</b> düzeltilen hatanın davranışsal karşılığını birim testinde ÜRETMEK
    /// mümkün değil — yazma sırasında IOException almak için diskin dolması ya da antivirüsün
    /// dosyayı kilitlemesi gerekir; ikisi de testte deterministik olarak kurulamaz (denendi).
    /// Oysa hatanın kendisi tümüyle yapısaldır: <c>try</c> hem <c>CreateNew</c>'i hem yazmayı
    /// kapsadığı sürece <c>when (File.Exists(...))</c> süzgeci HER ZAMAN doğrudur — çünkü dosyayı
    /// o noktada zaten biz yaratmışızdır — ve yazma hatası "bu dosya zaten var" diye maskelenir.
    /// </para>
    /// <para>
    /// Bu yüzden ölçülen şey tam olarak o yapıdır: <c>CreateNew</c> ile başlayan ve "zaten var"
    /// diyen <c>catch</c>'e kadar süren blokta YAZMA olmamalı. İki adım birleştirilirse test
    /// kızarır (mutasyonla doğrulandı).
    /// </para>
    /// </remarks>
    [Fact]
    public void Zaten_var_YALNIZ_yaratma_adiminda_soylenir()
    {
        var kaynak = File.ReadAllText(Path.Combine(KaynakKoku(),
            "src", "AdaPanel.Infrastructure", "Files", "FileService.cs"));

        // ÇAPA KOD OLMALI, YORUM DEĞİL. İlk hâlde çapa "FileMode.CreateNew" idi ve o dizge
        // metodun BELGESİNDE de geçiyordu: test, kodun yerine yorum metnini ölçüyor ve mutasyonda
        // KIZARMIYORDU (ölçüldü). Atama ifadesi yalnız kodda geçer.
        var yaratmaBaslangici = kaynak.IndexOf("akis = new FileStream(", StringComparison.Ordinal);
        Assert.True(yaratmaBaslangici > 0, "Yaratma adiminin kendisi bulunamadi.");

        var ilkYakalama = kaynak.IndexOf("catch (IOException)", yaratmaBaslangici, StringComparison.Ordinal);
        Assert.True(ilkYakalama > yaratmaBaslangici, "Yaratmadan sonra IOException yakalamasi bulunamadi.");

        var korunanBlok = kaynak[yaratmaBaslangici..ilkYakalama];

        // Yazma bu blokta OLMAMALI: olduğu anda süzgeç koşulsuzlaşır ve gerçek sebep kaybolur.
        Assert.DoesNotContain("yazici.WriteAsync(", korunanBlok, StringComparison.Ordinal);
        Assert.DoesNotContain("StreamWriter", korunanBlok, StringComparison.Ordinal);

        // Ve geri sarma GERÇEKTEN var: yazma patlarsa yarattığımız dosya silinir.
        Assert.Contains("File.Delete(target.PhysicalPath);", kaynak, StringComparison.Ordinal);
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
