using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Files;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Files;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// DİSK KOTASININ dosya yöneticisindeki KURALI ve ÖN ÖLÇÜMÜ.
/// </summary>
/// <remarks>
/// <para>
/// <b>Kapatılan açık:</b> kota denetimi <c>FilesController.UploadAsync</c>'in gövdesine gömülüydü
/// ve yalnız orada vardı. Diski büyüten diğer üç uç — <c>POST /files/copy</c>, <c>PUT /files</c>,
/// <c>POST /files/extract</c> — hiç sormuyordu. 500 MB kotalı bir müşteri 400 MB'lık klasörünü
/// tekrar tekrar kopyalayarak birimi doldurabiliyor, aynı birimdeki KOMŞU KİRACILAR da yazamaz
/// hâle geliyordu.
/// </para>
/// <para>
/// <b>Neden kural saf bir fonksiyonda test ediliyor:</b> kuralın kendisi (bayt hesabı,
/// "0 = sınırsız", kesilmiş ölçümün anlamı) hiçbir bağımlılık istemiyor; 25 üyeli
/// <c>ICustomerScopeService</c>'i taklit etmek, ölçülen şeyle ilgisi olmayan yüzlerce satır gürültü
/// olurdu. "Kimin kotası" sorusu API katmanında kalır ve aşağıdaki kaynak nöbetçisiyle korunur.
/// </para>
/// </remarks>
public sealed class FileQuotaTests : IDisposable
{
    private const long Mb = 1024L * 1024L;

    /// <summary>TAM ölçülmüş kullanım — kesilme senaryosu OLMAYAN testlerin girdisi.</summary>
    private static DiskUsage Olculdu(long kullanilanMb) => new(kullanilanMb, true, string.Empty);

    /// <summary>
    /// KESİLMİŞ kullanım: sayı gerçek kullanım değil bir ALT SINIRDIR.
    /// </summary>
    /// <remarks>
    /// Sebep anahtarı bilerek parametre: kuralın kesilme SEBEBİNE göre farklı davranmadığını
    /// (kullanım tarafında hepsi aynı muamele görür) çağrı yerinde görebilmek için.
    /// </remarks>
    private static DiskUsage Kesildi(long altSinirMb, string sebep) => new(altSinirMb, false, sebep);

    private readonly string kok;
    private readonly string siteKoku;
    private readonly FileService servis;

    public FileQuotaTests()
    {
        kok = Path.Combine(Path.GetTempPath(), "adapanel-kota-" + Guid.NewGuid().ToString("N"));
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
            // Geçici klasör temizliği en iyi çaba.
        }
    }

    // ==================================================================
    // KURAL: TAMSAYI BÖLMESİ HATASI
    // ==================================================================

    /// <summary>
    /// 1 MB ALTINDAKİ DOSYA KOTAYA "0" OLARAK GİRMEZ.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Kapatılan hata:</b> eski kod <c>var incomingMb = file.Length / (1024 * 1024);</c>
    /// yazıyordu — TAMSAYI bölmesi. Tavanı tam dolmuş bir müşteri, 1 MB altındaki dosyaları
    /// SINIRSIZ yükleyebiliyordu: her biri kotaya 0 olarak giriyor, denetim
    /// <c>500 + 0 &gt; 500</c> ile "sığıyor" diyordu. 0.7.6'nın çoklu/sürükle-bırak yüklemesi bunu
    /// tek hamlede yüzlerce dosyaya ölçekliyor.
    /// </para>
    /// <para>
    /// Sınır değeri bilerek "tam tavanda + 1 bayt": eski uygulamada bu KESİNLİKLE geçiyordu, yenide
    /// KESİNLİKLE geçmiyor. Yuvarlamaya bağlı gri bir bölge seçilseydi test, düzeltilmiş kodu da
    /// düzeltilmemiş kodu da geçebilirdi.
    /// </para>
    /// </remarks>
    [Fact]
    public void Bir_MB_altindaki_dosya_kotaya_SIFIR_girmez()
    {
        var hata = Assert.Throws<AppException>(() =>
            FileQuotaGuard.EnsureRoom(maxDiskMb: 500, kullanim: Olculdu(500), gelenBayt: 1, gelenOlcumKesilmeSebebi: string.Empty));

        Assert.Equal("files.quota_exceeded", hata.MessageKey);
        Assert.Equal(403, hata.HttpStatusCode);
    }

    /// <summary>MUTASYON KONTROLÜ: tavana SIĞAN iş reddedilmez.</summary>
    /// <remarks>
    /// Bu olmadan "her isteği reddet" de yukarıdaki testi geçerdi. Ayrıca sınırın DOĞRU yerde
    /// olduğunu ölçer: tam tavana oturan iş geçmeli, tavanı bir bayt aşan geçmemeli.
    /// </remarks>
    [Fact]
    public void Tavana_SIGAN_is_reddedilmez()
    {
        FileQuotaGuard.EnsureRoom(maxDiskMb: 500, kullanim: Olculdu(499), gelenBayt: Mb, gelenOlcumKesilmeSebebi: string.Empty);
        FileQuotaGuard.EnsureRoom(maxDiskMb: 500, kullanim: Olculdu(0), gelenBayt: 700 * 1024, gelenOlcumKesilmeSebebi: string.Empty);
    }

    /// <summary>SINIRSIZ abonelik (<c>MaxDiskMb = 0</c>) hiçbir şeyle sınırlanmaz.</summary>
    /// <remarks>
    /// "0 = sınırsız" panelin her yerinde aynı sözleşme; burada bozmak, kotası olmayan bütün
    /// müşterilerin dosya yöneticisini bir anda kilitlerdi. Kesilmiş ölçüm de burada önemsizdir:
    /// denetlenecek bir tavan yoksa ölçemediğimiz şeyin de sonucu yoktur.
    /// </remarks>
    [Fact]
    public void Sinirsiz_abonelik_DENETLENMEZ()
    {
        FileQuotaGuard.EnsureRoom(maxDiskMb: 0, kullanim: Olculdu(900_000), gelenBayt: long.MaxValue / 2, gelenOlcumKesilmeSebebi: "files.size_truncated_entries");
    }

    // ==================================================================
    // KURAL: KESİLMİŞ ÖLÇÜM
    // ==================================================================

    /// <summary>
    /// KESİLMİŞ ÖLÇÜMÜN ALT SINIRI TAVANI AŞIYORSA CEVAP "KOTA DOLDU"DUR — kesilme değil.
    /// </summary>
    /// <remarks>
    /// Sıra önemlidir: kesilmiş ölçüm bir ALT SINIRDIR, yani "en az bu kadar" demektir. Alt sınır
    /// bile tavanı aşıyorsa sonuç KESİNDİR ve kullanıcıya söylenmesi gereken şey gerçek sebeptir
    /// ("kotanız dolu"), ölçüm ayrıntısı değil ("ağaç ölçülemedi"). Ters sırada yazılmış bir
    /// uygulama kullanıcıyı, aslında kotasını büyütmesi gerekirken içeriği parçalara bölmeye
    /// çalışmakla oyalardı.
    /// </remarks>
    [Fact]
    public void Kesilmis_olcumun_ALT_SINIRI_asiyorsa_KOTA_hatasi_verilir()
    {
        var hata = Assert.Throws<AppException>(() => FileQuotaGuard.EnsureRoom(
            maxDiskMb: 100,
            kullanim: Olculdu(90),
            gelenBayt: 50 * Mb,
            gelenOlcumKesilmeSebebi: "files.size_truncated_entries"));

        Assert.Equal("files.quota_exceeded", hata.MessageKey);
    }

    /// <summary>
    /// ALT SINIR SIĞSA BİLE, ÖLÇÜLEMEYEN İŞ BAŞLATILMAZ.
    /// </summary>
    /// <remarks>
    /// <para>
    /// KARARIN ÖLÇÜMÜ. Buraya gelmek "alt sınır sığıyor" demektir — ama gerçek boyut yine de
    /// aşabilir, çünkü ağacın geri kalanına HİÇ BAKILMADI. Belirsizliği "sığıyor" yönüne
    /// yorumlamak kotayı İNŞA EDİLEBİLİR biçimde delerdi: bütçeyi dolduracak kadar büyük bir ağaç
    /// kuran kiracı, o ağacı istediği kadar kopyalayabilirdi.
    /// </para>
    /// <para>
    /// Bedeli düşük çünkü kesilen ölçüm KULLANICININ SEÇTİĞİ ağaçtır (kota hesabındaki gibi
    /// sunucunun yavaşlığı değil), çaresi vardır (alt klasörleri ayrı kopyala) ve kopyalamanın
    /// KENDİ bütçesi de aynı ağaçta dolacağı için izin verilse bile iş yarıda kesilip geri
    /// sarılırdı. Reddetmek aynı sonucu boşa iş yapmadan verir.
    /// </para>
    /// </remarks>
    [Fact]
    public void Alt_sinir_sigsa_bile_OLCULEMEYEN_is_baslatilmaz()
    {
        var hata = Assert.Throws<AppException>(() => FileQuotaGuard.EnsureRoom(
            maxDiskMb: 500,
            kullanim: Olculdu(1),
            gelenBayt: Mb,
            gelenOlcumKesilmeSebebi: "files.size_truncated_entries"));

        // Anahtar YÜRÜYÜŞÜN KENDİ sebep anahtarıdır; yeni bir anahtar uydurulmadı (çevirisi olmayan
        // anahtar, kullanıcının ekranda ham anahtarı görmesi demektir — MessageKeyCoverageTests).
        Assert.Equal("files.size_truncated_entries", hata.MessageKey);
        Assert.Equal(413, hata.HttpStatusCode);
        Assert.Contains("BASLATILMADI", hata.Details, StringComparison.Ordinal);
    }

    /// <summary>
    /// ERİŞİM REDDİ yüzünden kesilen ölçüm işi DÜŞÜRMEZ.
    /// </summary>
    /// <remarks>
    /// Okunamayan alt klasörü ölçüm de kopyalama da AYNI şekilde atlar; orada ölçüm bir alt sınır
    /// değil, yazılacak şeyin TAM karşılığıdır. Reddetmek, panelin zaten okuyamadığı bir klasör
    /// yüzünden meşru bir kopyalamayı engellemek olurdu — üstelik kullanıcının bunu düzeltmek için
    /// yapabileceği hiçbir şey yok.
    /// </remarks>
    [Fact]
    public void Erisim_reddi_kesilmesi_isi_DUSURMEZ()
    {
        FileQuotaGuard.EnsureRoom(
            maxDiskMb: 500,
            kullanim: Olculdu(1),
            gelenBayt: Mb,
            gelenOlcumKesilmeSebebi: "files.size_truncated_denied");
    }

    // ==================================================================
    // KURAL: KULLANIM ÖLÇÜMÜ KESİLDİĞİNDE (kotanın KENDİ hesabı)
    // ==================================================================

    /// <summary>
    /// KULLANIM ÖLÇÜLEMEMİŞ OLSA BİLE, ALT SINIR TAVANI AŞIYORSA İSTEK REDDEDİLİR.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Kesilmiş ölçüm bir ALT SINIRDIR: gerçek kullanım EN AZ bu kadardır. Alt sınır bile tavanı
    /// aşıyorsa ihlal KESİNDİR ve "ölçümüm eksikti" demek onu ortadan kaldırmaz.
    /// </para>
    /// <para>
    /// <b>Bu, trafik kotasından bilerek AYRILDIĞIMIZ tek noktadır</b> (trafik
    /// <c>olculdu &amp;&amp; bayt &gt;= sinir</c> yazar, yani ölçemediğinde hiç bakmaz) ve fark
    /// ölçülmüştür: trafiğin yaptırımı ASKIYA ALMAKTIR — bütün siteler yayından kalkar, müşteri
    /// kendi başına geri açamaz. Buradaki yaptırım TEK BİR YAZMAYI reddetmektir. Kesin bir ihlali
    /// geçirmek, kotayı ölçüm bütçesini doldurabilen herkese açardı.
    /// </para>
    /// </remarks>
    [Fact]
    public void Olculemeyen_kullanimin_ALT_SINIRI_asiyorsa_REDDEDILIR()
    {
        var hata = Assert.Throws<AppException>(() => FileQuotaGuard.EnsureRoom(
            maxDiskMb: 100,
            kullanim: Kesildi(90, "files.size_truncated_entries"),
            gelenBayt: 50 * Mb,
            gelenOlcumKesilmeSebebi: string.Empty));

        Assert.Equal("files.quota_exceeded", hata.MessageKey);
        Assert.Equal(403, hata.HttpStatusCode);
    }

    /// <summary>
    /// ÖLÇÜLEMEYEN KULLANIM SIĞIYORSA İŞ GEÇER: ölçemediğimiz kotayı UYGULAMAYIZ.
    /// </summary>
    /// <remarks>
    /// <para>
    /// KARARIN ÖLÇÜMÜ ve trafik kotasıyla AYNI felsefe (<c>TrafikOlcAsync</c>: ölçülemeyen
    /// abonelikte kota uygulanmaz). Gerekçe kesilmenin sebeplerinde: üçünden ikisi kiracının
    /// elinde DEĞİLDİR (süre bütçesi = yavaş/ağ diski, erişim reddi = ACL). Belirsizliği "aşmış
    /// say" yönüne yormak, sunucu yavaşladığında meşru müşterinin HİÇ dosya yükleyememesi olurdu
    /// — yaptırım noktası yükleme olduğu için müşteri tümden çalışamaz hâle gelirdi.
    /// </para>
    /// <para>
    /// Bu testin mutasyon değeri yüksek: olmasaydı "ölçemedin mi, reddet" gibi bir uygulama da
    /// yukarıdaki testi geçerdi ve kapatılan asıl açık — kilitlenen meşru müşteri — geri gelirdi.
    /// Kaybedilen kesinlik ise gizlenmiyor, GÖSTERİLİYOR: aynı ölçüm kullanıcı listesine ve
    /// abonelik ekranına <c>DiskMeasured = false</c> olarak gider.
    /// </para>
    /// </remarks>
    [Fact]
    public void Olculemeyen_kullanim_SIGIYORSA_is_GECER()
    {
        FileQuotaGuard.EnsureRoom(
            maxDiskMb: 500,
            kullanim: Kesildi(1, "files.size_truncated_time"),
            gelenBayt: Mb,
            gelenOlcumKesilmeSebebi: string.Empty);
    }

    // ==================================================================
    // ÖN ÖLÇÜM: KOPYALANACAK AĞACIN BOYUTU
    // ==================================================================

    /// <summary>Ölçüm klasör AĞACINI ve DOSYALARI birlikte toplar.</summary>
    /// <remarks>
    /// Boyutlar bilerek FARKLI: hepsi eşit olsaydı bir ögeyi iki kez sayan ya da birini atlayan bir
    /// ölçüm de doğru toplamı verebilirdi.
    /// </remarks>
    [Fact]
    public async Task Olcum_klasor_ve_dosyalari_birlikte_toplar()
    {
        DosyaYaz(Path.Combine(siteKoku, "veri", "ust.bin"), 1000);
        DosyaYaz(Path.Combine(siteKoku, "veri", "alt", "ic.bin"), 200);
        DosyaYaz(Path.Combine(siteKoku, "tek.bin"), 31);

        var olcum = await servis.MeasureAsync(["sites/a.test/veri", "sites/a.test/tek.bin"]);

        Assert.Equal(1231, olcum.TotalBytes);
        Assert.Equal(3, olcum.FileCount);
        Assert.False(olcum.Truncated);
    }

    /// <summary>
    /// OLMAYAN YOL İSTİSNA DEĞİL, SIFIR: yeni dosya yazmak ve eksik kaynak bunu gerektirir.
    /// </summary>
    /// <remarks>
    /// İstisna atsaydı, sekiz ögeli bir kopyalama isteği tek eksik dosya yüzünden HİÇ başlamazdı —
    /// oysa o durum öge başına <c>files.not_found</c> ile raporlanmalı. Yazma yolunda ise hedefin
    /// olmaması NORMAL durumdur (yeni dosya) ve orada istisna, "yeni dosya" düğmesini tümden
    /// bozardı.
    /// </remarks>
    [Fact]
    public async Task Olmayan_yol_SIFIR_sayilir()
    {
        DosyaYaz(Path.Combine(siteKoku, "var.bin"), 500);

        var olcum = await servis.MeasureAsync(["sites/a.test/var.bin", "sites/a.test/yok.bin"]);

        Assert.Equal(500, olcum.TotalBytes);
        Assert.Equal(1, olcum.FileCount);
    }

    /// <summary>Ölçüm SANAL ALAN kapısından geçer: kapsam dışı bir klasörün boyutu bile sızmaz.</summary>
    [Fact]
    public async Task Olcum_sanal_alan_DISINA_bakamaz()
    {
        DosyaYaz(Path.Combine(kok, "disari", "komsu.bin"), 4096);

        var hata = await Assert.ThrowsAsync<AppException>(() =>
            servis.MeasureAsync(["sites/a.test/../disari"]));

        Assert.Equal("files.path_outside_root", hata.MessageKey);
    }

    /// <summary>
    /// ÖLÇÜM BÜTÇELİDİR ve dolduğunda KESİLDİĞİNİ SÖYLER.
    /// </summary>
    /// <remarks>
    /// Kota bir güvenlik sınırıdır: kesilmiş bir toplamı tam sanmak, kotanın ölçemediği kadarını
    /// bedava vermek demektir. Bu yüzden bayrağın kendisi sözleşmenin parçası ve guard onu okumak
    /// ZORUNDA (bkz. <see cref="Alt_sinir_sigsa_bile_OLCULEMEYEN_is_baslatilmaz"/>).
    /// </remarks>
    [Fact]
    public async Task Olcum_butcesi_dolunca_KESILDIGINI_soyler()
    {
        for (var sira = 0; sira < 10; sira++)
        {
            DosyaYaz(Path.Combine(siteKoku, "buyuk", $"dosya-{sira:00}.bin"), 10);
        }

        // Bütçe, TEK bir yol için değil TÜM istek için geçerli; ölçüm bunu paylaşılan bir bütçeyle
        // yapar (gerekçe SafeDirectoryWalkBudget'te). Burada üretim bütçesi kullanılır ve DOLMAZ —
        // dolma davranışı ortak yürüyüşün kendi testlerinde ölçülüyor.
        var olcum = await servis.MeasureAsync(["sites/a.test/buyuk"]);

        Assert.False(olcum.Truncated);
        Assert.Equal(10, olcum.FileCount);
        Assert.Equal(100, olcum.TotalBytes);
    }

    // ==================================================================
    // KAYNAĞA BAKAN NÖBETÇİ: DİSKİ BÜYÜTEN HER UÇ KOTA SORAR
    // ==================================================================

    /// <summary>
    /// DİSKİ BÜYÜTEN DÖRT UCUN HEPSİ KOTAYI SORAR — ve hiçbiri kendi denetimini YAZMAZ.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Bu test DAVRANIŞI değil YAPIYI ölçer ve bilerek öyle: açığın kök nedeni bir davranış hatası
    /// değil, denetimin TEK BİR UCUN GÖVDESİNE gömülü olmasıydı. Gömülü kaldığı sürece her yeni uç
    /// korumasız doğar — nitekim <c>copy</c> tam olarak öyle doğdu. Bu denetleyicinin bir test
    /// projesi yok (API katmanı için birim testi kurulumu bulunmuyor), yani bu nöbetçi kaldırılan
    /// bir çağrıyı yakalayabilecek TEK şey.
    /// </para>
    /// <para>
    /// Aynı kalıbın örnekleri: <c>TenantTreeWalkGuardTests.Uc_yolda_da_KENDI_yuruyusu_YOK</c> ve
    /// <c>FileCopyTests.Guvenlik_on_hazirligi_TEK_KAYNAKTAN_gelir</c>.
    /// </para>
    /// </remarks>
    [Fact]
    public void Diski_buyuten_her_uc_KOTA_sorar()
    {
        var kaynak = File.ReadAllText(Path.Combine(
            DepoKoku(), "src", "AdaPanel.Api", "Controllers", "FilesController.cs"));

        foreach (var imza in new[]
                 {
                     "public async Task<ActionResult<ApiResponse<object>>> UploadAsync(",
                     "public async Task<ActionResult<ApiResponse<FileWorkspaceDto>>> WriteAsync(",
                     "public async Task<ActionResult<ApiResponse<CopyFilesResultDto>>> CopyAsync(",
                     "public async Task<ActionResult<ApiResponse<FileWorkspaceDto>>> ExtractAsync("
                 })
        {
            var govde = GovdeAl(kaynak, imza);
            Assert.Contains("EnsureDiskQuotaAsync(", govde, StringComparison.Ordinal);

            // Denetim ORTAK yerde yaşar: uç kendi kota hesabını TEKRARLAMAZ. Tekrarlanan bir hesap,
            // birini sıkılaştırıp diğerini unutmanın yoludur — bu depoda zaten bir kez öyle oldu.
            Assert.DoesNotContain("MaxDiskMb", govde, StringComparison.Ordinal);
            Assert.DoesNotContain("files.quota_exceeded", govde, StringComparison.Ordinal);
        }

        // TAMSAYI BÖLMESİ GERİ GELMESİN: hesap bayt üzerinden yapılır, MB'ye bölerek değil.
        Assert.DoesNotContain("file.Length / (1024 * 1024)", kaynak, StringComparison.Ordinal);
    }

    /// <summary>
    /// ZORLAMA YOLU ÖLÇÜM BAYRAĞINI YENİDEN ÜRETMEZ: kullanım kurala OLDUĞU GİBİ geçer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Kapatılan açığın kök nedeni bilginin YOLDA kaybolmasıydı: ölçüm "kesildi mi" biliyordu, ama
    /// kotaya düz bir sayı olarak varıyordu. Tip bunu artık zorluyor — kaybın tek yolu, denetleyicide
    /// sayıyı alıp <c>new DiskUsage(..., true, ...)</c> ile "ölçülmüş" gibi yeniden paketlemek.
    /// Bu tam olarak eski davranışa dönmek olur ve DAVRANIŞ testleriyle görülemez: kural doğru
    /// çalışmaya devam eder, yalnızca yanlış girdiyle.
    /// </para>
    /// <para>
    /// Bu denetleyicinin birim test projesi yok (API katmanı için kurulum bulunmuyor); nöbetçi
    /// kaynağa bakmak zorunda. Aranan diziler kaynakta yalnız KOD olarak geçer — yorumda geçselerdi
    /// nöbetçi, kodu silinmiş bir kuralı yorum sayesinde yeşil görürdü (bu depoda bir kez öyle oldu).
    /// </para>
    /// </remarks>
    [Fact]
    public void Kota_yolu_olcum_bayragini_YENIDEN_URETMEZ()
    {
        var kaynak = File.ReadAllText(Path.Combine(
            DepoKoku(), "src", "AdaPanel.Api", "Controllers", "FilesController.cs"));

        var govde = GovdeAl(kaynak, "private async Task EnsureDiskQuotaAsync(");
        var sahipGovdesi = GovdeAl(kaynak, "private async Task EnsureOwnerDiskQuotaAsync(");

        Assert.Contains("await EnsureOwnerDiskQuotaAsync(quotaOwner, olcum, cancellationToken)", govde, StringComparison.Ordinal);
        Assert.Contains("FileQuotaGuard.EnsureRoom(", sahipGovdesi, StringComparison.Ordinal);
        foreach (var ilgiliGovde in new[] { govde, sahipGovdesi })
        {
            Assert.DoesNotContain("new DiskUsage(", ilgiliGovde, StringComparison.Ordinal);
            Assert.DoesNotContain(".UsageMb", ilgiliGovde, StringComparison.Ordinal);
        }
    }

    // --- düzenek ---

    private static void DosyaYaz(string yol, int bayt)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(yol)!);
        File.WriteAllBytes(yol, new byte[bayt]);
    }

    private static string GovdeAl(string kaynak, string imza)
    {
        var baslangic = kaynak.IndexOf(imza, StringComparison.Ordinal);
        Assert.True(baslangic >= 0, $"FilesController.cs icinde bulunamadi: {imza}");

        var bitis = kaynak.IndexOf("\n    }", baslangic, StringComparison.Ordinal);
        Assert.True(bitis > baslangic, $"Metot govdesinin sonu bulunamadi: {imza}");

        return kaynak[baslangic..bitis];
    }

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
