using AdaPanel.Core.Entities;
using AdaPanel.Core.Enums;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Cache;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// <c>CacheService</c> gözlemlenebilirliği: "ÖLÇEMEDİK"in "sorun yok" gibi göründüğü yollar
/// sunucuda iz bırakmalı.
/// </summary>
/// <remarks>
/// <para>
/// Bu serviste bir arıza olduğunda teşhis için elde HİÇBİR ŞEY yoktu: fırlatılan istisna HTTP
/// yanıtına gidip orada bitiyor, fırlatMAYAN yollar (durum okuma, "hiçbir şey yapmadan
/// durduruldu") ise hiçbir iz bırakmıyordu. Panelin gösterdiği "Durduruldu" yazısı iki apayrı
/// gerçeğin ortak yüzüydü: bileşen gerçekten duruyor, ya da biz ölçemedik.
/// </para>
/// <para>
/// Testler DIŞ ARACA (schtasks/powershell) bağlı yolları da geçiyor; bu yüzden ölçülen şey
/// çağrının SONUCU değil, kararın GEREKÇESİNİN kaydedilmiş olmasıdır. Böylece test geliştirme
/// makinesinde memcached kurulu olsa da olmasa da aynı şeyi ölçer.
/// </para>
/// </remarks>
public sealed class CacheObservabilityTests : IDisposable
{
    private readonly SqliteConnection baglanti;
    private readonly AdaPanelDbContext dbContext;

    public CacheObservabilityTests()
    {
        baglanti = new SqliteConnection("Filename=:memory:");
        baglanti.Open();
        dbContext = new AdaPanelDbContext(new DbContextOptionsBuilder<AdaPanelDbContext>().UseSqlite(baglanti).Options);
        dbContext.Database.EnsureCreated();
    }

    /// <summary>
    /// KAYIT "kurulu" diyor ama diskte çalıştırılabilir YOK.
    /// <para>
    /// Panel bileşeni yine KURULU gösterir (<c>IsInstalled</c> veritabanı kaydına bakar); kullanıcı
    /// "Başlat"a bastığında anlamsız bir 404 alır ve sunucuda çelişkinin izi bulunmazdı.
    /// </para>
    /// <para>
    /// MUTASYON: <c>BuildRuntimeAsync</c> içindeki "kayitta 'kurulu' ama ... diskte bulunamadi"
    /// uyarısı silinirse bu test düşer.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Kayit_kurulu_diyor_ama_calistirilabilir_yoksa_UYARI_yazilir()
    {
        var olmayanDizin = Path.Combine(Path.GetTempPath(), "ada-yok-" + Guid.NewGuid().ToString("N"));
        BilesenKaydet("memcached", BenzersizBirimAdi(), olmayanDizin);

        var gunlukcu = new KayitTutanGunlukcu<CacheService>();
        var servis = new CacheService(dbContext, gunlukcu);

        var durum = await servis.GetStatusAsync();

        // Panel hâlâ "kurulu" diyor — davranış korunuyor...
        Assert.True(durum.Memcached.IsInstalled);
        // ...ama çelişki artık kayıtlı.
        Assert.True(
            gunlukcu.Icerir(LogLevel.Warning, "memcached.exe", olmayanDizin),
            $"Eksik calistirilabilir uyarisi yazilmadi. Yazilanlar:\n{gunlukcu.TumMetin}");
    }

    /// <summary>
    /// HİÇBİR ŞEY YAPMADAN "durduruldu" dönen yol.
    /// <para>
    /// memcached'de kanıtlanmış arıza deseni buydu: görev/servis kaydı görünmediği için Durdur
    /// sessizce dönüyor, panel yeşil "Durduruldu" gösteriyor, bileşen ise çalışmaya devam ediyordu.
    /// Kaydın görünmemesi "gerçekten yok" da olabilir "sorgu çalıştırılamadı" da — ikisi de
    /// kullanıcıya AYNI yeşil ekran olarak dönüyor.
    /// </para>
    /// <para>
    /// MUTASYON: <c>StopScheduledTaskAsync</c> içindeki "kayitli GORUNMUYOR" uyarısı silinirse
    /// bu test düşer.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Gorev_kayitli_gorunmuyorken_durdurma_GEREKCEYI_yazar()
    {
        // Bilerek var olmayan bir görev adı: makinede gerçekten kurulu bir AdaMemcached olsa bile
        // bu ad HİÇBİR koşulda kayıtlı olamaz. Test böylece ortamdan bağımsızlaşır.
        var birimAdi = BenzersizBirimAdi();
        BilesenKaydet("memcached", birimAdi, Path.Combine(Path.GetTempPath(), "ada-yok-" + Guid.NewGuid().ToString("N")));

        var gunlukcu = new KayitTutanGunlukcu<CacheService>();
        var servis = new CacheService(dbContext, gunlukcu);

        try
        {
            await servis.StopMemcachedAsync();
        }
        catch (Exception)
        {
            // 11211 portu bu makinede başka bir şey tarafından tutuluyorsa akış "durduramadim"
            // ile biter. ÖLÇTÜĞÜMÜZ ŞEY sonuç değil, kararın gerekçesinin kaydedilmesidir.
        }

        Assert.True(
            gunlukcu.Icerir(LogLevel.Warning, birimAdi, "GORUNMUYOR"),
            $"Kayitsiz gorev uyarisi yazilmadi. Yazilanlar:\n{gunlukcu.TumMetin}");
    }

    /// <summary>
    /// GÜNLÜKÇÜ İSTEĞE BAĞLI olmalı: dar kapsamlı testler bu servisi tek bağımlılıkla kuruyor
    /// (<c>MemcachedRuntimeTruthTests</c>). Zorunlu parametre o kurulumları kırardı; gerekçe
    /// <c>BackupService</c>'te yazılı. Ayrıca günlükçüsüz çalışırken hiçbir yol
    /// <c>NullReferenceException</c> atmamalı — koruma her çağrıda <c>?.</c> ile kuruludur.
    /// </summary>
    [Fact]
    public async Task Gunlukcusuz_kurulum_calismaya_devam_eder()
    {
        BilesenKaydet("memcached", BenzersizBirimAdi(), Path.Combine(Path.GetTempPath(), "ada-yok-" + Guid.NewGuid().ToString("N")));

        var servis = new CacheService(dbContext);
        var durum = await servis.GetStatusAsync();

        Assert.True(durum.Memcached.IsInstalled);
    }

    private static string BenzersizBirimAdi() => "AdaTestGorevi_" + Guid.NewGuid().ToString("N");

    private void BilesenKaydet(string bilesenId, string birimAdi, string kurulumDizini)
    {
        dbContext.InstalledComponents.Add(new InstalledComponent
        {
            ComponentId = bilesenId,
            DisplayName = bilesenId,
            Version = "1.6.8",
            Type = ComponentType.Zip,
            InstallDir = kurulumDizini,
            ServiceName = birimAdi,
            Status = ComponentInstallStatus.Installed
        });
        dbContext.SaveChanges();
    }

    public void Dispose()
    {
        dbContext.Dispose();
        baglanti.Dispose();
    }
}
