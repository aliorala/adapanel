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
