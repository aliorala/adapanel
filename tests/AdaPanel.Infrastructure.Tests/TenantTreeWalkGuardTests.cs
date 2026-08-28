using System.Diagnostics;
using System.IO.Compression;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Backup;
using AdaPanel.Core.Dtos.Databases;
using AdaPanel.Core.Dtos.Migration;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Backup;
using AdaPanel.Infrastructure.Iis;
using AdaPanel.Infrastructure.Migration;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// KİRACININ YAZABİLDİĞİ AĞAÇLARDA YÜRÜYEN ÜÇ YOL: yedek taraması, göçte <c>.user.ini</c> arama,
/// arşivden içe aktarma.
/// </summary>
/// <remarks>
/// <para>
/// <b>Kapatılan açık (aynı hata sınıfının üç örneği):</b> üçü de ağacı KENDİ yazdığı yürüyüşle
/// geziyordu ve üçünde de aynı iki koruma eksikti — bağlantı (junction/symlink) atlaması ve bütçe.
/// Kök her üçünde de kiracının yazabildiği bir yer olduğu için, kiracının kendi klasörüne kendi
/// üst ağacını gösteren TEK bir kavşak koyması yeterliydi:
/// </para>
/// <list type="bullet">
///   <item><description>yedek işi sonsuz döngüye girer (ya da komşunun dosyalarını arşive alır),</description></item>
///   <item><description>göç isteği hiç dönmez,</description></item>
///   <item><description>içe aktarma, site kökü YANA ALINMIŞ hâldeyken takılır.</description></item>
/// </list>
/// <para>
/// <b>Neden GERÇEK dosya sistemi ve GERÇEK kavşak:</b> korunan şeyin kaynağa bakarak karşılığı yok
/// — bağlantı takibi ancak gerçek bir kavşak kurulunca ölçülebilir. Kavşağın kurulduğu her testte
/// ÖN KOŞUL olarak doğrulanır; kurulamamışsa test "yeşil" değil, "ön koşul sağlanmadı" der.
/// </para>
/// <para>
/// <b>Her testte ZAMAN AŞIMI var.</b> Koruma bozulup yürüyüş döngüye girse bile test ASILMAZ:
/// asılan bir test paketini kimse koşturmaz, yani regresyon kırmızı yerine "hâlâ çalışıyor" diye
/// görünürdü. İptal jetonu kabul eden yollarda jeton, kabul etmeyenlerde
/// <see cref="Task.Wait(TimeSpan)"/> kullanılır.
/// </para>
/// </remarks>
public sealed class TenantTreeWalkGuardTests : IDisposable
{
    /// <summary>Kavşak testlerinin üst sınırı; gerekçesi sınıf belgesinde.</summary>
    private static readonly TimeSpan Tavan = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Kesilen taramanın kullanıcıya gösterilen anahtarı: yürüyüşün KENDİ sebep anahtarı.
    /// </summary>
    /// <remarks>
    /// Yeni bir "backup.*" anahtarı UYDURULMADI, çünkü çevirisi olmayan bir anahtar kullanıcının
    /// ekranda ham anahtarı görmesi demektir — <c>MessageKeyCoverageTests</c> tam bu sınıfı
    /// kapatıyor ve ilk denemede bunu YAKALADI (ölçüldü). Bu anahtarın çevirisi tr ve en'de zaten
    /// var; yedeğe özgü sonuç ("arşiv eksiktir") Detail alanında taşınır ve arayüz onu çevirinin
    /// altında olduğu gibi basar.
    /// </remarks>
    private const string TaramaKesildiAnahtari = "files.size_truncated_entries";

    private readonly string kok;

    public TenantTreeWalkGuardTests()
    {
        kok = Path.Combine(Path.GetTempPath(), "adapanel-agac-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(kok);
    }

    public void Dispose()
    {
        try
        {
            // Kavşaklar ÖNCE kaldırılır: özyinelemeli silme bir kavşağı takip ederse testin kendisi,
            // önlemeye çalıştığı veri kaybını üretirdi.
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

    // ===============================================================================================
    // 1) YEDEK TARAMASI
    // ===============================================================================================

    /// <summary>
    /// YEDEK TARAMASI, SİTENİN KENDİNİ GÖSTEREN BİR KAVŞAĞINDA SONSUZA DÖNMEZ.
    /// </summary>
    /// <remarks>
    /// Eski <c>ScanFiles</c> elle yazılmış bir yığın yürüyüşüydü; yığın kısmı doğruydu ama
    /// <c>Directory.GetDirectories</c> kavşağı NORMAL bir klasör gibi döndürür, yani yürüyüş onu
    /// takip ederdi. Kiracının site köküne kendini gösteren bir kavşak koyması, gece çalışan yedek
    /// işini sonsuz döngüye sokmaya yeterdi.
    /// </remarks>
    [Fact]
    public async Task Yedek_taramasi_KENDINI_GOSTEREN_kavsakta_SONSUZA_DONMEZ()
    {
        using var alan = new YedekAlani(kok);
        alan.SiteDosyasi("index.php", "icerik");

        var kendine = Path.Combine(alan.SiteKoku, "kendine");
        KavsakKur(kendine, alan.SiteKoku);

        using var iptal = new CancellationTokenSource(Tavan);
        var kayit = await alan.Servis().BackupSiteAsync(alan.Istek(), iptal.Token);

        // Döngüye giren bir uygulama buraya HİÇ gelmez.
        Assert.False(iptal.IsCancellationRequested, "Yedek taramasi tavani asti: yuruyus kavsagi TAKIP EDIYOR.");

        var girdiler = alan.ArsivGirdileri(kayit);
        Assert.Contains("files/index.php", girdiler);

        // Kavşak arşivde HİÇ yok; iç içe tekrarı ("files/kendine/kendine/...") hiç oluşmamış.
        Assert.DoesNotContain(girdiler, girdi => girdi.StartsWith("files/kendine", StringComparison.Ordinal));
    }

    /// <summary>
    /// YEDEK, KAVŞAĞIN GÖSTERDİĞİ HEDEFİ ARŞİVE KATMAZ.
    /// </summary>
    /// <remarks>
    /// Takip edilseydi kiracı, kendi site köküne komşu bir siteyi (ya da panelin veri klasörünü)
    /// gösteren bir kavşak koyarak BAŞKA BİR KİRACININ dosyalarını kendi indirebildiği yedek
    /// arşivine sokardı — kiracı izolasyonunun doğrudan delinmesi.
    /// </remarks>
    [Fact]
    public async Task Yedek_kavsagin_HEDEFINI_arsive_KATMAZ()
    {
        using var alan = new YedekAlani(kok);
        alan.SiteDosyasi("index.php", "kendi icerigim");

        var komsu = Path.Combine(kok, "komsu-site");
        Directory.CreateDirectory(komsu);
        File.WriteAllText(Path.Combine(komsu, "komsunun-sirri.txt"), "BASKA KIRACININ VERISI");

        KavsakKur(Path.Combine(alan.SiteKoku, "komsuya"), komsu);

        using var iptal = new CancellationTokenSource(Tavan);
        var kayit = await alan.Servis().BackupSiteAsync(alan.Istek(), iptal.Token);

        var girdiler = alan.ArsivGirdileri(kayit);
        Assert.Contains("files/index.php", girdiler);
        Assert.DoesNotContain(girdiler, girdi => girdi.Contains("komsunun-sirri", StringComparison.OrdinalIgnoreCase));

        // Kavşak atlaması "eksik yedek" DEĞİLDİR (gerekçesi ScanFiles belgesinde): kavşak veri
        // değil işaretçidir. Durum bu yüzden temiz kalmalı — aksi hâlde kavşağı olan her site
        // kalıcı olarak "eksik" damgası yerdi.
        Assert.Equal("created", kayit.Status);
    }

    /// <summary>
    /// TARAMA BÜTÇESİ DOLARSA YEDEK ALINIR AMA EKSİK OLDUĞUNU SÖYLER.
    /// </summary>
    /// <remarks>
    /// <para>
    /// KARARIN ÖLÇÜMÜ. Kesilmiş bir tarama EKSİK bir arşiv üretir; eksik arşivi tam sanmak geri
    /// yükleme gününde veri kaybıdır. İki uç da yanlıştı: istisna atıp hiç yedek almamak (gece
    /// çalışan iş sessizce hiçbir şey üretmez) ve sessizce kesmek (kullanıcı "created" görür).
    /// Seçilen yol: yedek alınır, durum <c>created_with_errors</c> olur ve kesilen ağaç ADIYLA
    /// uyarı listesine girer.
    /// </para>
    /// <para>
    /// Test üç şeyi birden ölçer: (a) arşiv gerçekten üretildi, (b) durum "created" DEĞİL,
    /// (c) uyarı kesilen kökü adıyla taşıyor. Yalnız duruma bakmak, "durumu doğru yazan ama
    /// nerede kesildiğini söylemeyen" bir uygulamayı yeşil gösterirdi.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Tarama_butcesi_dolunca_yedek_EKSIK_oldugunu_SOYLER()
    {
        using var alan = new YedekAlani(kok);
        for (var sira = 0; sira < 10; sira++)
        {
            alan.SiteDosyasi($"dosya-{sira:00}.txt", "veri");
        }

        using var iptal = new CancellationTokenSource(Tavan);
        var kayit = await alan.Servis(scanEntryBudget: 3).BackupSiteAsync(alan.Istek(), iptal.Token);

        // (a) Yedek VAR: bütçe, işi tümden iptal etmez.
        Assert.True(File.Exists(kayit.Path));

        // (b) Ama "created" DEĞİL — panelde sağlam yedeklerden ayırt edilebilir olmalı.
        Assert.Equal("created_with_errors", kayit.Status);

        // (c) NEREDE kesildiği yazılı.
        var uyari = Assert.Single(kayit.Warnings, item => item.MessageKey == TaramaKesildiAnahtari);
        Assert.Equal("files", uyari.Target);

        // Sonucun kendisi ("arşiv eksik") ayrıntıda YAZILI olmalı: anahtarın çevirisi yalnızca
        // sebebi ("bütçe doldu") anlatır, kullanıcının bilmesi gereken şey ise arşivin eksik
        // olduğudur.
        //
        // ARANAN ŞEY ARTIK TÜRKÇE CÜMLE DEĞİL ETİKET: `Detail` bu depoda çevrilmeyen alandır ve
        // arayüz onu çevirinin altına HAM basar; içine Türkçe anlatı konunca İngilizce panelde
        // yarı çevrilmiş bir satır çıkıyordu (gerekçe BackupService.ApplySkippedFiles'ta yazılı).
        Assert.Contains("archive=incomplete", uyari.Detail, StringComparison.Ordinal);
        DildenBagimsizOlmali(uyari.Detail, "yedek kesilen-tarama uyarisi");

        // (d) Ölçüm: arşiv gerçekten kesilmiş (10 dosyanın hepsi girmedi). Bu olmadan "uyarıyı
        //     yazıp yine de her şeyi arşivleyen" bir uygulama da testi geçerdi.
        var dosyaGirdileri = alan.ArsivGirdileri(kayit).Count(girdi => girdi.StartsWith("files/", StringComparison.Ordinal));
        Assert.InRange(dosyaGirdileri, 1, 9);
    }

    /// <summary>
    /// MUTASYON KONTROLÜ: bütçe BOLKEN durum "created" kalır ve kesilme uyarısı ÜRETİLMEZ.
    /// </summary>
    /// <remarks>
    /// Bu olmadan "her yedeğe kesildi uyarısı yaz" gibi bir değişiklik de yukarıdaki testi geçerdi;
    /// yani test, korumanın çalıştığını değil yalnızca bir metnin varlığını ölçerdi.
    /// </remarks>
    [Fact]
    public async Task Butce_bolken_yedek_TEMIZ_kalir()
    {
        using var alan = new YedekAlani(kok);
        for (var sira = 0; sira < 10; sira++)
        {
            alan.SiteDosyasi($"dosya-{sira:00}.txt", "veri");
        }

        using var iptal = new CancellationTokenSource(Tavan);
        var kayit = await alan.Servis().BackupSiteAsync(alan.Istek(), iptal.Token);

        Assert.Equal("created", kayit.Status);
        Assert.DoesNotContain(kayit.Warnings, item => item.MessageKey == TaramaKesildiAnahtari);
        Assert.Equal(10, alan.ArsivGirdileri(kayit).Count(girdi => girdi.StartsWith("files/", StringComparison.Ordinal)));
    }

    // ===============================================================================================
    // 2) GÖÇ: .user.ini TARAMASI
    // ===============================================================================================

    /// <summary>
    /// GÖÇÜN <c>.user.ini</c> TARAMASI, KENDİNİ GÖSTEREN BİR KAVŞAKTA SONSUZA DÖNMEZ.
    /// </summary>
    /// <remarks>
    /// Eski çağrı <c>EnumerateFiles(kok, ".user.ini", ...AllDirectories)</c> idi ve bağlantıları
    /// TAKİP EDER. Sondaki <c>.Take(200)</c> bunu engellemiyordu: o SONUCU sınırlar, yürüyüşü
    /// değil — 200'den az eşleşme olduğunda (yani hemen her sitede) ağacın tamamı gezilirdi.
    /// </remarks>
    [Fact]
    public async Task Goc_userini_taramasi_KENDINI_GOSTEREN_kavsakta_SONSUZA_DONMEZ()
    {
        var siteKoku = Path.Combine(kok, "goc-sitesi");
        Directory.CreateDirectory(Path.Combine(siteKoku, "alt"));
        File.WriteAllText(
            Path.Combine(siteKoku, "alt", ".user.ini"),
            "open_basedir=/var/www/vhosts/eski.test/httpdocs");

        KavsakKur(Path.Combine(siteKoku, "kendine"), siteKoku);

        using var iptal = new CancellationTokenSource(Tavan);
        var sonuc = await GocServisi(siteKoku).MigrateSiteAsync(GocIstegi(), null, null, null, iptal.Token);

        Assert.False(iptal.IsCancellationRequested, "Goc taramasi tavani asti: yuruyus kavsagi TAKIP EDIYOR.");

        var adim = Assert.Single(sonuc.Steps, item => item.Step == "userini");
        Assert.True(adim.Success, adim.Detail);

        // KAVŞAK GERÇEKTEN ATLANDI. "Zamanında bitti" tek başına yetmez: bütçe, kavşağı takip eden
        // bir yürüyüşü de er geç durdurur — yani yalnız süreye bakan bir test, korumanın kalkmasını
        // YEŞİL gösterirdi (ölçüldü). Kavşak takip edilseydi AYNI .user.ini "kendine/alt/..." ve
        // "kendine/kendine/alt/..." altında defalarca bulunurdu; sayı 1 ise takip edilmemiştir.
        Assert.Contains("1 adet .user.ini bulundu", adim.Detail ?? string.Empty, StringComparison.Ordinal);

        // Ve iş GERÇEKTEN yapıldı: eski sunucu yolu temizlendi.
        var temizlenmis = await File.ReadAllTextAsync(Path.Combine(siteKoku, "alt", ".user.ini"), iptal.Token);
        Assert.DoesNotContain("/var/www/vhosts", temizlenmis, StringComparison.Ordinal);
    }

    /// <summary>
    /// GÖÇ TARAMASI YARIDA KALIRSA ADIM BUNU SÖYLER.
    /// </summary>
    /// <remarks>
    /// KARARIN ÖLÇÜMÜ. Eksik tarama göçü DÜŞÜRMEZ (dosyalar ve veritabanı zaten taşınmıştır; o
    /// noktada hata döndürmek kullanıcıya "hiçbir şey olmadı" izlenimi verirdi) ama SESSİZ DE
    /// KALMAZ: temizlenmemiş bir <c>.user.ini</c> içindeki eski <c>open_basedir</c>, siteyi 500 ile
    /// düşüren ve sebebi hiçbir yerde yazmayan bir arızadır.
    /// </remarks>
    [Fact]
    public async Task Goc_taramasi_KESILIRSE_adim_bunu_SOYLER()
    {
        var siteKoku = Path.Combine(kok, "goc-buyuk");
        Directory.CreateDirectory(siteKoku);
        for (var sira = 0; sira < 10; sira++)
        {
            File.WriteAllText(Path.Combine(siteKoku, $"dolgu-{sira:00}.txt"), "x");
        }

        using var iptal = new CancellationTokenSource(Tavan);
        var sonuc = await GocServisi(siteKoku, userIniScanEntryBudget: 3)
            .MigrateSiteAsync(GocIstegi(), null, null, null, iptal.Token);

        var adim = Assert.Single(sonuc.Steps, item => item.Step == "userini");
        Assert.True(adim.Success, "Eksik tarama gocu DUSURMEMELI.");
        Assert.NotNull(adim.Detail);
        Assert.Contains("TAMAMLANAMADI", adim.Detail!, StringComparison.Ordinal);
    }

    /// <summary>MUTASYON KONTROLÜ: bütçe bolken adım "tamamlanamadı" DEMEZ.</summary>
    [Fact]
    public async Task Goc_taramasi_tamamlanabildiginde_UYARI_URETMEZ()
    {
        var siteKoku = Path.Combine(kok, "goc-kucuk");
        Directory.CreateDirectory(siteKoku);
        File.WriteAllText(Path.Combine(siteKoku, ".user.ini"), "memory_limit=256M");

        using var iptal = new CancellationTokenSource(Tavan);
        var sonuc = await GocServisi(siteKoku).MigrateSiteAsync(GocIstegi(), null, null, null, iptal.Token);

        var adim = Assert.Single(sonuc.Steps, item => item.Step == "userini");
        Assert.DoesNotContain("TAMAMLANAMADI", adim.Detail ?? string.Empty, StringComparison.Ordinal);
    }

    // ===============================================================================================
    // 3) ARŞİVDEN İÇE AKTARMA
    // ===============================================================================================

    /// <summary>
    /// İÇE AKTARMA KOPYASI, KENDİNİ GÖSTEREN BİR KAVŞAKTA SONSUZA DÖNMEZ.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Bu yolda kavşağı normalde <c>Expand-Archive</c> üretmez; koruma yine de gerekli, çünkü
    /// kaynak klasör diskte durur ve bu yol (kopyalama) sitenin kökünü YANA ALDIKTAN sonra çalışır
    /// — burada takılmak, sitenin ne eski ne yeni hâlde kalması demektir.
    /// </para>
    /// <para>
    /// <b>Neden iptal jetonu değil <see cref="Task.Wait(TimeSpan)"/>:</b> bu yol bir jeton kabul
    /// etmiyor (senkron, saf bir yardımcı). Ölçüt aynı kalıyor — koruma bozulursa test asılmaz,
    /// tavanı aşar ve kızarır.
    /// </para>
    /// </remarks>
    [Fact]
    public void Ice_aktarma_KENDINI_GOSTEREN_kavsakta_SONSUZA_DONMEZ()
    {
        var kaynak = Path.Combine(kok, "arsiv-icerigi");
        Directory.CreateDirectory(Path.Combine(kaynak, "wp-content"));
        File.WriteAllText(Path.Combine(kaynak, "index.php"), "<?php");
        File.WriteAllText(Path.Combine(kaynak, "wp-content", "tema.css"), "body{}");

        KavsakKur(Path.Combine(kaynak, "kendine"), kaynak);

        var hedef = Path.Combine(kok, "site-koku");
        Directory.CreateDirectory(hedef);

        var is1 = Task.Run(() => SiteHostingSettingsService.ApplyImportedContent("site", kaynak, hedef, cleanFirst: false));
        Assert.True(is1.Wait(Tavan), "Ice aktarma tavani asti: kopyalama kavsagi TAKIP EDIYOR.");

        Assert.Equal(2, is1.Result.FilesCopied);
        Assert.True(File.Exists(Path.Combine(hedef, "wp-content", "tema.css")));
        Assert.False(Directory.Exists(Path.Combine(hedef, "kendine")), "Kavsak siteye KOPYALANMAMALI.");
    }

    /// <summary>İÇE AKTARMA, KAVŞAĞIN HEDEFİNİ SİTEYE KOPYALAMAZ.</summary>
    [Fact]
    public void Ice_aktarma_kavsagin_HEDEFINI_siteye_KOPYALAMAZ()
    {
        var disari = Path.Combine(kok, "disaridaki-veri");
        Directory.CreateDirectory(disari);
        File.WriteAllText(Path.Combine(disari, "baskasinin-dosyasi.txt"), "BASKA KIRACININ VERISI");

        var kaynak = Path.Combine(kok, "arsiv2");
        Directory.CreateDirectory(kaynak);
        File.WriteAllText(Path.Combine(kaynak, "index.php"), "<?php");
        KavsakKur(Path.Combine(kaynak, "disariya"), disari);

        var hedef = Path.Combine(kok, "site-koku2");
        Directory.CreateDirectory(hedef);

        var is1 = Task.Run(() => SiteHostingSettingsService.ApplyImportedContent("site", kaynak, hedef, cleanFirst: false));
        Assert.True(is1.Wait(Tavan), "Ice aktarma tavani asti.");

        Assert.Equal(1, is1.Result.FilesCopied);
        Assert.False(File.Exists(Path.Combine(hedef, "disariya", "baskasinin-dosyasi.txt")));
    }

    /// <summary>
    /// ÇOK BÜYÜK ARŞİV AĞACI: SİTEYE DOKUNULMADAN ÖLÇÜLÜR VE REDDEDİLİR.
    /// </summary>
    /// <remarks>
    /// KARARIN ÖLÇÜMÜ ve yedekten neden FARKLI olduğu: burada iş bir okuma değil, sitenin kökünü
    /// DEĞİŞTİREN bir yazmadır. Yedekte kesilmiş tarama "eksik ama damgalı bir arşiv" bırakır —
    /// elde bir şey kalır. Burada kesilmiş kopyalama "yarısı yeni, yarısı yok" bir site bırakırdı
    /// ve o, kullanıcı için veri kaybından ayırt edilemez. Bu yüzden kısmi sonuca izin YOKTUR.
    /// </remarks>
    [Fact]
    public void Cok_buyuk_ice_aktarma_agaci_OLCUMDE_yakalanir()
    {
        var kaynak = Path.Combine(kok, "zip-bombasi");
        Directory.CreateDirectory(kaynak);
        for (var sira = 0; sira < 10; sira++)
        {
            File.WriteAllText(Path.Combine(kaynak, $"dosya-{sira:00}.txt"), "x");
        }

        var olcum = SiteHostingSettingsService.MeasureImportTree(kaynak, ogeButcesi: 3);

        Assert.True(olcum.Truncated, "Butce dolmasina ragmen olcum KESILMEDI.");
        Assert.NotEmpty(olcum.TruncatedReasonKey);

        // REDDİN AYRINTISI DİLDEN BAĞIMSIZ ve ÖLÇÜLEN SAYILARI taşıyor.
        //
        // Neden burada ve neden fırlatma yolundan değil: gerçek fırlatma
        // `ImportSiteArchiveAsync` içinde, bir HTTP indirmesinin ve yarım milyon ögelik bir ağacın
        // arkasında. Ayrıntıyı üreten satır bu yüzden ayrı bir yardımcıya alındı ve ölçülen şey
        // tam olarak kullanıcının göreceği metin.
        var ayrinti = SiteHostingSettingsService.CokBuyukArsivAyrintisi(olcum, 3);

        Assert.Contains($"files>={olcum.FileCount}", ayrinti, StringComparison.Ordinal);
        Assert.Contains("limit=3", ayrinti, StringComparison.Ordinal);

        // Kullanıcının bilmesi gereken asıl olgu: SİTEYE DOKUNULMADI.
        Assert.Contains("site=untouched", ayrinti, StringComparison.Ordinal);
        DildenBagimsizOlmali(ayrinti, "cok buyuk arsiv reddi");
    }

    /// <summary>
    /// KOPYALAMA BÜTÇESİ DOLARSA İSTİSNA ATILIR VE SİTE GERİ SARILIR — yarım kopya BIRAKILMAZ.
    /// </summary>
    [Fact]
    public void Ice_aktarma_butcesi_dolunca_SITE_GERI_SARILIR()
    {
        var kaynak = Path.Combine(kok, "buyuk-kaynak");
        Directory.CreateDirectory(kaynak);
        for (var sira = 0; sira < 10; sira++)
        {
            File.WriteAllText(Path.Combine(kaynak, $"yeni-{sira:00}.txt"), "yeni");
        }

        var hedef = Path.Combine(kok, "site-koku3");
        Directory.CreateDirectory(hedef);
        File.WriteAllText(Path.Combine(hedef, "eski-site.txt"), "ESKI ICERIK");

        var hata = Assert.Throws<AppException>(() =>
            SiteHostingSettingsService.ApplyImportedContent("site", kaynak, hedef, cleanFirst: true, ogeButcesi: 3));

        // Anahtar, yürüyüşün ÇEVİRİSİ OLAN sebep anahtarıdır (gerekçesi TaramaKesildiAnahtari'nde).
        Assert.Equal(TaramaKesildiAnahtari, hata.MessageKey);
        Assert.Equal(413, hata.HttpStatusCode);

        // BU YOLA ÖZGÜ OLGU AYRINTIDA DURUYOR ve DİLDEN BAĞIMSIZ: hata kutusuna ayrıntı
        // "çeviri + ham metin" olarak basılır (useNotice.resolveApiMessage), yani `t()`'ye hiç
        // uğramaz. Buraya Türkçe cümle yazmak, İngilizce panelde İngilizce başlığın altında
        // Türkçe bir satır demekti.
        Assert.Contains("copy=truncated", hata.Details, StringComparison.Ordinal);
        Assert.Contains("site=rolled_back", hata.Details, StringComparison.Ordinal);
        DildenBagimsizOlmali(hata.Details, "ice aktarma kopyalama kesintisi");

        // GERİ SARMA GERÇEKTEN OLDU: eski içerik yerinde, yarım kopya yok.
        Assert.True(File.Exists(Path.Combine(hedef, "eski-site.txt")), "Eski site icerigi GERI GELMEDI.");
        Assert.Empty(Directory.GetFiles(hedef, "yeni-*.txt"));
    }

    /// <summary>MUTASYON KONTROLÜ: bütçe bolken içe aktarma normal biçimde tamamlanır.</summary>
    [Fact]
    public void Butce_bolken_ice_aktarma_TAMAMLANIR()
    {
        var kaynak = Path.Combine(kok, "normal-kaynak");
        Directory.CreateDirectory(kaynak);
        for (var sira = 0; sira < 10; sira++)
        {
            File.WriteAllText(Path.Combine(kaynak, $"yeni-{sira:00}.txt"), "yeni");
        }

        var hedef = Path.Combine(kok, "site-koku4");
        Directory.CreateDirectory(hedef);

        var sonuc = SiteHostingSettingsService.ApplyImportedContent("site", kaynak, hedef, cleanFirst: true);

        Assert.Equal(10, sonuc.FilesCopied);
        Assert.False(SiteHostingSettingsService.MeasureImportTree(kaynak).Truncated);
    }

    // ===============================================================================================
    // 4) KAYNAĞA BAKAN NÖBETÇİ
    // ===============================================================================================

    /// <summary>
    /// BEŞ YOLDA DA ELLE YAZILMIŞ İKİNCİ BİR YÜRÜYÜŞ ARTIK YOK.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Bu test DAVRANIŞI değil YAPIYI ölçer ve bilerek öyle: açığın kök nedeni bir davranış hatası
    /// değil, aynı yürüyüşün DÖRT KOPYASI olmasıydı. Kopya geri gelirse davranış testleri bunu
    /// GÖREMEZ — kavşaksız bir ağaçta her kopya kendi testinde yeşil kalır; açık ancak kiracı
    /// kavşağı kurduğunda ortaya çıkar.
    /// </para>
    /// <para>
    /// <b>DÖRDÜNCÜ YOL SONRADAN EKLENDİ ve eklenmesi bir önceki turun ASIL kazancıydı:</b> liste
    /// yalnız üç dosyaya bakıyordu ve <c>FileService.cs</c> içinde YOKTU. Sonuç ölçüldü —
    /// <c>FileService.KlasoruKopyala</c> kendini ÖZYİNELEMELİ çağıran, hiçbir bütçesi olmayan bir
    /// yürüyüş taşımaya devam ediyordu; yani bu nöbetçinin koruduğunu iddia ettiği kural, nöbetçinin
    /// göremediği bir dosyada çiğneniyordu. Elle yazılmış kapsam listesi bu depoda beşinci kez
    /// kaydı; listeye yeni bir yürüyüş müşterisi eklendiğinde buraya da eklenmesi ZORUNLUDUR.
    /// </para>
    /// <para>
    /// <b>BEŞİNCİ YOL (<c>SiteCascadeDeletionService.CountFilesSafely</c>) aynı dersin devamıdır:</b>
    /// o gövdenin korumaları (kavşak atlaması + tavan) DOĞRUYDU, yani ortada açık yoktu — ama
    /// nöbetçinin listesinde olmadığı için "yürüyüş TEK YERDE" iddiası onun üzerinde HİÇ
    /// ölçülmüyordu. Doğru bir kopya da kopyadır: sıkılaştırma günü biri güncellenip diğeri
    /// unutulur. O yüzden liste, "açık var mı" ile değil "elle yazılmış yürüyüş var mı" ile
    /// büyütülür.
    /// </para>
    /// <para>
    /// Aynı kalıbın örnekleri: <c>QuotaDirectoryWalkTests.Agac_yuruyusu_TEK_KAYNAKTAN_gelir</c> ve
    /// <c>FileCopyTests.Guvenlik_on_hazirligi_TEK_KAYNAKTAN_gelir</c>.
    /// </para>
    /// </remarks>
    [Fact]
    public void Uc_yolda_da_KENDI_yuruyusu_YOK()
    {
        var yedek = KaynakOku("src", "AdaPanel.Infrastructure", "Backup", "BackupService.cs");
        var goc = KaynakOku("src", "AdaPanel.Infrastructure", "Migration", "SiteMigrationService.cs");
        var barindirma = KaynakOku("src", "AdaPanel.Infrastructure", "Iis", "SiteHostingSettingsService.cs");
        var dosyaYoneticisi = KaynakOku("src", "AdaPanel.Infrastructure", "Files", "FileService.cs");
        var siteSilme = KaynakOku("src", "AdaPanel.Infrastructure", "Iis", "SiteCascadeDeletionService.cs");

        // 1) Açığı ÜRETEN çağrı hiçbirinde YOK. Davranış testleri "şu an döngü yok" der; bu nöbetçi
        //    "döngüyü üreten çağrı geri kondu" der.
        foreach (var kaynak in new[] { yedek, goc, barindirma, dosyaYoneticisi, siteSilme })
        {
            Assert.DoesNotContain("SearchOption.AllDirectories", kaynak, StringComparison.Ordinal);
        }

        // 2) Beş gövde de ORTAK yürüyüşü çağırır ve KENDİ yürüyüşünü taşımaz.
        foreach (var (kaynak, imza) in new[]
                 {
                     (yedek, "private static List<ArchiveFile> ScanFiles("),
                     (goc, "private async Task<string?> SanitizeUserIniFilesAsync("),
                     (barindirma, "private static int CopyDirectoryRecursive("),
                     (dosyaYoneticisi, "private static KlasorKopyasi KlasoruKopyala("),
                     // Dönüş tipi artık düz sayı DEĞİL: sayımın kesilip kesilmediği de taşınıyor
                     // (FileCountMeasurement). Nöbetçinin sorduğu soru değişmedi — bu gövde KENDİ
                     // yürüyüşünü taşımıyor, ortak kapıyı çağırıyor.
                     (siteSilme, "internal static FileCountMeasurement CountFilesSafely(")
                 })
        {
            var govde = GovdeAl(kaynak, imza);

            Assert.Contains("SafeDirectoryWalk.Yuru(", govde, StringComparison.Ordinal);
            Assert.DoesNotContain("new Stack<", govde, StringComparison.Ordinal);
            Assert.DoesNotContain("new Queue<", govde, StringComparison.Ordinal);
            Assert.DoesNotContain("FileAttributes.ReparsePoint", govde, StringComparison.Ordinal);
            Assert.DoesNotContain("EnumerateFileSystemInfos(", govde, StringComparison.Ordinal);

            // ÖZYİNELEME YOK: gövde KENDİ adını çağırmaz. `new Stack<` denetimi elle yazılmış YIĞIN
            // yürüyüşünü yakalar ama ÖZYİNELEMELİ olanı yakalamaz — FileService.KlasoruKopyala tam
            // olarak o türdendi (yığın yok, kendini çağırıyordu) ve derin bir ağaçta
            // StackOverflowException üretirdi; o istisna .NET'te YAKALANAMAZ, yani panel süreci
            // TÜM kiracılar için ölürdü.
            var ad = imza[(imza.LastIndexOf(' ', imza.IndexOf('(', StringComparison.Ordinal)) + 1)..];
            Assert.DoesNotContain(ad, govde[imza.Length..], StringComparison.Ordinal);
        }

        // 3) Korumalar GERÇEKTEN ortak yerde yaşıyor. Biri buradan düşerse BÜTÜN çağıranlar birden
        //    korumasız kalır ve bu test kızarır.
        var ortak = KaynakOku("src", "AdaPanel.Infrastructure", "Files", "SafeDirectoryWalk.cs");
        foreach (var nobetci in new[]
                 {
                     "new Stack<string>()",           // özyineleme yok (StackOverflowException yakalanamaz)
                     "FileAttributes.ReparsePoint",   // bağlantı takip edilmez
                     "butce.KalanOge <= 0",           // öge bütçesi
                     "butce.SureDoldu",               // süre bütçesi
                     "ThrowIfCancellationRequested()" // iptal
                 })
        {
            Assert.Contains(nobetci, ortak, StringComparison.Ordinal);
        }
    }

    // ===============================================================================================
    // Yardımcılar
    // ===============================================================================================

    /// <summary>
    /// GERÇEK bir kavşak kurar ve KURULDUĞUNU doğrular.
    /// </summary>
    /// <remarks>
    /// Doğrulama ön koşuldur: kavşak sessizce kurulamazsa test, korumayı hiç sınamadan yeşil
    /// kalırdı — yani en tehlikeli test türü olurdu (koruma yokken de geçen bir test).
    /// </remarks>
    private static void KavsakKur(string baglanti, string hedef)
    {
        var betik = $"New-Item -ItemType Junction -Path '{baglanti.Replace("'", "''")}' -Target '{hedef.Replace("'", "''")}' | Out-Null";
        var yol = Path.Combine(Path.GetTempPath(), $"ada-agac-{Guid.NewGuid():N}.ps1");
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

        Assert.True(Directory.Exists(baglanti), "Junction kurulamadi; test onkosulu saglanmadi.");
        Assert.True(
            new DirectoryInfo(baglanti).Attributes.HasFlag(FileAttributes.ReparsePoint),
            "Kurulan girdi kavsak degil; test onkosulu saglanmadi.");
    }

    /// <summary>
    /// Kullanıcıya HAM basılan bir ayrıntı alanının DİLDEN BAĞIMSIZ olduğunu ölçer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Neden ASCII ölçütü:</b> bu depoda <c>Detail</c>/<c>Details</c> ÇEVRİLMEZ — ne sunucu
    /// tarafında i18n anahtarı taşır ne de arayüz onu <c>t()</c>'den geçirir (BackupView.vue
    /// <c>{{ warning.detail }}</c>, useNotice.ts "çeviri + \n\n + ham ayrıntı"). Alanın kendi
    /// belgesi de bunu söyler: "Teknik ayrıntı. Çevrilmez; teşhis içindir."
    /// </para>
    /// <para>
    /// Buna rağmen bu iki yola Türkçe anlatı cümleleri yazılmıştı ve sonuç ölçüldü: İNGİLİZCE
    /// panelde İngilizce anahtarın hemen altında Türkçe bir cümle görünüyordu. Yeni bir i18n
    /// anahtarı çözüm DEĞİL (çevirisi olmayan anahtar ekranda HAM görünür — <c>MessageKeyCoverageTests</c>
    /// tam bunu kapatıyor); doğru olan alanı sözleşmesine çekmek.
    /// </para>
    /// <para>
    /// Ölçüt ASCII, çünkü bu iki alanın içeriğini TAMAMEN kod üretiyor (sayı + sabit etiket);
    /// dışarıdan gelen bir yol ya da istisna metni buraya karışmıyor. Türkçe cümle geri
    /// yazıldığında <c>ğ ı İ ş</c> harfleri bu iddiayı anında kırar.
    /// </para>
    /// <para>
    /// <b><c>internal</c>, çünkü aynı sınıfın son iki örneği başka dosyalarda ölçülüyor</b>
    /// (<c>BackupReconciliationTests</c> — kimliksiz uzak hedef, <c>MailPerSiteBackupTests</c> —
    /// yapılandırılmamış posta arşivleyicisi). İkinci bir ASCII denetimi yazmak, aynı kuralı iki
    /// yerde tutmak olurdu: kuralı sıkılaştırma günü birini güncelleyip diğerini unutmak, bu
    /// dosyanın bütün varlık sebebi olan hatanın ta kendisi.
    /// </para>
    /// </remarks>
    internal static void DildenBagimsizOlmali(string? ayrinti, string neresi)
    {
        Assert.False(string.IsNullOrWhiteSpace(ayrinti), $"{neresi}: ayrinti bos.");

        var yabanci = ayrinti!.Where(karakter => karakter > 127).ToArray();
        Assert.True(
            yabanci.Length == 0,
            $"{neresi}: ayrinti dile bagli karakter tasiyor ('{new string(yabanci)}') -> '{ayrinti}'");
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

    // --- Göç kurulumu ---

    /// <summary>
    /// Site sağlama ve veritabanı servisleri <c>null!</c> geçilir ve bu BİLEREK öyle: istek
    /// <c>CreateSite=false, TransferDatabase=false</c> olduğu için o iki yol hiç çalışmaz.
    /// Yüzlerce üyeli iki arabirimi taklit etmek, ölçülen şeyle (ağaç yürüyüşü) ilgisi olmayan
    /// yüzlerce satır gürültü demekti; kullanılsalardı test <c>NullReferenceException</c> ile
    /// KIZARIRDI, yani sessizce yanlış bir şey ölçmez.
    /// </summary>
    private static SiteMigrationService GocServisi(string siteKoku, int? userIniScanEntryBudget = null)
        => new(
            null!,
            new SahteDosyaGocu(siteKoku),
            null!,
            new SahteSiteServisi(siteKoku),
            NullLogger<SiteMigrationService>.Instance,
            userIniScanEntryBudget);

    /// <summary>Yalnız dosya çekme + .user.ini adımını çalıştıran istek: ölçülen tek şey tarama.</summary>
    private static MigrateSiteRequest GocIstegi() => new()
    {
        SiteName = "goc.test",
        CreateSite = false,
        PullFiles = true,
        TransferDatabase = false
    };

    /// <summary>Dosyalar zaten yerinde; çekme adımı yalnız "başarılı" der ki userini adımı çalışsın.</summary>
    private sealed class SahteDosyaGocu(string hedef) : IFileMigrationService
    {
        public Task<RemoteFileMigrationResult> PullAsync(RemoteFileMigrationRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new RemoteFileMigrationResult
            {
                SiteName = request.SiteName,
                Protocol = request.Protocol,
                FilesDownloaded = 1,
                BytesDownloaded = 1,
                TargetPath = hedef
            });
    }

    /// <summary>Site adını fiziksel köke çeviren sahte; başka hiçbir IIS işlemi kullanılmaz.</summary>
    private sealed class SahteSiteServisi(string fizikselYol) : IIisSiteService
    {
        public Task<SiteDto> GetSiteAsync(string siteName, CancellationToken cancellationToken = default)
            => Task.FromResult(new SiteDto { Name = siteName, PhysicalPath = fizikselYol, ApplicationRootPath = fizikselYol });

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

    // --- Yedek kurulumu (BackupSkippedFileTests kalıbı) ---

    private sealed class YedekAlani(string kok) : IDisposable
    {
        private readonly string veriKoku = Olustur(Path.Combine(kok, "veri"));

        public string SiteKoku { get; } = Olustur(Path.Combine(kok, "wwwroot", "shop"));

        public BackupService Servis(int? scanEntryBudget = null) =>
            new(new SahteSiteServisi(SiteKoku),
                null!,
                null!,
                new SahteTercihServisi(Path.Combine(kok, "backups")),
                null!,
                new JsonFileStore(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = veriKoku })),
                null!,
                scanEntryBudget: scanEntryBudget);

        public BackupSiteRequest Istek() => new()
        {
            SiteName = "shop.example.com",
            IncludeFiles = true,
            IncludeConfig = false,
            IncludeMail = false,
            TargetType = "local"
        };

        public void SiteDosyasi(string goreliAd, string icerik)
        {
            var yol = Path.Combine(SiteKoku, goreliAd.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(yol)!);
            File.WriteAllText(yol, icerik);
        }

        public IReadOnlyCollection<string> ArsivGirdileri(BackupRecordDto kayit)
        {
            using var arsiv = ZipFile.OpenRead(kayit.Path);
            return arsiv.Entries.Select(girdi => girdi.FullName).ToArray();
        }

        public void Dispose()
        {
        }

        private static string Olustur(string yol)
        {
            Directory.CreateDirectory(yol);
            return yol;
        }
    }
}
