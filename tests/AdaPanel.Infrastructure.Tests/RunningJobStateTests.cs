using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.RunningJobs;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.RunningJobs;
using AdaPanel.Infrastructure.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// UZUN SÜREN İŞLERİN sunucudan geri okunabilmesi (yedek, geri yükleme, göç, bileşen kurulumu).
/// <para>
/// <b>KAPATILAN HATA:</b> bu uçların hiçbiri işi arka plana almıyor — HTTP isteği iş bitene kadar
/// açık kalıyor. Kullanıcı sayfayı yenilediğinde iş sunucuda DEVAM ediyor ama panelde "sürüyor"
/// bilgisi tamamen kayboluyordu: ekran boş görünüyor, kullanıcı işi ikinci kez tetikliyordu (iki
/// saatlik ikinci bir yedek ya da aynı siteye ikinci bir göç).
/// </para>
/// <para>
/// <b>NEDEN BURADA TEST EDİLİYOR:</b> ölçülen şey gerçek bir yedek/göç değil, panelin DURUM
/// MUHASEBESİ: kayıt yazılıyor mu, sahibi doğru mu belirleniyor, panel yeniden başladığında yalan
/// söylüyor mu, ikinci iş engelleniyor mu. Aynı gerekçe antivirüs tarafında da geçerliydi
/// (bkz. <see cref="AntivirusRunningScanTests"/>) ve çözüm oradan alındı.
/// </para>
/// </summary>
public sealed class RunningJobStateTests
{
    [Fact]
    public async Task Is_Yokken_Durum_Bos_Doner()
    {
        using var alan = new IsAlani();

        var durum = await alan.Servis().GetStatusAsync([RunningJobKinds.Backup], null, alan.Iptal);

        Assert.Empty(durum.Running);
        Assert.Empty(durum.Interrupted);
    }

    /// <summary>
    /// ASIL DÜZELTME: iş sürerken açılan/yenilenen sayfa "sürüyor"u sunucudan geri okuyabilir.
    /// (Mutasyon: <c>GetStatusAsync</c> daima boş liste dönerse bu test kırılır ve hata geri gelir.)
    /// </summary>
    [Fact]
    public async Task Suren_Is_Hedefi_Ve_Sunucudaki_BaslangicAnini_Dondurur()
    {
        using var alan = new IsAlani();
        var servis = alan.Servis();

        await using var kapsam = await servis.BeginAsync(
            RunningJobKinds.Backup, "shop.example.com", subjectUserId: null, alan.Iptal);

        var durum = await servis.GetStatusAsync([RunningJobKinds.Backup], null, alan.Iptal);

        var is1 = Assert.Single(durum.Running);
        Assert.Equal(RunningJobKinds.Backup, is1.Kind);
        Assert.Equal("shop.example.com", is1.Title);
        Assert.True(is1.StartedAtUtc > DateTime.UtcNow.AddMinutes(-1));

        // Sunucunun kendi saati de dönmeli: geçen süre tarayıcının saatiyle hesaplansaydı,
        // saati sapmış bir istemcide panel "3 saattir sürüyor" ya da negatif süre yazardı.
        Assert.True(durum.ServerTimeUtc > DateTime.UtcNow.AddMinutes(-1));
    }

    /// <summary>
    /// SAHTE YÜZDE YASAĞI sözleşme düzeyinde korunuyor. Bu işlerin hiçbiri ölçülebilir ilerleme
    /// yayımlamıyor; DTO'ya bir yüzde alanı eklenirse onu doldurmanın tek yolu UYDURMAK olur.
    /// </summary>
    [Fact]
    public void Suren_Is_Sozlesmesinde_Yuzde_Alani_Yok()
    {
        var alanlar = typeof(RunningJobDto).GetProperties().Select(item => item.Name).ToArray();

        Assert.Equal(new[] { "Kind", "StartedAtUtc", "Title" }.OrderBy(x => x), alanlar.OrderBy(x => x));
        Assert.DoesNotContain(
            alanlar,
            ad => ad.Contains("percent", StringComparison.OrdinalIgnoreCase) ||
                  ad.Contains("progress", StringComparison.OrdinalIgnoreCase) ||
                  ad.Contains("eta", StringComparison.OrdinalIgnoreCase) ||
                  ad.Contains("remaining", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// İş bittiğinde kayıt temizlenmeli. Temizlenmezse bir sonraki iş 409 alır ve panel ekranda
    /// sonsuz "sürüyor" şeridi gösterir.
    /// </summary>
    [Fact]
    public async Task Is_Bitince_Kayit_Temizlenir()
    {
        using var alan = new IsAlani();
        var servis = alan.Servis();

        await using (await servis.BeginAsync(RunningJobKinds.Backup, "shop.example.com", null, alan.Iptal))
        {
            Assert.Single((await servis.GetStatusAsync([RunningJobKinds.Backup], null, alan.Iptal)).Running);
        }

        Assert.Empty((await servis.GetStatusAsync([RunningJobKinds.Backup], null, alan.Iptal)).Running);
    }

    /// <summary>
    /// İŞ PATLASA BİLE kayıt diskte kalmamalı: kalsaydı panel bir daha asla o türden iş
    /// başlatamaz, sonsuza kadar "sürüyor" derdi.
    /// </summary>
    [Fact]
    public async Task Is_Patlasa_Bile_Kayit_Temizlenir()
    {
        using var alan = new IsAlani();
        var servis = alan.Servis();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await using var kapsam = await servis.BeginAsync(RunningJobKinds.Backup, "shop.example.com", null, alan.Iptal);
            throw new InvalidOperationException("disk dolu");
        });

        Assert.Empty((await servis.GetStatusAsync([RunningJobKinds.Backup], null, alan.Iptal)).Running);
    }

    /// <summary>
    /// AYNI ANDA İKİ İŞ YOK: ikinci istek 409 ile reddedilir. Aksi hâlde ikinci iş birincinin
    /// kaydını ezerdi ve panel bittiğinde hangisinin bittiğini söyleyemezdi.
    /// </summary>
    [Fact]
    public async Task Ayni_Turden_Ikinci_Is_409_Ile_Reddedilir()
    {
        using var alan = new IsAlani();
        var servis = alan.Servis();

        await using var birinci = await servis.BeginAsync(RunningJobKinds.Backup, "shop.example.com", null, alan.Iptal);

        var hata = await Assert.ThrowsAsync<AppException>(() =>
            servis.BeginAsync(RunningJobKinds.Backup, "blog.example.com", null, alan.Iptal));

        Assert.Equal("runningjob.backup_in_progress", hata.MessageKey);
        Assert.Equal(409, hata.HttpStatusCode);
        Assert.Contains("shop.example.com", hata.Details);
    }

    /// <summary>
    /// FARKLI TÜRLER BİRBİRİNİ ENGELLEMEZ. Bir geri yükleme sürerken elle tetiklenen bir yedeği
    /// yasaklamak, kullanıcıyı riskli bir işlemden önce yedek almaktan alıkoyardı.
    /// </summary>
    [Fact]
    public async Task Farkli_Turler_Ayni_Anda_Calisabilir()
    {
        using var alan = new IsAlani();
        var servis = alan.Servis();

        await using var yedek = await servis.BeginAsync(RunningJobKinds.Backup, "shop.example.com", null, alan.Iptal);
        await using var geriYukle = await servis.BeginAsync(RunningJobKinds.Restore, "shop-20260824.zip", null, alan.Iptal);

        var durum = await servis.GetStatusAsync(
            [RunningJobKinds.Backup, RunningJobKinds.Restore], null, alan.Iptal);

        Assert.Equal(2, durum.Running.Count);
    }

    /// <summary>
    /// İstenmeyen TÜRLER sızmaz: yedek ekranı göç işini göstermemeli (o başka bir modülün ekranı
    /// ve başka bir yetki alanı).
    /// </summary>
    [Fact]
    public async Task Istenmeyen_Tur_Donmez()
    {
        using var alan = new IsAlani();
        var servis = alan.Servis();

        await using var goc = await servis.BeginAsync(RunningJobKinds.Migration, "shop.example.com", null, alan.Iptal);

        var durum = await servis.GetStatusAsync([RunningJobKinds.Backup], null, alan.Iptal);

        Assert.Empty(durum.Running);
    }

    // ===============================================================================================
    // BAYAT KAYIT: panel iş sürerken yeniden başladıysa kayıt YALAN söylememeli
    // ===============================================================================================

    /// <summary>
    /// Panel iş sürerken yeniden başlarsa kayıt diskte KALIR. O kaydı olduğu gibi "sürüyor" diye
    /// göstermek YALAN olurdu: iş o süreçle birlikte gitti, sonucu asla yazılmayacak. Sahiplik
    /// kontrolü bunu ayırt eder ve durum "kesildi" olarak yüzeye çıkar.
    /// </summary>
    [Fact]
    public async Task Panel_YenidenBaslamissa_Suruyor_Denmez_Kesildi_Yazilir()
    {
        using var alan = new IsAlani();
        var baslangic = DateTime.UtcNow.AddMinutes(-90);
        await alan.KayitYazAsync(RunningJobKinds.Backup, "shop.example.com", baslangic, sahipBuSurec: false);

        var durum = await alan.Servis().GetStatusAsync([RunningJobKinds.Backup], null, alan.Iptal);

        Assert.Empty(durum.Running);
        var kesilen = Assert.Single(durum.Interrupted);
        Assert.Equal("shop.example.com", kesilen.Title);
        Assert.Equal(baslangic, kesilen.StartedAtUtc, TimeSpan.FromSeconds(1));
        Assert.True(kesilen.DetectedAtUtc > DateTime.UtcNow.AddMinutes(-1));

        // Kayıt TEMİZLENMİŞ olmalı: temizlenmezse panel her açılışta aynı uyarıyı yeniden üretir
        // ve gerçek bir sonraki iş başlatılamaz (409 döner).
        Assert.Null(await alan.SurenKaydiOkuAsync(RunningJobKinds.Backup));

        // İkinci okuma da aynı şeyi söylemeli: uyarı tek seferlik bir parıltı olamaz.
        var ikinciOkuma = await alan.Servis().GetStatusAsync([RunningJobKinds.Backup], null, alan.Iptal);
        Assert.Single(ikinciOkuma.Interrupted);
    }

    /// <summary>
    /// PID YENİDEN KULLANIMI: aynı numarayı alan YEPYENİ bir süreç, ölü bir işi "sürüyor"
    /// göstermemeli. Süreç başlangıç anı çifti bunu ayırt eder.
    /// </summary>
    [Fact]
    public async Task Ayni_Pid_Farkli_SurecBaslangici_Sahiplik_Saymaz()
    {
        using var alan = new IsAlani();
        await alan.KayitYazAsync(
            RunningJobKinds.Backup,
            "shop.example.com",
            DateTime.UtcNow.AddMinutes(-5),
            sahipBuSurec: true,
            surecBaslangicTicksGecersizKil: 1);

        var durum = await alan.Servis().GetStatusAsync([RunningJobKinds.Backup], null, alan.Iptal);

        Assert.Empty(durum.Running);
        Assert.Single(durum.Interrupted);
    }

    /// <summary>
    /// Süreç başlangıç anı ÖLÇÜLEMEYEN ortamda (0 yazılır) tek kanıt PID'dir; onu da yok saymak
    /// süren HER işi "kesildi" ilan etmek olurdu.
    /// </summary>
    [Fact]
    public async Task SurecBaslangici_Olculemediyse_Pid_Yeterli_Sayilir()
    {
        using var alan = new IsAlani();
        await alan.KayitYazAsync(
            RunningJobKinds.Backup,
            "shop.example.com",
            DateTime.UtcNow.AddMinutes(-5),
            sahipBuSurec: true,
            surecBaslangicTicksGecersizKil: 0);

        var durum = await alan.Servis().GetStatusAsync([RunningJobKinds.Backup], null, alan.Iptal);

        Assert.Single(durum.Running);
        Assert.Empty(durum.Interrupted);
    }

    /// <summary>
    /// Sahipsiz (ölü süreçten kalan) kayıt yeni işi ENGELLEMEMELİ; yoksa bir kez çöken panel bir
    /// daha asla yedek alamazdı.
    /// </summary>
    [Fact]
    public async Task Sahipsiz_Kayit_Yeni_Isi_Engellemez()
    {
        using var alan = new IsAlani();
        await alan.KayitYazAsync(
            RunningJobKinds.Backup, "eski.example.com", DateTime.UtcNow.AddHours(-5), sahipBuSurec: false);

        var servis = alan.Servis();
        await using var kapsam = await servis.BeginAsync(RunningJobKinds.Backup, "shop.example.com", null, alan.Iptal);

        var suren = Assert.Single((await servis.GetStatusAsync([RunningJobKinds.Backup], null, alan.Iptal)).Running);
        Assert.Equal("shop.example.com", suren.Title);
    }

    /// <summary>
    /// Yeni bir iş, aynı türün eski "kesildi" uyarısını düşürür: kullanıcı zaten yeniden başlattı,
    /// uyarıyı bir daha göstermek gürültü olurdu.
    /// </summary>
    [Fact]
    public async Task Yeni_Is_Eski_Kesildi_Uyarisini_Dusurur()
    {
        using var alan = new IsAlani();
        var servis = alan.Servis();
        await alan.KayitYazAsync(
            RunningJobKinds.Backup, "eski.example.com", DateTime.UtcNow.AddHours(-5), sahipBuSurec: false);
        Assert.Single((await servis.GetStatusAsync([RunningJobKinds.Backup], null, alan.Iptal)).Interrupted);

        await using (await servis.BeginAsync(RunningJobKinds.Backup, "shop.example.com", null, alan.Iptal))
        {
        }

        Assert.Empty((await servis.GetStatusAsync([RunningJobKinds.Backup], null, alan.Iptal)).Interrupted);
    }

    /// <summary>
    /// Kapsam, BAŞKASININ yazdığı kaydı silmemeli. 409 koruması bunu zaten engelliyor ama savunma
    /// ucuz: bayat kayıt temizlendikten sonra başlayan gerçek bir iş, geciken bir kapsam
    /// temizliğiyle ekrandan yok olmamalı.
    /// </summary>
    [Fact]
    public async Task Kapsam_Yalniz_Kendi_Kaydini_Temizler()
    {
        using var alan = new IsAlani();
        var servis = alan.Servis();

        var kapsam = await servis.BeginAsync(RunningJobKinds.Backup, "birinci.example.com", null, alan.Iptal);

        // Yuvayı BAŞKA bir işin kaydı doldurmuş gibi davran (aynı süreç, farklı başlangıç anı).
        await alan.KayitYazAsync(
            RunningJobKinds.Backup, "ikinci.example.com", DateTime.UtcNow.AddSeconds(5), sahipBuSurec: true);

        await kapsam.DisposeAsync();

        var suren = Assert.Single((await servis.GetStatusAsync([RunningJobKinds.Backup], null, alan.Iptal)).Running);
        Assert.Equal("ikinci.example.com", suren.Title);
    }

    // ===============================================================================================
    // KİRACI İZOLASYONU: iş etiketleri site/yedek adı taşır
    // ===============================================================================================

    /// <summary>
    /// Kapsamlı kullanıcı BAŞKA bir aboneliğin işini görmemeli — şeritteki etiket site adıdır ve
    /// bu tek başına bir kiracı sızıntısıdır.
    /// </summary>
    [Fact]
    public async Task Kapsamli_Kullanici_Baskasinin_Isini_Gormez()
    {
        using var alan = new IsAlani();
        var servis = alan.Servis();
        var baskaKiraci = Guid.NewGuid();
        var benimKiracim = Guid.NewGuid();

        await using var kapsam = await servis.BeginAsync(
            RunningJobKinds.Backup, "baskasinin-sitesi.example.com", baskaKiraci, alan.Iptal);

        var durum = await servis.GetStatusAsync(
            [RunningJobKinds.Backup],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { benimKiracim.ToString("N") },
            alan.Iptal);

        Assert.Empty(durum.Running);
    }

    /// <summary>Kendi aboneliğinin işi ise GÖRÜNÜR — yoksa müşteri kendi yedeğini takip edemezdi.</summary>
    [Fact]
    public async Task Kapsamli_Kullanici_Kendi_Isini_Gorur()
    {
        using var alan = new IsAlani();
        var servis = alan.Servis();
        var benimKiracim = Guid.NewGuid();

        await using var kapsam = await servis.BeginAsync(
            RunningJobKinds.Backup, "benim-sitem.example.com", benimKiracim, alan.Iptal);

        var durum = await servis.GetStatusAsync(
            [RunningJobKinds.Backup],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { benimKiracim.ToString("N") },
            alan.Iptal);

        Assert.Equal("benim-sitem.example.com", Assert.Single(durum.Running).Title);
    }

    /// <summary>
    /// SUNUCU GENELİ işler (panel yedeği, posta sunucusu yedeği, bileşen kurulumu) sahipsizdir ve
    /// kiracıya HİÇ görünmez; yöneticiye görünür.
    /// </summary>
    [Fact]
    public async Task Sunucu_Geneli_Is_Yalniz_Yoneticide_Gorunur()
    {
        using var alan = new IsAlani();
        var servis = alan.Servis();

        await using var kapsam = await servis.BeginAsync(
            RunningJobKinds.Backup, "panel", subjectUserId: null, alan.Iptal);

        var kiraci = await servis.GetStatusAsync(
            [RunningJobKinds.Backup],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Guid.NewGuid().ToString("N") },
            alan.Iptal);
        Assert.Empty(kiraci.Running);

        var yonetici = await servis.GetStatusAsync([RunningJobKinds.Backup], null, alan.Iptal);
        Assert.Single(yonetici.Running);
    }

    /// <summary>"Kesildi" kaydı da aynı kiracı süzgecinden geçer; etiket orada da site adıdır.</summary>
    [Fact]
    public async Task Kesildi_Kaydi_Da_Kiraci_Suzgecinden_Gecer()
    {
        using var alan = new IsAlani();
        var baskaKiraci = Guid.NewGuid();
        await alan.KayitYazAsync(
            RunningJobKinds.Backup,
            "baskasinin-sitesi.example.com",
            DateTime.UtcNow.AddHours(-2),
            sahipBuSurec: false,
            sahipKiraci: baskaKiraci);

        var durum = await alan.Servis().GetStatusAsync(
            [RunningJobKinds.Backup],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Guid.NewGuid().ToString("N") },
            alan.Iptal);

        Assert.Empty(durum.Interrupted);
    }

    // ===============================================================================================
    // Yardimcilar
    // ===============================================================================================

    private sealed class IsAlani : IDisposable
    {
        private const string KayitYolu = "config/running-jobs.json";

        private readonly CancellationTokenSource iptal = new(TimeSpan.FromSeconds(120));

        public IsAlani()
        {
            Kok = Path.Combine(Path.GetTempPath(), $"adapanel-isler-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Kok);
            DosyaDeposu = new JsonFileStore(
                Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = Kok }));
        }

        public string Kok { get; }

        public JsonFileStore DosyaDeposu { get; }

        public CancellationToken Iptal => iptal.Token;

        public RunningJobService Servis() => new(DosyaDeposu, NullLogger<RunningJobService>.Instance);

        /// <summary>Diske DOĞRUDAN kayıt yazar (panel yeniden başlamış gibi davranmak için).</summary>
        public async Task KayitYazAsync(
            string tur,
            string baslik,
            DateTime baslangicUtc,
            bool sahipBuSurec,
            long? surecBaslangicTicksGecersizKil = null,
            Guid? sahipKiraci = null)
        {
            var dosya = await DosyaDeposu.ReadAsync(KayitYolu, new RunningJobsFile(), Iptal);
            dosya.Running[tur] = new RunningJobState
            {
                Kind = tur,
                Title = baslik,
                StartedAtUtc = baslangicUtc,
                SubjectUserId = sahipKiraci is { } sahip ? sahip.ToString("N") : string.Empty,
                // Sahiplik BU sürece aitse gerçek PID yazılır; değilse asla var olmayacak bir PID
                // (negatif) yazılır -- böylece test başka bir sürecin PID'sini TAHMİN etmez.
                OwnerProcessId = sahipBuSurec ? Environment.ProcessId : -1,
                // 0 = "süreç başlangıç anı ölçülemedi"; servis o durumda yalnız PID'e bakar.
                OwnerProcessStartedAtTicks = surecBaslangicTicksGecersizKil ?? SurecBaslangici(sahipBuSurec)
            };

            await DosyaDeposu.WriteAsync(KayitYolu, dosya, Iptal);
        }

        public async Task<RunningJobState?> SurenKaydiOkuAsync(string tur)
        {
            var dosya = await DosyaDeposu.ReadAsync(KayitYolu, new RunningJobsFile(), Iptal);
            return dosya.Running.TryGetValue(tur, out var kayit) ? kayit : null;
        }

        /// <summary>
        /// Bu sürecin GERÇEK başlangıç anı. Sabit yazılmaz: sahiplik kontrolü PID + başlangıç anı
        /// çiftine bakıyor ve test onu tahmin etseydi kendi kendini kandırırdı.
        /// </summary>
        private static long SurecBaslangici(bool sahipBuSurec)
        {
            if (!sahipBuSurec)
            {
                return 0;
            }

            try
            {
                using var current = System.Diagnostics.Process.GetCurrentProcess();
                return current.StartTime.ToUniversalTime().Ticks;
            }
            catch
            {
                return 0;
            }
        }

        public void Dispose()
        {
            iptal.Dispose();
            try
            {
                Directory.Delete(Kok, recursive: true);
            }
            catch
            {
                // Geçici dizin temizliği en iyi çaba.
            }
        }
    }
}
