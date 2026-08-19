using AdaPanel.Core.Dtos.Backup;
using AdaPanel.Infrastructure.Backup;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Support;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Budamanin YEDEKLER ALINAMAZKEN ne yaptigi.
/// <para>
/// Mevcut <c>BackupRetentionTests</c> yalnizca saklama matematigini olcuyordu; asil tehlike ise
/// ETKILESIMDEDIR: gecelik is her kaynagi ayri try/catch ile yedekler, hepsi patlasa bile kosulsuz
/// budamaya gecer. Yalnizca tarihe bakan bir budama, MariaDB 14 gun kapali kaldiginda geri
/// yuklenecek TEK bir yedek birakmadan hepsini siler ve kimseye haber gitmez.
/// </para>
/// </summary>
public sealed class BackupPruneProtectionTests
{
    [Fact]
    public async Task Prune_WhenSourceHasNoRecentBackup_KeepsItsExpiredBackups()
    {
        using var alan = new BudamaAlani();

        // shop.example.com: yedekleri 20 ve 30 gun once alinmis, o gunden beri HIC basarili yedek yok.
        var eski = alan.KayitUret("shop.example.com", gunOnce: 30);
        var dahaEski = alan.KayitUret("shop.example.com", gunOnce: 40);

        var servis = alan.ServisKur(eski, dahaEski);
        var silinen = await servis.PruneAsync(retentionDays: 14);

        Assert.Equal(0, silinen);
        Assert.True(File.Exists(eski.Path));
        Assert.True(File.Exists(dahaEski.Path));
        Assert.Equal(2, (await servis.GetBackupsAsync()).Count);
    }

    [Fact]
    public async Task Prune_WhenSourceHasRecentBackup_StillRemovesItsExpiredBackups()
    {
        using var alan = new BudamaAlani();

        var eski = alan.KayitUret("shop.example.com", gunOnce: 30);
        var taze = alan.KayitUret("shop.example.com", gunOnce: 1);

        var servis = alan.ServisKur(eski, taze);
        var silinen = await servis.PruneAsync(retentionDays: 14);

        // Koruma saklama politikasini IPTAL ETMEZ; yalnizca yeni yedek yokken devreye girer.
        Assert.Equal(1, silinen);
        Assert.False(File.Exists(eski.Path));
        Assert.True(File.Exists(taze.Path));
    }

    [Fact]
    public async Task Prune_ProtectionIsPerSource_OneHealthySiteDoesNotAuthorizeAnother()
    {
        using var alan = new BudamaAlani();

        // saglikli.example.com her gece yedekleniyor; bozuk.example.com haftalardir yedeklenemiyor.
        var saglikliEski = alan.KayitUret("saglikli.example.com", gunOnce: 30);
        var saglikliTaze = alan.KayitUret("saglikli.example.com", gunOnce: 1);
        var bozukEski = alan.KayitUret("bozuk.example.com", gunOnce: 30);

        var servis = alan.ServisKur(saglikliEski, saglikliTaze, bozukEski);
        var silinen = await servis.PruneAsync(retentionDays: 14);

        Assert.Equal(1, silinen);
        Assert.False(File.Exists(saglikliEski.Path));
        Assert.True(File.Exists(bozukEski.Path));
    }

    [Fact]
    public async Task Prune_ProtectionSeparatesTypes_SiteBackupDoesNotCoverDatabase()
    {
        using var alan = new BudamaAlani();

        // Ayni ad, farkli tur: sitenin yedegi alinabiliyor ama veritabani motoru dusmus olabilir.
        var siteTaze = alan.KayitUret("shop", gunOnce: 1);
        var dbEski = alan.KayitUret("shop", gunOnce: 30, tur: "database");

        var servis = alan.ServisKur(siteTaze, dbEski);
        var silinen = await servis.PruneAsync(retentionDays: 14);

        Assert.Equal(0, silinen);
        Assert.True(File.Exists(dbEski.Path));
    }

    [Fact]
    public async Task Prune_RecentRecordWithMissingFile_DoesNotAuthorizePruning()
    {
        using var alan = new BudamaAlani();

        var eski = alan.KayitUret("shop.example.com", gunOnce: 30);
        var taze = alan.KayitUret("shop.example.com", gunOnce: 1);

        // Taze yedegin dosyasi artik yok (elle silinmis / disk temizligi). Kayit duruyor diye
        // "guncel yedegimiz var" demek, olculmemis bir seye guvenmek olurdu.
        File.Delete(taze.Path);

        var servis = alan.ServisKur(eski, taze);
        var silinen = await servis.PruneAsync(retentionDays: 14);

        Assert.Equal(0, silinen);
        Assert.True(File.Exists(eski.Path));
    }

    [Fact]
    public async Task Prune_RecentRecordWithRemoteCopyOnly_StillAuthorizesPruning()
    {
        using var alan = new BudamaAlani();

        var eski = alan.KayitUret("shop.example.com", gunOnce: 30);
        var taze = alan.KayitUret("shop.example.com", gunOnce: 1);
        File.Delete(taze.Path);
        taze.RemoteUri = "s3://kova/shop-guncel.zip";

        var servis = alan.ServisKur(eski, taze);
        var silinen = await servis.PruneAsync(retentionDays: 14);

        // Off-site kopya da gecerli bir geri yukleme noktasidir.
        Assert.Equal(1, silinen);
        Assert.False(File.Exists(eski.Path));
    }

    // ===========================================================================================
    // SAYI LIMITI (keepCount) DALI
    //
    // OLCULEN BOSLUK: yukaridaki 6 testin HEPSI keepCount = 0 ile calisiyordu, yani "en yeni N
    // noktayi koru" dali HIC olculmemisti. O dal `kullanilabilir` yuklemini hic cagirmiyor ve
    // yorumu "hic yedek kalmama riski yoktur" diyordu -- yanlis: "nokta kalmasi" ile "GERI
    // YUKLENEBILIR nokta kalmasi" ayni sey degil.
    // ===========================================================================================

    [Fact]
    public async Task Prune_KeepCount_SON_KULLANILABILIR_yedegi_asla_silmez()
    {
        using var alan = new BudamaAlani();
        var abone = Guid.NewGuid();

        var eski = alan.AbonelikKaydi(abone, gunOnce: 10);
        var orta = alan.AbonelikKaydi(abone, gunOnce: 5);
        var enYeni = alan.AbonelikKaydi(abone, gunOnce: 0);

        // En yeni kaydin DOSYASI yok (yedek yarim kaldi / elle silindi). Kaydi duruyor diye
        // "elimizde 1 nokta var" demek, olculmemis bir seye guvenmek olurdu.
        File.Delete(enYeni.Path);

        var servis = alan.ServisKur(eski, orta, enYeni);
        var silinen = await servis.PruneSubscriptionAsync(abone, retentionDays: 0, keepCount: 1);

        // ESKI DAVRANIS: en yeni (bozuk) nokta korunur, orta+eski silinir -> geri yuklenebilir
        // yedek sayisi SIFIR. Simdi: en yeni KULLANILABILIR nokta da korunur.
        Assert.Equal(1, silinen);
        Assert.True(File.Exists(orta.Path));

        var kalanlar = await servis.GetBackupsAsync();
        Assert.Equal(2, kalanlar.Count);
        Assert.Contains(kalanlar, kayit => kayit.Id == orta.Id);

        // ASIL OLCU: budamadan sonra GERI YUKLENEBILIR nokta sayisi. "Kac kayit kaldi" degil.
        Assert.Equal(1, kalanlar.Count(kayit => File.Exists(kayit.Path)));
    }

    [Fact]
    public async Task Prune_KeepCount_her_sey_saglamken_ESKI_davranisi_aynen_korur()
    {
        // MUTASYON KONTROLU: koruma "hicbir seyi silme"ye donusmemeli. Butun noktalar
        // kullanilabilirken keepCount=1 tam olarak 1 nokta birakir.
        using var alan = new BudamaAlani();
        var abone = Guid.NewGuid();

        var eski = alan.AbonelikKaydi(abone, gunOnce: 10);
        var orta = alan.AbonelikKaydi(abone, gunOnce: 5);
        var enYeni = alan.AbonelikKaydi(abone, gunOnce: 0);

        var servis = alan.ServisKur(eski, orta, enYeni);
        var silinen = await servis.PruneSubscriptionAsync(abone, retentionDays: 0, keepCount: 1);

        Assert.Equal(2, silinen);
        Assert.False(File.Exists(eski.Path));
        Assert.False(File.Exists(orta.Path));
        Assert.True(File.Exists(enYeni.Path));
    }

    [Fact]
    public void Planner_KeepCount_ZINCIRIN_eksik_halkasi_noktayi_KULLANILABILIR_saymaz()
    {
        // Zincir TEK bir geri yukleme noktasidir ve ancak BUTUN halkalari yerindeyse geri
        // yuklenebilir. En yeni nokta bir zincirse ve halkasi eksikse, o nokta "elimizde duran
        // yedek" sayilamaz -- sayilirsa saglam olan eski tekil yedek silinirdi.
        var tamHalka = Kayit("z0", gunOnce: 3, zincir: "c1");
        var artimsalHalka = Kayit("z1", gunOnce: 2, zincir: "c1");
        var eskiTekil = Kayit("tekil", gunOnce: 40);

        var silinecek = BackupPrunePlanner.Select(
            [tamHalka, artimsalHalka, eskiTekil],
            cutoffUtc: DateTime.MinValue,
            keepCount: 1,
            // z1'in dosyasi yok: zincir kopuk.
            kullanilabilir: kayit => kayit.Id != "z1");

        Assert.Empty(silinecek);
    }

    [Fact]
    public void Planner_KeepCount_hicbir_nokta_kullanilamiyorsa_yine_de_EN_YENIYI_korur()
    {
        // Hicbiri geri yuklenemiyor: yer acmak dogru, ama en yeni kaydi tutmak kullaniciya
        // "burada bir yedek vardi ve dosyasi kayboldu" demenin tek yoludur (kayit silinirse
        // yedek kendiliginden buharlasmis gibi gorunur).
        var a = Kayit("a", gunOnce: 3);
        var b = Kayit("b", gunOnce: 1);

        var silinecek = BackupPrunePlanner.Select(
            [a, b],
            cutoffUtc: DateTime.MinValue,
            keepCount: 1,
            kullanilabilir: _ => false);

        Assert.Equal(["a"], silinecek.Select(kayit => kayit.Id).ToArray());
    }

    private static BackupRecordDto Kayit(string id, int gunOnce, string zincir = "") => new()
    {
        Id = id,
        Name = $"{id}.zip",
        Type = "site",
        Source = "shop.example.com",
        Path = $@"C:\yedek\{id}.zip",
        Format = "zip",
        Status = "created",
        ChainId = zincir,
        CreatedAtUtc = DateTime.UtcNow.AddDays(-gunOnce)
    };

    private sealed class BudamaAlani : IDisposable
    {
        private readonly string _kok;
        private readonly string _yedekKlasoru;

        public BudamaAlani()
        {
            _kok = Path.Combine(Path.GetTempPath(), $"adapanel-prune-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_kok);
            DosyaDeposu = new JsonFileStore(
                Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = _kok }));
            _yedekKlasoru = DosyaDeposu.EnsureDirectory("backups");
        }

        public JsonFileStore DosyaDeposu { get; }

        public BackupRecordDto KayitUret(string kaynak, int gunOnce, string tur = "site")
        {
            var id = Guid.NewGuid().ToString("N");
            var yol = Path.Combine(_yedekKlasoru, $"{id}.zip");
            File.WriteAllText(yol, "yedek");
            return new BackupRecordDto
            {
                Id = id,
                Name = Path.GetFileName(yol),
                Type = tur,
                Source = kaynak,
                Path = yol,
                Format = "zip",
                Status = "created",
                CreatedAtUtc = DateTime.UtcNow.AddDays(-gunOnce)
            };
        }

        /// <summary>
        /// ABONELIK yedegi kaydi: sayi limiti (keepCount) yalnizca bu yoldan
        /// (<c>PruneSubscriptionAsync</c>) calisir, saklama budamasindan degil.
        /// </summary>
        public BackupRecordDto AbonelikKaydi(Guid abone, int gunOnce)
        {
            var kayit = KayitUret(abone.ToString("N"), gunOnce, tur: "subscription");
            kayit.SubscriptionUserId = abone.ToString("N");
            return kayit;
        }

        public BackupService ServisKur(params BackupRecordDto[] kayitlar)
        {
            DosyaDeposu.WriteAsync("config/backups.json", kayitlar).GetAwaiter().GetResult();
            return new BackupService(null!, null!, null!, null!, null!, DosyaDeposu, null!);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_kok, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
