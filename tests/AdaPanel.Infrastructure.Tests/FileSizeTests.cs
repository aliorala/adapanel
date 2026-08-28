using System.Diagnostics;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Files;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// KLASÖR BOYUTU hesabının korumaları.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden gerçek dosya sistemi üzerinde:</b> burada korunan üç şeyin de yalnız çalışırken bir
/// karşılığı var. (1) Bağlantı (junction) takibi ancak gerçek bir bağlantı kurulunca ölçülebilir —
/// kaynağa bakıp "ReparsePoint yazıyor" demek, yürüyüşün başka bir kolunda takip edilip
/// edilmediğini söylemez. (2) Bütçe bir davranıştır: dolduğunda İSTİSNA ATMAYIP kısmi sonucu
/// "kısmi" diye damgalaması gerekir. (3) Sanal alan denetimi yol çözümlemesinden gelir, yani ancak
/// gerçek bir yol verilerek sınanır.
/// </para>
/// <para>
/// <b>Bütçe testleri neden <c>internal</c> aşırı yüklemeyi çağırır:</b> üretim bütçesi 200.000
/// ögedir; onu doldurmak için testin 200.000 dosya yaratması gerekirdi (dakikalar sürer, diski
/// yorar ve mekanizma hakkında tek bir ek şey kanıtlamaz). Sınanan şey MEKANİZMADIR — sabitlerin
/// değeri değil; sabitler ölçülmüş gerekçesiyle FileService'te yazılıdır.
/// </para>
/// </remarks>
public sealed class FileSizeTests : IDisposable
{
    private readonly string kok;
    private readonly string siteKoku;
    private readonly FileService servis;

    public FileSizeTests()
    {
        kok = Path.Combine(Path.GetTempPath(), "adapanel-size-" + Guid.NewGuid().ToString("N"));
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
            // Bağlantılar ÖNCE kaldırılır: özyinelemeli silme bir junction'ı takip ederse testin
            // kendisi, korumaya çalıştığı veri kaybını üretirdi.
            foreach (var dizin in Directory.EnumerateDirectories(kok, "*", SearchOption.AllDirectories).Reverse())
            {
                if (new DirectoryInfo(dizin).Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    Directory.Delete(dizin);
                }
            }

            Directory.Delete(kok, recursive: true);
        }
        catch
        {
            // Geçici klasör temizliği en iyi çaba; testin sonucunu DEĞİŞTİRMEZ.
        }
    }

    // ------------------------------------------------------------------
    // ASIL İŞ: TOPLAM VE SAYILAR
    // ------------------------------------------------------------------

    /// <summary>
    /// İç içe klasörlerin toplamı DOĞRU: bilinen boyutlarda dosyalar yazılır, beklenen elle
    /// toplanır.
    /// </summary>
    /// <remarks>
    /// Boyutlar bilerek FARKLI: hepsi eşit olsaydı, bir dosyayı iki kez sayan ya da birini atlayan
    /// bir yürüyüş de doğru toplamı verebilirdi (çarpım/bölüm hataları görünmezdi).
    /// </remarks>
    [Fact]
    public async Task Ic_ice_klasorlerin_TOPLAMI_dogru()
    {
        DosyaYaz(Path.Combine(siteKoku, "olcum", "ust.bin"), 1000);
        DosyaYaz(Path.Combine(siteKoku, "olcum", "alt", "orta.bin"), 200);
        DosyaYaz(Path.Combine(siteKoku, "olcum", "alt", "daha-alt", "en-ic.bin"), 31);

        // Kapsam DIŞINDA kalması gereken bir komşu: hesap istenen klasörün ALT AĞACINI sayar,
        // sitenin tamamını değil. Bu dosya toplama girerse "hangi klasör diski yiyor" sorusunun
        // cevabı anlamsızlaşır.
        DosyaYaz(Path.Combine(siteKoku, "komsu.bin"), 5_000_000);

        var sonuc = await servis.CalculateSizeAsync("sites/a.test/olcum");

        Assert.Equal(1231, sonuc.TotalBytes);
        Assert.False(sonuc.Truncated);
        Assert.Equal(string.Empty, sonuc.TruncatedReasonKey);
        Assert.Equal("sites/a.test/olcum", sonuc.Path);
    }

    /// <summary>Dosya ve klasör SAYILARI doğru; kökün kendisi klasör sayısına GİRMEZ.</summary>
    [Fact]
    public async Task Dosya_ve_klasor_SAYILARI_dogru()
    {
        DosyaYaz(Path.Combine(siteKoku, "olcum", "a.bin"), 10);
        DosyaYaz(Path.Combine(siteKoku, "olcum", "b.bin"), 10);
        DosyaYaz(Path.Combine(siteKoku, "olcum", "alt", "c.bin"), 10);
        DosyaYaz(Path.Combine(siteKoku, "olcum", "alt", "daha-alt", "d.bin"), 10);
        Directory.CreateDirectory(Path.Combine(siteKoku, "olcum", "bos-alt"));

        var sonuc = await servis.CalculateSizeAsync("sites/a.test/olcum");

        Assert.Equal(4, sonuc.FileCount);

        // alt + alt/daha-alt + bos-alt = 3. Kökün kendisi sayılsaydı kullanıcı, içinde tek klasör
        // olmayan bir klasör için "1 klasör" okurdu.
        Assert.Equal(3, sonuc.DirectoryCount);
        Assert.Equal(40, sonuc.TotalBytes);
    }

    /// <summary>Boş klasör 0 döner ve ÇÖKMEZ.</summary>
    /// <remarks>
    /// En sık karşılaşılacak sınır durum: yeni açılmış bir klasör. Sıfır uzunlukta bir sıralamada
    /// patlayan bir yürüyüş, ucun ilk kullanımında hata verirdi.
    /// </remarks>
    [Fact]
    public async Task Bos_klasor_SIFIR_doner_cokmez()
    {
        Directory.CreateDirectory(Path.Combine(siteKoku, "bos"));

        var sonuc = await servis.CalculateSizeAsync("sites/a.test/bos");

        Assert.Equal(0, sonuc.TotalBytes);
        Assert.Equal(0, sonuc.FileCount);
        Assert.Equal(0, sonuc.DirectoryCount);
        Assert.False(sonuc.Truncated);
    }

    /// <summary>
    /// SİTE KÖKÜ hesaplanabilir: "sitem ne kadar yer kaplıyor" bu ucun asıl sorusudur.
    /// </summary>
    /// <remarks>
    /// Kök koruması (<c>EnsureNotSiteRoot</c>) kökü DEĞİŞTİREN işlemler içindir. Okuma yapan bu
    /// hesaba da uygulansaydı, uç en çok kullanılacağı yerde reddederdi.
    /// </remarks>
    [Fact]
    public async Task SITE_KOKUNUN_boyutu_hesaplanabilir()
    {
        DosyaYaz(Path.Combine(siteKoku, "index.php"), 123);
        DosyaYaz(Path.Combine(siteKoku, "alt", "stil.css"), 77);

        var sonuc = await servis.CalculateSizeAsync("sites/a.test");

        Assert.Equal(200, sonuc.TotalBytes);
        Assert.Equal(2, sonuc.FileCount);
    }

    /// <summary>Var olmayan klasör 404 anlamına gelen anahtarla reddedilir.</summary>
    [Fact]
    public async Task Olmayan_klasor_NOT_FOUND()
    {
        var hata = await Assert.ThrowsAsync<AppException>(() =>
            servis.CalculateSizeAsync("sites/a.test/hic-yok"));

        Assert.Equal("files.not_found", hata.MessageKey);
    }

    // ------------------------------------------------------------------
    // SANAL ALAN
    // ------------------------------------------------------------------

    /// <summary>
    /// Sanal alan DIŞI yol REDDEDİLİR.
    /// </summary>
    /// <remarks>
    /// Hesap "yalnız okuyor" diye zararsız değildir: sanal alandan çıkabilen bir boyut ucu,
    /// kiracıya komşusunun sitesinin (ya da panelin veri klasörünün) ne kadar yer kapladığını ve
    /// kaç dosyası olduğunu ölçtürür — dosya adı görmeden bile bu bir bilgi sızıntısıdır.
    /// </remarks>
    [Fact]
    public async Task Sanal_alan_disi_yol_REDDEDILIR()
    {
        var disari = Path.Combine(kok, "disari");
        DosyaYaz(Path.Combine(disari, "gizli.bin"), 4096);

        var hata = await Assert.ThrowsAsync<AppException>(() =>
            servis.CalculateSizeAsync("sites/a.test/../disari"));

        Assert.Equal("files.path_outside_root", hata.MessageKey);
    }

    // ------------------------------------------------------------------
    // BAĞLANTI (junction)
    // ------------------------------------------------------------------

    /// <summary>
    /// Klasörün İÇİNDEKİ bağlantı TAKİP EDİLMEZ: hedefindeki baytlar toplama GİRMEZ.
    /// </summary>
    /// <remarks>
    /// Takip edilseydi iki felaket birden: kiracı, sanal alanın dışındaki (komşu site, panel veri
    /// klasörü) içeriğin boyutunu ölçtürebilirdi; kendi üst ağacını gösteren bir bağlantıda ise
    /// yürüyüş sonsuza kadar aynı ağacı sayardı (bütçe onu durdurur ama sonuç anlamsız olurdu).
    /// </remarks>
    [Fact]
    public void Klasorun_ICINDEKI_baglanti_TAKIP_EDILMEZ()
    {
        var disari = Path.Combine(kok, "gercek-site");
        DosyaYaz(Path.Combine(disari, "buyuk.bin"), 1_000_000);

        DosyaYaz(Path.Combine(siteKoku, "olcum", "kendi.bin"), 500);
        var baglanti = Path.Combine(siteKoku, "olcum", "baglanti");
        JunctionOlustur(baglanti, disari);
        Assert.True(Directory.Exists(baglanti), "Junction kurulamadi; test onkosulu saglanmadi.");

        var sonuc = servis.CalculateSizeAsync("sites/a.test/olcum").GetAwaiter().GetResult();

        // ASIL KORUMA: bağlantının ardındaki 1.000.000 bayt SAYILMADI.
        Assert.Equal(500, sonuc.TotalBytes);
        Assert.Equal(1, sonuc.FileCount);

        // Bağlantı bir klasör olarak da sayılmaz: panelin dosya görüşünde bağlantılar hiçbir yerde
        // yoktur (listeleme/arama/kopyalama), sayı da o görüşle TUTARLI olmak zorunda.
        Assert.Equal(0, sonuc.DirectoryCount);
    }

    // ------------------------------------------------------------------
    // BÜTÇE
    // ------------------------------------------------------------------

    /// <summary>
    /// ÖGE bütçesi dolunca İSTİSNA ATILMAZ: o ana kadarki toplam döner ve "kısmi" damgası yer.
    /// </summary>
    /// <remarks>
    /// İki ayrı yanlış birden kapatılır. (1) İstisna atmak, kullanıcıya hiçbir şey vermemek olurdu —
    /// alt sınır bile "bu klasör EN AZ şu kadar" sorusunu cevaplar. (2) Damgasız dönmek daha
    /// kötüsüdür: kullanıcı kısmi bir sayıya bakıp yanlış klasörü siler.
    /// </remarks>
    [Fact]
    public async Task Oge_butcesi_dolunca_TRUNCATED_doner_ISTISNA_ATMAZ()
    {
        for (var i = 0; i < 40; i++)
        {
            DosyaYaz(Path.Combine(siteKoku, "cok", $"d{i}.bin"), 100);
        }

        var sonuc = await servis.CalculateSizeAsync("sites/a.test/cok", ogeButcesi: 10, sureButcesiMs: 60_000, CancellationToken.None);

        Assert.True(sonuc.Truncated);
        Assert.Equal("files.size_truncated_entries", sonuc.TruncatedReasonKey);

        // Kısmi ama GERÇEK: bütçe kadar öge sayılmış olmalı, ne fazlası ne de "hiç".
        Assert.Equal(10, sonuc.FileCount);
        Assert.Equal(1000, sonuc.TotalBytes);
    }

    /// <summary>SÜRE bütçesi dolunca da aynı davranış: kısmi sonuç + kendi sebep anahtarı.</summary>
    /// <remarks>
    /// Ayrı bir bütçe olmasının sebebi ölçülebilir: öge bütçesi işin MİKTARINI sınırlar, süresini
    /// değil. Ağ paylaşımına bakan bir site kökünde aynı öge sayısı dakikalar sürebilir. Sebep
    /// anahtarının ayrı olması da bilinçli — kullanıcıya "çok fazla dosya var" ile "disk çok yavaş"
    /// aynı cümleyle söylenemez.
    /// </remarks>
    [Fact]
    public async Task Sure_butcesi_dolunca_TRUNCATED_doner()
    {
        DosyaYaz(Path.Combine(siteKoku, "cok", "a.bin"), 100);
        DosyaYaz(Path.Combine(siteKoku, "cok", "b.bin"), 100);

        // 0 ms: ilk ögeden önce bütçe zaten dolmuştur — sınır durumu, "geçen sürenin ölçülmediği"
        // bir uygulamayı da yakalar.
        var sonuc = await servis.CalculateSizeAsync("sites/a.test/cok", ogeButcesi: 1_000_000, sureButcesiMs: 0, CancellationToken.None);

        Assert.True(sonuc.Truncated);
        Assert.Equal("files.size_truncated_time", sonuc.TruncatedReasonKey);
    }

    /// <summary>
    /// İptal edilen istek YÜRÜMEYE DEVAM ETMEZ.
    /// </summary>
    /// <remarks>
    /// Bütçeden farkı: iptalde cevabı bekleyen kimse kalmamıştır (kullanıcı sekmeyi kapattı),
    /// dolayısıyla kısmi sonuç üretmenin bir anlamı yoktur — asıl mesele işi BIRAKMAKTIR.
    /// </remarks>
    [Fact]
    public async Task Iptal_edilen_istek_YURUYUSU_BIRAKIR()
    {
        DosyaYaz(Path.Combine(siteKoku, "cok", "a.bin"), 100);
        using var kaynak = new CancellationTokenSource();
        await kaynak.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            servis.CalculateSizeAsync("sites/a.test/cok", kaynak.Token));
    }

    // ------------------------------------------------------------------
    // Yardımcılar
    // ------------------------------------------------------------------

    /// <summary>Verilen UZUNLUKTA dosya yazar (içerik önemsiz, ölçülen tek şey boyut).</summary>
    private static void DosyaYaz(string yol, int uzunluk)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(yol)!);
        File.WriteAllBytes(yol, new byte[uzunluk]);
    }

    private static void JunctionOlustur(string link, string hedef)
    {
        var betik = $"New-Item -ItemType Junction -Path '{link.Replace("'", "''")}' -Target '{hedef.Replace("'", "''")}' | Out-Null";
        var yol = Path.Combine(Path.GetTempPath(), $"ada-size-{Guid.NewGuid():N}.ps1");
        File.WriteAllText(yol, "$ErrorActionPreference = 'Stop'\n" + betik);
        try
        {
            using var surec = Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{yol}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            })!;

            surec.WaitForExit(60_000);
        }
        finally
        {
            File.Delete(yol);
        }
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
