using System.Diagnostics;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Dtos.Users;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Security;
using AdaPanel.Infrastructure.Support;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// DİSK KOTASI hesabının ağaç yürüyüşü.
/// </summary>
/// <remarks>
/// <para>
/// <b>Kapatılan açık (ölçüldü):</b> <c>CustomerScopeService.DirectorySize</c>
/// <c>Directory.EnumerateFiles(kok, "*", SearchOption.AllDirectories)</c> kullanıyordu. O çağrı
/// bağlantıları (junction/symlink) TAKİP EDER ve bütçesi YOKTUR. Kiracı kendi sitesinin köküne
/// kendi üst ağacını gösteren bir kavşak koyduğunda kotayı hesaplayan istek dönmüyordu — üstelik
/// bu hesap her dosya yüklemesinde ve kullanıcı listesindeki her müşteri satırında çalışıyor, yani
/// tek kiracı bütün paneli yanıtsız bırakabiliyordu. Komşu bir siteyi gösteren kavşak ise kiracıya
/// komşusunun baytlarını saydırıyordu.
/// </para>
/// <para>
/// <b>Neden GERÇEK dosya sistemi ve GERÇEK <see cref="CustomerScopeService"/>:</b> korunan şeylerin
/// hiçbirinin kaynağa bakarak karşılığı yok. Bağlantı takibi ancak gerçek bir kavşak kurulunca
/// ölçülebilir; bütçe bir davranıştır (dolunca İSTİSNA ATMAMASI gerekir); bütçenin abonelik başına
/// mı site başına mı olduğu ancak birden çok siteli bir abonelikte görülür. Sahte bir kapsamla
/// yazılsaydı test, kuralın kendisini değil kendi taklidini doğrulardı.
/// </para>
/// </remarks>
public sealed class QuotaDirectoryWalkTests : IDisposable
{
    private static readonly Guid Musteri = Guid.Parse("44444444-4444-4444-4444-444444444444");

    /// <summary>
    /// Kendini gösteren kavşak testinin ÜST SINIRI. Testin KENDİSİ asılmamalı: asılan bir test
    /// paketini kimse koşturmaz, dolayısıyla koruma bozulduğunda kırmızı yerine "sonsuza kadar
    /// çalışıyor" görülürdü. İptal jetonu, yürüyüşün her klasör adımında sorulur — koruma bozulup
    /// yürüyüş döngüye girse bile istek burada BIRAKILIR ve test istisna alarak KIZARIR.
    /// </summary>
    private static readonly TimeSpan Tavan = TimeSpan.FromSeconds(20);

    private readonly string kok;
    private readonly string siteKoku;
    private readonly SahteSiteServisi siteler = new();
    private readonly CustomerScopeService kapsamlar;

    public QuotaDirectoryWalkTests()
    {
        kok = Path.Combine(Path.GetTempPath(), "adapanel-kota-" + Guid.NewGuid().ToString("N"));
        siteKoku = Path.Combine(kok, "a.test");
        Directory.CreateDirectory(siteKoku);
        siteler.Ekle("a.test", siteKoku);

        var dataRoot = Path.Combine(kok, "_veri");
        Directory.CreateDirectory(dataRoot);
        var store = new JsonFileStore(
            Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = dataRoot }));

        kapsamlar = new CustomerScopeService(store, new PlansizServis(), new TavansizServis(), siteler);
    }

    public void Dispose()
    {
        try
        {
            // Bağlantılar ÖNCE kaldırılır: özyinelemeli silme bir kavşağı takip ederse testin
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
    // ASIL AÇIK: DÖNGÜ
    // ------------------------------------------------------------------

    /// <summary>
    /// KOTA HESABI, SİTENİN KENDİNİ GÖSTEREN BİR KAVŞAĞINDA SONSUZA DÖNMEZ.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Kapatılan açık tam olarak budur: kiracı kendi site kökünün içine, yine kendi site kökünü
    /// gösteren bir kavşak koyar. Takip eden bir yürüyüş aynı ağacı sonsuza kadar (ya da yol
    /// uzunluğu sınırına kadar) gezer; kotayı hesaplayan HTTP isteği hiç dönmez.
    /// </para>
    /// <para>
    /// Testin iki ayrı ölçütü var ve ikisi de gerekli: (1) hesap <see cref="Tavan"/> içinde
    /// BİTMELİ — bu, döngü olmadığının kanıtı; (2) sonuç DOĞRU olmalı — çünkü döngüyü sadece
    /// bütçeyle durduran bir uygulama da "bitiyor" ama kesilmiş/anlamsız bir sayı verir.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Kota_hesabi_KENDINI_GOSTEREN_kavsakta_SONSUZA_DONMEZ()
    {
        DosyaYaz(Path.Combine(siteKoku, "icerik", "buyuk.bin"), 2 * 1024 * 1024);

        var kendine = Path.Combine(siteKoku, "kendine");
        JunctionOlustur(kendine, siteKoku);
        Assert.True(Directory.Exists(kendine), "Junction kurulamadi; test onkosulu saglanmadi.");
        Assert.True(
            new DirectoryInfo(kendine).Attributes.HasFlag(FileAttributes.ReparsePoint),
            "Kurulan girdi kavsak degil; test onkosulu saglanmadi.");

        await KapsamKurAsync("a.test");

        using var kaynak = new CancellationTokenSource(Tavan);
        var kullanim = await kapsamlar.GetDiskUsageMbAsync(Musteri, kaynak.Token);

        // Döngüye giren bir uygulama buraya HİÇ gelmez (jeton yukarıdaki tavanda iptal eder ve
        // GetDiskUsageMbAsync iptali artık YUTMAZ, yükseltir).
        Assert.False(kaynak.IsCancellationRequested, "Kota hesabi tavani asti: yuruyus kavsagi TAKIP EDIYOR.");

        // Ve sayı kesilmiş değil, gerçek: yalnız kendi 2 MB'ı.
        Assert.Equal(2, kullanim.UsageMb);

        // SAĞLIKLI ölçüm "eksik" DAMGALANMAZ. Bayrağı koşulsuz false döndüren bir uygulama da
        // eksikliği bildiren testleri geçerdi; o hâlde panel her satıra hak edilmemiş bir uyarı
        // asar ve uyarı, gerçekten eksik olan satırlarda anlamını yitirirdi.
        Assert.True(kullanim.Measured);
        Assert.Equal(string.Empty, kullanim.TruncatedReasonKey);
    }

    /// <summary>
    /// Kota, kavşağın gösterdiği HEDEFİ toplama KATMAZ: komşu sitenin baytları sayılmaz.
    /// </summary>
    /// <remarks>
    /// Takip edilseydi kiracı, kendi sitesine komşu bir siteyi (ya da panelin veri klasörünü)
    /// gösteren bir kavşak koyarak kendi kotasını komşusunun içeriğiyle DOLDURABİLİRDİ — kendi
    /// yükleme hakkını kaybetmesi bir yana, komşunun disk kullanımını da ölçmüş olurdu.
    /// </remarks>
    [Fact]
    public async Task Kota_kavsagin_HEDEFINI_toplama_KATMAZ()
    {
        var komsu = Path.Combine(kok, "komsu.test");
        DosyaYaz(Path.Combine(komsu, "komsunun-dosyasi.bin"), 8 * 1024 * 1024);

        DosyaYaz(Path.Combine(siteKoku, "kendi.bin"), 2 * 1024 * 1024);
        var baglanti = Path.Combine(siteKoku, "komsuya");
        JunctionOlustur(baglanti, komsu);
        Assert.True(Directory.Exists(baglanti), "Junction kurulamadi; test onkosulu saglanmadi.");

        await KapsamKurAsync("a.test");

        using var kaynak = new CancellationTokenSource(Tavan);
        var kullanim = await kapsamlar.GetDiskUsageMbAsync(Musteri, kaynak.Token);

        // 10 değil 2: komşunun 8 MB'ı SAYILMADI.
        Assert.Equal(2, kullanim.UsageMb);

        // Atlanan BAĞLANTI bir eksiklik DEĞİLDİR (gerekçesi SafeDirectoryWalk'ın eleme satırında:
        // bağlantılar panelin dosya görüşünde hiçbir yerde yok, dolayısıyla toplama girmemeleri
        // eksiklik değil TUTARLILIKTIR). Bunu "ölçemedim" saymak, kavşak içeren her siteyi kalıcı
        // olarak "eksik" damgalardı.
        Assert.True(kullanim.Measured);
    }

    // ------------------------------------------------------------------
    // BÜTÇE
    // ------------------------------------------------------------------

    /// <summary>
    /// ÖGE bütçesi dolunca kota hesabı İSTİSNA ATMAZ; o ana kadarki toplamı KESİLDİĞİNİ SÖYLEYEREK
    /// döndürür.
    /// </summary>
    /// <remarks>
    /// <para>
    /// İstisna atmak, kullanıcı listesinden dosya yüklemeye kadar bu hesabı çağıran HER ekranı
    /// çökertirdi. Kesilmiş toplamın kotada ne anlama geldiği (ALT SINIR olarak kullanılır, işlem
    /// bloke edilmez) <c>GetDiskUsageMbAsync</c> içinde gerekçesiyle yazılıdır.
    /// </para>
    /// <para>
    /// <b>Kesilme bilgisinin DÖNDÜĞÜNÜ ölçmek şart:</b> bu bilgi kaynakta (ortak yürüyüşün
    /// sonucunda) hep VARDI; kaybolduğu yer tam olarak burasıydı — metot düz bir sayı döndürüyor,
    /// "ölçüldü mü" yolda düşüyordu. Sayıyı doğrulayıp bayrağı doğrulamayan bir test, aynı kaybı
    /// yeniden görmezdi.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Oge_butcesi_dolunca_ISTISNA_ATMAZ_ama_KESILDIGINI_SOYLER()
    {
        for (var i = 0; i < 4; i++)
        {
            DosyaYaz(Path.Combine(siteKoku, $"d{i}.bin"), 2 * 1024 * 1024);
        }

        await KapsamKurAsync("a.test");

        var kullanim = await kapsamlar.GetDiskUsageMbAsync(Musteri, ogeButcesi: 2, sureButcesiMs: 60_000, CancellationToken.None);

        // Kısmi ama GERÇEK: bütçe kadar öge sayılmış olmalı (2 × 2 MB), ne fazlası ne de "hiç".
        Assert.Equal(4, kullanim.UsageMb);

        // ...ve kısmi olduğunu SÖYLER. Sebep anahtarı yürüyüşün kendi anahtarıdır (çevirisi tr/en'de
        // VAR); yeni bir anahtar uydurulsaydı kullanıcı ekranda ham anahtar görürdü.
        Assert.False(kullanim.Measured);
        Assert.Equal("files.size_truncated_entries", kullanim.TruncatedReasonKey);
    }

    /// <summary>SÜRE bütçesi dolunca da aynı davranış: istisna yok, kısmi toplam, "kesildi" damgası.</summary>
    [Fact]
    public async Task Sure_butcesi_dolunca_ISTISNA_ATMAZ_ama_KESILDIGINI_SOYLER()
    {
        DosyaYaz(Path.Combine(siteKoku, "a.bin"), 2 * 1024 * 1024);
        await KapsamKurAsync("a.test");

        // 0 ms: ilk ögeden önce bütçe zaten dolmuştur — "geçen sürenin hiç ölçülmediği" bir
        // uygulamayı da yakalayan sınır durum.
        var kullanim = await kapsamlar.GetDiskUsageMbAsync(Musteri, ogeButcesi: 1_000_000, sureButcesiMs: 0, CancellationToken.None);

        Assert.Equal(0, kullanim.UsageMb);

        // SIFIR ile "ölçemedim" arasındaki fark tam olarak burada görülür: sayı aynı, anlamı zıt.
        // Bayrak olmasaydı panel bu aboneliği "diski bomboş" diye gösterirdi.
        Assert.False(kullanim.Measured);
        Assert.Equal("files.size_truncated_time", kullanim.TruncatedReasonKey);
    }

    /// <summary>
    /// IIS'ten OKUNAMAYAN SİTE "0 BAYT" SAYILMAZ: toplam eksiktir ve eksik olduğu söylenir.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Okunamayan site ATLANIR (tek bozuk site aboneliğin geri kalanının hesabını düşürmemeli) ama
    /// atlanan şey sessizce sıfıra dönüşmemeli: 40 GB'lık sitesi IIS'ten okunamayan bir abonelik
    /// "2 MB kullanıyor" diye görünürse yönetici o hesabı kotasız sanır. Aynı hata trafik
    /// kotasında bilerek yapılmıyor (<c>TrafikOlcAsync</c> okunamayan siteyi sıfır saymaz), disk
    /// tarafında ise yapılıyordu.
    /// </para>
    /// <para>
    /// SEBEP ANAHTARI BOŞ kalır ve bu bilinçli: bu durumun çevrilmiş bir anahtarı yok, uydurulan
    /// anahtar ise kullanıcıya ham anahtar gösterirdi (<c>MessageKeyCoverageTests</c>). Arayüzün
    /// bakacağı bayrak <c>Measured</c>'dır.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Okunamayan_site_SIFIR_sayilmaz()
    {
        DosyaYaz(Path.Combine(siteKoku, "kendi.bin"), 2 * 1024 * 1024);

        // "yok.test" sahte IIS'te KAYITLI DEĞİL: GetSiteAsync onun için istisna atar.
        await KapsamKurAsync("a.test", "yok.test");

        var kullanim = await kapsamlar.GetDiskUsageMbAsync(Musteri, CancellationToken.None);

        // Okunabilen sitenin baytları SAYILIR (hesap düşmez)...
        Assert.Equal(2, kullanim.UsageMb);

        // ...ama sayı TAM DEĞİLDİR.
        Assert.False(kullanim.Measured);
        Assert.Equal(string.Empty, kullanim.TruncatedReasonKey);
    }

    /// <summary>
    /// Bütçe TÜM ABONELİK için TEKTİR, site başına DEĞİL.
    /// </summary>
    /// <remarks>
    /// Site başına verilseydi tavan site sayısıyla ÇARPILIRDI: 20 siteli bir abonelik 20 kat daha
    /// uzun bir isteğe hak kazanır, yani bütçe fiilen kalkardı. Ölçüm: iki sitenin her birinde 2
    /// dosya var; 2 ögelik bütçe paylaşılıyorsa toplam 4 MB, site başına verilseydi 8 MB çıkardı.
    /// </remarks>
    [Fact]
    public async Task Butce_TUM_ABONELIK_icin_TEKTIR()
    {
        var ikinciKok = Path.Combine(kok, "b.test");
        Directory.CreateDirectory(ikinciKok);
        siteler.Ekle("b.test", ikinciKok);

        DosyaYaz(Path.Combine(siteKoku, "a1.bin"), 2 * 1024 * 1024);
        DosyaYaz(Path.Combine(siteKoku, "a2.bin"), 2 * 1024 * 1024);
        DosyaYaz(Path.Combine(ikinciKok, "b1.bin"), 2 * 1024 * 1024);
        DosyaYaz(Path.Combine(ikinciKok, "b2.bin"), 2 * 1024 * 1024);

        await KapsamKurAsync("a.test", "b.test");

        var kullanim = await kapsamlar.GetDiskUsageMbAsync(Musteri, ogeButcesi: 2, sureButcesiMs: 60_000, CancellationToken.None);

        Assert.Equal(4, kullanim.UsageMb);
    }

    // ------------------------------------------------------------------
    // İPTAL
    // ------------------------------------------------------------------

    /// <summary>
    /// İptal edilen istekte kota hesabı YÜRÜMEYE DEVAM ETMEZ; iptal YUTULMAZ.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Eski kod, site çözümlemesi ile yürüyüşü TEK bir <c>catch { }</c> içine alıyordu ve o
    /// yakalayıcı iptali de yutardı. Ortak yürüyüş iptali istisnayla bildirdiği için bu, iptal
    /// edilmiş bir isteğin aboneliğin KALAN SİTELERİNİ gezmeye devam etmesi demek olurdu — yani
    /// iptalin hiç işlemediği bir yol; tam da bu hesabın en pahalı olduğu yerde.
    /// </para>
    /// <para>
    /// <b>İptal neden kapsam okunduktan SONRA veriliyor:</b> jeton en baştan iptalli verilseydi
    /// istisna kapsam dosyasını okuyan katmandan da gelebilirdi ve test, ölçmek istediği şeyi
    /// (yürüyüşün iptali) değil kendi kurulumunu doğrulardı. Sahte site servisi iptali tam da
    /// yürüyüşten hemen önce tetikler.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Iptal_edilen_istek_YURUYUSU_BIRAKIR()
    {
        DosyaYaz(Path.Combine(siteKoku, "a.bin"), 1024);
        await KapsamKurAsync("a.test");

        using var kaynak = new CancellationTokenSource();
        siteler.SiteSorulunca = kaynak.Cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            kapsamlar.GetDiskUsageMbAsync(Musteri, kaynak.Token));
    }

    // ------------------------------------------------------------------
    // TEK KAYNAK (kaynağa bakan nöbetçiler)
    // ------------------------------------------------------------------

    /// <summary>
    /// Ağaç yürüyüşü TEK KAYNAKTAN gelir: hem dosya yöneticisi hem kota aynı yardımcıyı çağırır ve
    /// hiçbiri kendi kopyasını taşımaz.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Bu test DAVRANIŞI değil YAPIYI ölçer ve bilerek öyle: açığın kök nedeni bir davranış hatası
    /// değil, İKİ KOPYA olmasıydı. Dosya yöneticisi korumaları (bağlantı atlama, bütçe, yığın)
    /// yıllardır doğruydu; kota yolu aynı işi ikinci kez, korumasız yapıyordu. Kopya geri gelirse
    /// davranış testleri bunu GÖREMEZ (her iki kopya da kendi testinde yeşil kalabilir) — o yüzden
    /// nöbetçi burada.
    /// </para>
    /// <para>
    /// Aynı kalıbın bir örneği <c>FileCopyTests.Guvenlik_on_hazirligi_TEK_KAYNAKTAN_gelir</c>.
    /// </para>
    /// </remarks>
    [Fact]
    public void Agac_yuruyusu_TEK_KAYNAKTAN_gelir()
    {
        var dosyaServisi = KaynakOku("src", "AdaPanel.Infrastructure", "Files", "FileService.cs");
        var kapsamServisi = KaynakOku("src", "AdaPanel.Infrastructure", "Security", "CustomerScopeService.cs");
        var ortakYuruyus = KaynakOku("src", "AdaPanel.Infrastructure", "Files", "SafeDirectoryWalk.cs");

        var boyutHesabi = GovdeAl(dosyaServisi, "internal async Task<DirectorySizeDto> CalculateSizeAsync(");
        var kotaHesabi = GovdeAl(kapsamServisi, "internal async Task<DiskUsage> GetDiskUsageMbAsync(");

        // 1) İKİSİ DE ortak yürüyüşü çağırır.
        Assert.Contains("SafeDirectoryWalk.Yuru(", boyutHesabi, StringComparison.Ordinal);
        Assert.Contains("SafeDirectoryWalk.Yuru(", kotaHesabi, StringComparison.Ordinal);

        // 2) Kurallar GERÇEKTEN ortak yerde yaşıyor. Biri buradan düşerse iki çağıran birden
        //    korumasız kalır ve test kızarır.
        foreach (var nobetci in new[]
                 {
                     "new Stack<string>()",              // özyineleme yok (StackOverflowException yakalanamaz)
                     "FileAttributes.ReparsePoint",      // bağlantı takip edilmez
                     "butce.KalanOge <= 0",              // öge bütçesi
                     "butce.SureDoldu",                  // süre bütçesi
                     "ThrowIfCancellationRequested()"    // iptal
                 })
        {
            Assert.Contains(nobetci, ortakYuruyus, StringComparison.Ordinal);
        }

        // 3) Hiçbir çağıran yürüyüşü KENDİ İÇİNDE tekrarlamaz. Tekrarlanan bir yürüyüş, birini
        //    sıkılaştırıp diğerini unutmanın yoludur — bu depoda zaten bir kez öyle oldu.
        foreach (var govde in new[] { boyutHesabi, kotaHesabi })
        {
            Assert.DoesNotContain("EnumerateFileSystemInfos(", govde, StringComparison.Ordinal);
            Assert.DoesNotContain("FileAttributes.ReparsePoint", govde, StringComparison.Ordinal);
            Assert.DoesNotContain("new Stack<", govde, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Açığın kendisi geri GELMEZ: <c>CustomerScopeService</c> içinde
    /// <c>SearchOption.AllDirectories</c> deseni artık YOK.
    /// </summary>
    /// <remarks>
    /// Davranış testleri "şu an döngü yok" der; bu nöbetçi "döngüyü üreten çağrı geri kondu" der.
    /// İkisi aynı şey değil: <c>EnumerateFiles(..., AllDirectories)</c> bir sitenin ağacında
    /// kavşak yokken de yeşil kalır, açık ancak kiracı kavşağı kurduğunda ortaya çıkardı.
    /// </remarks>
    [Fact]
    public void Kota_yolunda_AllDirectories_deseni_YOK()
    {
        var kapsamServisi = KaynakOku("src", "AdaPanel.Infrastructure", "Security", "CustomerScopeService.cs");

        Assert.DoesNotContain("SearchOption.AllDirectories", kapsamServisi, StringComparison.Ordinal);
        Assert.DoesNotContain("Directory.EnumerateFiles(", kapsamServisi, StringComparison.Ordinal);
        Assert.DoesNotContain("Directory.GetFiles(", kapsamServisi, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // Yardımcılar
    // ------------------------------------------------------------------

    private Task KapsamKurAsync(params string[] siteAdlari)
        => kapsamlar.SetScopeAsync(Musteri, siteAdlari, maxSites: 10, maxDiskMb: 1024);

    /// <summary>Verilen UZUNLUKTA dosya yazar (içerik önemsiz, ölçülen tek şey boyut).</summary>
    private static void DosyaYaz(string yol, int uzunluk)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(yol)!);
        File.WriteAllBytes(yol, new byte[uzunluk]);
    }

    private static void JunctionOlustur(string link, string hedef)
    {
        var betik = $"New-Item -ItemType Junction -Path '{link.Replace("'", "''")}' -Target '{hedef.Replace("'", "''")}' | Out-Null";
        var yol = Path.Combine(Path.GetTempPath(), $"ada-kota-{Guid.NewGuid():N}.ps1");
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

    private static string KaynakOku(params string[] parcalar)
        => File.ReadAllText(Path.Combine([DepoKoku(), .. parcalar]));

    private static string DepoKoku()
    {
        var klasor = AppContext.BaseDirectory;
        for (var derinlik = 0; derinlik < 8; derinlik++)
        {
            if (File.Exists(Path.Combine(klasor, "AdaPanel.sln")))
            {
                return Path.GetFullPath(klasor);
            }

            klasor = Path.Combine(klasor, "..");
        }

        throw new DirectoryNotFoundException("Depo koku bulunamadi.");
    }

    private static string GovdeAl(string kaynak, string imza)
    {
        var baslangic = kaynak.IndexOf(imza, StringComparison.Ordinal);
        Assert.True(baslangic >= 0, $"Kaynakta bulunamadi: {imza}");

        var bitis = kaynak.IndexOf("\n    }", baslangic, StringComparison.Ordinal);
        Assert.True(bitis > baslangic, $"Metot govdesinin sonu bulunamadi: {imza}");

        return kaynak[baslangic..bitis];
    }

    /// <summary>Site adını fiziksel köke çeviren sahte; başka hiçbir IIS işlemi kullanılmaz.</summary>
    private sealed class SahteSiteServisi : IIisSiteService
    {
        private readonly Dictionary<string, string> kokler = new(StringComparer.OrdinalIgnoreCase);

        public void Ekle(string siteAdi, string fizikselYol) => kokler[siteAdi] = fizikselYol;

        /// <summary>Site sorulduğunda çalışır; iptal testinin, iptali TAM YÜRÜYÜŞTEN ÖNCE
        /// tetikleyebilmesi için var.</summary>
        public Action? SiteSorulunca { get; set; }

        public Task<SiteDto> GetSiteAsync(string siteName, CancellationToken cancellationToken = default)
        {
            SiteSorulunca?.Invoke();

            if (!kokler.TryGetValue(siteName, out var siteYolu))
            {
                // Erişilemeyen site: kota hesabı bunu ATLAMALI, tüm aboneliği düşürmemeli.
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

    /// <summary>Plan bagi bu testlerin konusu degil; kapsam yalnizca kayittaki degerlerle calisir.</summary>
    private sealed class PlansizServis : IServicePlanService
    {
        public Task<ServicePlanDto?> GetPlanAsync(string id, CancellationToken cancellationToken = default)
            => Task.FromResult<ServicePlanDto?>(null);

        public Task<IReadOnlyCollection<ServicePlanDto>> GetPlansAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyCollection<ServicePlanDto>>([]);

        public Task<ServicePlanDto> CreatePlanAsync(ServicePlanRequest request, Guid? ownerId = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ServicePlanDto> UpdatePlanAsync(string id, ServicePlanRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeletePlanAsync(string id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    /// <summary>Bayi tavani bu testlerin konusu degil.</summary>
    private sealed class TavansizServis : IResellerQuotaService
    {
        public Task<ResellerQuotaDto?> GetQuotaAsync(Guid resellerId, CancellationToken cancellationToken = default)
            => Task.FromResult<ResellerQuotaDto?>(null);

        public Task<ResellerQuotaDto> SetQuotaAsync(Guid resellerId, ResellerQuotaRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RemoveQuotaAsync(Guid resellerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
