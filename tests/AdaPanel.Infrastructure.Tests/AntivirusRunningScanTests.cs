using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Antivirus;
using AdaPanel.Core.Entities;
using AdaPanel.Core.Enums;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Antivirus;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Support;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// SÜREN antivirüs taramasının sunucudan geri okunabilmesi.
/// <para>
/// <b>KAPATILAN HATA:</b> tarama 125 dakikaya kadar sürüyor ama <c>/antivirus/status</c> yalnız SON
/// BİTEN taramayı döndürüyordu. Kullanıcı taramayı başlatıp sayfayı yenilediğinde panel "tarama
/// yok" diyordu — sürmekte olan iki saatlik iş ekrandan tamamen kayboluyordu ve kullanıcı ikinci
/// bir tarama başlatabiliyordu.
/// </para>
/// <para>
/// <b>NEDEN BURADA TEST EDİLİYOR:</b> ClamAV bu makinede kurulu DEĞİL, yani gerçek bir tarama
/// koşturulamaz. Test edilen şey zaten clamscan değil, panelin DURUM MUHASEBESİ: kayıt yazılıyor
/// mu, sahibi doğru mu belirleniyor, panel yeniden başladığında yalan söylüyor mu.
/// </para>
/// </summary>
public sealed class AntivirusRunningScanTests
{
    [Fact]
    public async Task Tarama_Yokken_Durum_SurenTarama_Bildirmez()
    {
        using var alan = new AntivirusAlani();

        var durum = await alan.Servis().GetStatusAsync(alan.Iptal);

        Assert.Null(durum.RunningScan);
    }

    /// <summary>
    /// ASIL DÜZELTME: kayıt diskte olduğu sürece "tarama sürüyor" bilgisi sayfa yenilense de
    /// okunabilir. (Mutasyon: <c>ResolveRunningScanAsync</c> daima <c>(null, lastScan)</c> dönerse
    /// bu test kırılır ve hata geri gelmiş olur.)
    /// </summary>
    [Fact]
    public async Task SurenTarama_Kaydi_Varsa_Durum_Hedefi_Ve_BaslangicAnini_Dondurur()
    {
        using var alan = new AntivirusAlani();
        var baslangic = DateTime.UtcNow.AddMinutes(-42);
        await alan.KayitYazAsync("shop.example.com", baslangic, sahipBuSurec: true);

        var durum = await alan.Servis().GetStatusAsync(alan.Iptal);

        Assert.NotNull(durum.RunningScan);
        Assert.Equal("shop.example.com", durum.RunningScan!.Target);
        Assert.Equal(baslangic, durum.RunningScan.StartedAtUtc, TimeSpan.FromSeconds(1));
    }

    /// <summary>
    /// SAHTE YÜZDE YASAĞI sözleşme düzeyinde korunuyor. clamscan ilerleme yüzdesi yayımlamaz;
    /// DTO'ya bir yüzde alanı eklenirse onu doldurmanın tek yolu UYDURMAK olur.
    /// </summary>
    [Fact]
    public void SurenTarama_Sozlesmesinde_Yuzde_Alani_Yok()
    {
        var alanlar = typeof(AntivirusRunningScanDto)
            .GetProperties()
            .Select(item => item.Name)
            .ToArray();

        Assert.Equal(new[] { "Target", "StartedAtUtc" }.OrderBy(x => x), alanlar.OrderBy(x => x));
        Assert.DoesNotContain(
            alanlar,
            ad => ad.Contains("percent", StringComparison.OrdinalIgnoreCase) ||
                  ad.Contains("progress", StringComparison.OrdinalIgnoreCase) ||
                  ad.Contains("eta", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Panel tarama sürerken yeniden başlarsa kayıt diskte KALIR. O kaydı olduğu gibi "sürüyor"
    /// diye göstermek YALAN olurdu: clamscan o süreçle birlikte gitti, sonucu asla yazılmayacak.
    /// Sahiplik kontrolü bunu ayırt eder ve durum "yarıda kaldı" olarak yüzeye çıkar.
    /// </summary>
    [Fact]
    public async Task Panel_YenidenBaslamissa_Tarama_Suruyor_Denmez_Yarida_Kaldi_Yazilir()
    {
        using var alan = new AntivirusAlani();
        var baslangic = DateTime.UtcNow.AddMinutes(-90);
        await alan.KayitYazAsync("shop.example.com", baslangic, sahipBuSurec: false);

        var durum = await alan.Servis().GetStatusAsync(alan.Iptal);

        Assert.Null(durum.RunningScan);
        Assert.NotNull(durum.LastScan);
        Assert.Equal("interrupted", durum.LastScan!.Status);
        Assert.Equal("shop.example.com", durum.LastScan.Target);

        // Kayıt TEMİZLENMİŞ olmalı: temizlenmezse panel her açılışta aynı uyarıyı yeniden üretir
        // ve gerçek bir sonraki tarama başlatılamaz (409 döner).
        Assert.Null(await alan.KayitOkuAsync());

        var ikinciOkuma = await alan.Servis().GetStatusAsync(alan.Iptal);
        Assert.Equal("interrupted", ikinciOkuma.LastScan!.Status);
    }

    /// <summary>
    /// PID YENİDEN KULLANIMI: aynı numarayı alan YEPYENİ bir süreç, ölü bir taramayı "sürüyor"
    /// göstermemeli. Süreç başlangıç anı çifti bunu ayırt eder.
    /// </summary>
    [Fact]
    public async Task Ayni_Pid_Farkli_SurecBaslangici_Sahiplik_Saymaz()
    {
        using var alan = new AntivirusAlani();
        await alan.KayitYazAsync(
            "shop.example.com",
            DateTime.UtcNow.AddMinutes(-5),
            sahipBuSurec: true,
            surecBaslangicTicksGecersizKil: 1);

        var durum = await alan.Servis().GetStatusAsync(alan.Iptal);

        Assert.Null(durum.RunningScan);
        Assert.Equal("interrupted", durum.LastScan!.Status);
    }

    /// <summary>
    /// Yarıda kalan tarama, SONRADAN tamamlanmış gerçek bir sonucun üstüne yazmamalı — bu veri
    /// kaybı olurdu.
    /// </summary>
    [Fact]
    public async Task Daha_Yeni_Bir_Sonuc_Varsa_Yarida_Kaldi_Uzerine_Yazmaz()
    {
        using var alan = new AntivirusAlani();
        var eskiTarama = DateTime.UtcNow.AddHours(-3);
        await alan.KayitYazAsync("shop.example.com", eskiTarama, sahipBuSurec: false);
        await alan.SonTaramaYazAsync(new AntivirusScanResultDto
        {
            Target = "shop.example.com",
            StartedAtUtc = eskiTarama.AddHours(1),
            ScannedFiles = 1200,
            Status = "clean"
        });

        var durum = await alan.Servis().GetStatusAsync(alan.Iptal);

        Assert.Null(durum.RunningScan);
        Assert.Equal("clean", durum.LastScan!.Status);
        Assert.Equal(1200, durum.LastScan.ScannedFiles);
    }

    /// <summary>
    /// AYNI ANDA İKİ TARAMA YOK: ikinci istek 409 ile reddedilir. Aksi hâlde ikinci tarama
    /// birincinin "sürüyor" kaydını ezerdi ve hangi sonucun yazıldığı belirsizleşirdi.
    /// </summary>
    [Fact]
    public async Task Tarama_Surerken_Ikinci_Tarama_409_Ile_Reddedilir()
    {
        using var alan = new AntivirusAlani();
        alan.ClamAvKur();
        await alan.KayitYazAsync("shop.example.com", DateTime.UtcNow.AddMinutes(-10), sahipBuSurec: true);

        var hata = await Assert.ThrowsAsync<AppException>(() =>
            alan.Servis().ScanAsync(new AntivirusScanRequest { Path = alan.HedefDizin }, alan.Iptal));

        Assert.Equal("antivirus.scan_in_progress", hata.MessageKey);
        Assert.Equal(409, hata.HttpStatusCode);
    }

    /// <summary>
    /// Sahipsiz (ölü süreçten kalan) kayıt yeni taramayı ENGELLEMEMELİ; yoksa bir kez çöken panel
    /// bir daha asla tarama yapamazdı. Burada tarama gerçekten başlar, sahte clamscan hemen
    /// döner ve sonuç yazılır.
    /// </summary>
    [Fact]
    public async Task Sahipsiz_Kayit_Yeni_Taramayi_Engellemez()
    {
        using var alan = new AntivirusAlani();
        alan.ClamAvKur();
        await alan.KayitYazAsync("eski.example.com", DateTime.UtcNow.AddHours(-5), sahipBuSurec: false);

        var hata = await Record.ExceptionAsync(() =>
            alan.Servis().ScanAsync(new AntivirusScanRequest { Path = alan.HedefDizin }, alan.Iptal));

        // Sahte clamscan gerçek bir tarama yapmaz; önemli olan 409'a TAKILMAMASI.
        Assert.False(hata is AppException app && app.MessageKey == "antivirus.scan_in_progress");

        // Tarama PATLASA BİLE "sürüyor" kaydı diskte kalmamalı: kalsaydı panel bir daha asla
        // tarama başlatamaz, sonsuza kadar "tarama sürüyor" derdi.
        Assert.Null(await alan.KayitOkuAsync());
    }

    /// <summary>
    /// Tarama BİTTİĞİNDE "sürüyor" kaydı temizlenmeli ve sonuç yazılmalı. Temizlenmezse bir sonraki
    /// tarama 409 alır ve panel ekranda sonsuz "sürüyor" şeridi gösterir.
    /// <para>
    /// ClamAV bu makinede KURULU DEĞİL. Bu yüzden clamscan yerine gerçekten çalışan, hemen dönen ve
    /// yan etkisi olmayan bir Windows aracı (<c>where.exe</c>) clamscan.exe adıyla konuluyor.
    /// Ölçülen şey virüs bulma değil, taramanın bitişinde panelin DURUM MUHASEBESİ.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Tarama_Bitince_Suruyor_Kaydi_Temizlenir_Ve_Sonuc_Yazilir()
    {
        using var alan = new AntivirusAlani();
        alan.ClamAvKur(gercekCalisanTaklit: true);

        await alan.Servis().ScanAsync(new AntivirusScanRequest { Path = alan.HedefDizin }, alan.Iptal);

        Assert.Null(await alan.KayitOkuAsync());

        var durum = await alan.Servis().GetStatusAsync(alan.Iptal);
        Assert.Null(durum.RunningScan);
        Assert.NotNull(durum.LastScan);
        Assert.Equal(alan.HedefDizin, durum.LastScan!.Target);
    }

    /// <summary>
    /// AYNI ANDA gelen isteklerden YALNIZ BİRİ yuvayı alır.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>KAPATILAN YARIŞ (ölçüldü):</b> "tarama var mı" kontrolü ile kaydın yazılması AYRI iki
    /// çağrıydı ve aralarında <c>await</c> vardı. Eşzamanlı N istek hepsi "tarama yok" görüp hepsi
    /// yazıyordu — N tane <c>clamscan.exe</c>, her biri 2 saat zaman aşımlı, hepsi aynı ağaçta.
    /// Yani 409 koruması vardı ama YALNIZCA sıralı isteklerde çalışıyordu; asıl saldırgan zaten
    /// istekleri aynı anda gönderir.
    /// </para>
    /// <para>
    /// <b>NEDEN <c>ScanAsync</c> ÜZERİNDEN ÖLÇÜLMÜYOR:</b> yarışı görmek için taramanın SÜRÜYOR
    /// olması gerekir; bu makinede ClamAV yok ve taklit araç anında dönüyor, yani iki istek hiç
    /// çakışmazdı. Ayırtma adımı doğrudan ölçülüyor. Adımın <c>ScanAsync</c>'e BAĞLI olduğu ayrı
    /// bir testte ölçülüyor (<see cref="Tarama_Surerken_Ikinci_Tarama_409_Ile_Reddedilir"/>).
    /// </para>
    /// <para>
    /// <b>MUTASYON (ölçüldü):</b> ayırtma <c>UpdateAsync</c> yerine "önce oku, sonra
    /// <c>WriteAsync</c>" hâline döndürülünce bu test KIRMIZI yandı (8 isteğin 8'i de yuvayı aldı).
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Ayni_Anda_Gelen_Istekler_Yuvayi_Bir_Kez_Ayirtir()
    {
        using var alan = new AntivirusAlani();
        var servis = alan.Servis();
        const int istekSayisi = 8;

        // Hepsi AYNI ANDA serbest bırakılır: sıralı çağrılar bu yarışı hiç göstermez.
        using var kapi = new SemaphoreSlim(0, istekSayisi);
        var istekler = Enumerable.Range(0, istekSayisi).Select(async sira =>
        {
            await kapi.WaitAsync(alan.Iptal);
            try
            {
                await servis.YuvayiAyirtAsync($"hedef-{sira}", DateTime.UtcNow, alan.Iptal);
                return true;
            }
            catch (AppException hata) when (hata.MessageKey == "antivirus.scan_in_progress")
            {
                return false;
            }
        }).ToArray();

        kapi.Release(istekSayisi);
        var sonuclar = await Task.WhenAll(istekler);

        Assert.Equal(1, sonuclar.Count(basarili => basarili));

        // Diskte de TEK kayıt olmalı ve o kayıt gerçekten yazılmış olmalı.
        var kayit = await alan.KayitOkuAsync();
        Assert.NotNull(kayit);
        Assert.StartsWith("hedef-", kayit!.Target, StringComparison.Ordinal);
    }

    /// <summary>
    /// Tarama biterken yuvayı BAŞKASININ kaydı doldurduysa temizleme ona DOKUNMAZ.
    /// </summary>
    /// <remarks>
    /// Koşulsuz bir temizleme, biten taramanın hemen ardından başlamış GERÇEK bir taramanın kaydını
    /// silerdi: panel "tarama yok" der, ekran boşalır ve üçüncü bir clamscan başlatılabilirdi.
    /// (Mutasyon: temizleme başlangıç anını karşılaştırmayı bırakırsa test kırılır.)
    /// </remarks>
    [Fact]
    public async Task Temizleme_Baskasinin_Kaydina_Dokunmaz()
    {
        using var alan = new AntivirusAlani();
        var servis = alan.Servis();

        var ikinciBaslangic = DateTime.UtcNow;
        await servis.YuvayiAyirtAsync("yeni.example.com", ikinciBaslangic, alan.Iptal);

        // Daha ESKİ (çoktan bitmiş) bir taramanın temizleme çağrısı: yuvayı boşaltmamalı.
        await servis.ClearRunningScanAsync(ikinciBaslangic.AddMinutes(-30), alan.Iptal);

        var kayit = await alan.KayitOkuAsync();
        Assert.NotNull(kayit);
        Assert.Equal("yeni.example.com", kayit!.Target);
    }

    // ===============================================================================================
    // Yardimcilar
    // ===============================================================================================

    private sealed class AntivirusAlani : IDisposable
    {
        private const string KayitYolu = "config/antivirus-running.json";
        private const string SonTaramaYolu = "config/antivirus.json";

        private readonly CancellationTokenSource iptal = new(TimeSpan.FromSeconds(120));
        private readonly SqliteConnection baglanti;
        private readonly AdaPanelDbContext dbContext;

        public AntivirusAlani()
        {
            Kok = Path.Combine(Path.GetTempPath(), $"adapanel-av-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Kok);
            HedefDizin = Path.Combine(Kok, "site");
            Directory.CreateDirectory(HedefDizin);
            KurulumDizini = Path.Combine(Kok, "clamav");

            DosyaDeposu = new JsonFileStore(
                Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = Kok }));

            baglanti = new SqliteConnection("Filename=:memory:");
            baglanti.Open();
            dbContext = new AdaPanelDbContext(
                new DbContextOptionsBuilder<AdaPanelDbContext>().UseSqlite(baglanti).Options);
            dbContext.Database.EnsureCreated();
        }

        public string Kok { get; }

        public string HedefDizin { get; }

        public string KurulumDizini { get; }

        public JsonFileStore DosyaDeposu { get; }

        public CancellationToken Iptal => iptal.Token;

        /// <summary>
        /// Kurulu bir ClamAV taklidi: servis yalnız dosyaların VARLIĞINA bakar (clamscan.exe'yi
        /// bulur, tanım dosyası arar). Gerçek tarama koşturulmuyor — bu makinede ClamAV yok.
        /// </summary>
        public void ClamAvKur(bool gercekCalisanTaklit = false)
        {
            var veritabani = Path.Combine(KurulumDizini, "database");
            Directory.CreateDirectory(veritabani);
            var clamscan = Path.Combine(KurulumDizini, "clamscan.exe");

            if (gercekCalisanTaklit)
            {
                // Gerçekten başlayıp hemen dönen, yan etkisi olmayan bir araç. Sürüm/derleme
                // farkından etkilenmesin diye yol Windows dizininden ÇÖZÜLÜR, sabit yazılmaz.
                var kaynak = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "where.exe");
                Assert.True(File.Exists(kaynak), $"Taklit çalıştırılabilir bulunamadı: {kaynak}");
                File.Copy(kaynak, clamscan);
            }
            else
            {
                File.WriteAllText(clamscan, "sahte");
            }

            File.WriteAllText(Path.Combine(veritabani, "daily.cld"), "sahte");

            dbContext.InstalledComponents.Add(new InstalledComponent
            {
                ComponentId = "clamav",
                DisplayName = "ClamAV",
                Version = "1.4.1",
                InstallDir = KurulumDizini,
                Status = ComponentInstallStatus.Installed
            });
            dbContext.SaveChanges();
        }

        public AntivirusService Servis() =>
            new(dbContext, null!, DosyaDeposu, NullLogger<AntivirusService>.Instance);

        public Task KayitYazAsync(
            string hedef,
            DateTime baslangicUtc,
            bool sahipBuSurec,
            long? surecBaslangicTicksGecersizKil = null) =>
            DosyaDeposu.WriteAsync<AntivirusRunningScanState?>(
                KayitYolu,
                new AntivirusRunningScanState
                {
                    Target = hedef,
                    StartedAtUtc = baslangicUtc,
                    // Sahiplik BU sürece aitse gerçek PID yazılır; değilse asla var olmayacak bir
                    // PID (negatif) yazılır -- böylece test başka bir sürecin PID'sini tahmin etmez.
                    OwnerProcessId = sahipBuSurec ? Environment.ProcessId : -1,
                    // 0 = "süreç başlangıç anı ölçülemedi"; servis o durumda yalnız PID'e bakar.
                    OwnerProcessStartedAtTicks = surecBaslangicTicksGecersizKil ?? 0
                },
                Iptal);

        public Task<AntivirusRunningScanState?> KayitOkuAsync() =>
            DosyaDeposu.ReadAsync<AntivirusRunningScanState?>(KayitYolu, null, Iptal);

        public Task SonTaramaYazAsync(AntivirusScanResultDto sonuc) =>
            DosyaDeposu.WriteAsync(SonTaramaYolu, sonuc, Iptal);

        public void Dispose()
        {
            iptal.Dispose();
            dbContext.Dispose();
            baglanti.Dispose();
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
