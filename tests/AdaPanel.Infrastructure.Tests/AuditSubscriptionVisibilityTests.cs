using AdaPanel.Core.Dtos.Audit;
using AdaPanel.Core.Dtos.Users;
using AdaPanel.Core.Entities;
using AdaPanel.Core.Enums;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Services;
using AdaPanel.Infrastructure.Support;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Denetim kaydının KİRACI GÖRÜNÜRLÜĞÜ: kaydın konusu olan abonelik de kapsamı belirler.
/// </summary>
/// <remarks>
/// <para>
/// <b>Kapatılan açık:</b> kayıtlar yalnız AKTÖRE göre kapsamlanıyordu. Sonuç: bir yöneticinin
/// kiracının sitesinde yaptığı silme/yazma işlemi, kiracının kendi denetim görünümünde HİÇ
/// çıkmıyordu. Oysa "benim siteme kim dokundu" kaydın var olma sebebidir; en çok gerektiği durumda
/// (kiracının kendisi yapmadıysa) tam olarak sessizdi.
/// </para>
/// <para>
/// <b>Aynı anda korunan şey:</b> genişletmenin çapraz-kiracı sızıntıya dönüşmemesi. Yeni sütun
/// başka bir kiracının kaydını göstermeye başlarsa, kapatılan açıktan daha büyüğü açılmış olur —
/// bu yüzden her genişleme testinin yanında bir izolasyon testi var.
/// </para>
/// </remarks>
public sealed class AuditSubscriptionVisibilityTests : IDisposable
{
    private static readonly Guid Yonetici = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid MusteriA = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid MusteriB = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid AltKullaniciA = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid Bayi = Guid.Parse("55555555-5555-5555-5555-555555555555");

    private readonly SqliteConnection baglanti;
    private readonly AdaPanelDbContext dbContext;
    private readonly string veriKoku;
    private readonly AuditService servis;

    public AuditSubscriptionVisibilityTests()
    {
        baglanti = new SqliteConnection("Filename=:memory:");
        baglanti.Open();
        dbContext = new AdaPanelDbContext(new DbContextOptionsBuilder<AdaPanelDbContext>().UseSqlite(baglanti).Options);
        dbContext.Database.EnsureCreated();

        KullaniciEkle(Yonetici, "yonetici", UserRole.Administrator);
        KullaniciEkle(MusteriA, "musteri-a", UserRole.Customer);
        KullaniciEkle(MusteriB, "musteri-b", UserRole.Customer);
        KullaniciEkle(AltKullaniciA, "alt-a", UserRole.Customer);
        KullaniciEkle(Bayi, "bayi", UserRole.Reseller);
        dbContext.SaveChanges();

        var kapsam = new AuditLogReadTests.SahteKapsam();
        kapsam.AltKullanicilar[MusteriA] = [AltKullaniciA];
        kapsam.Ebeveynler[AltKullaniciA] = MusteriA;
        kapsam.SahipOlunanlar[Bayi] = [MusteriA];

        veriKoku = Path.Combine(Path.GetTempPath(), "adapanel-audit-abonelik-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(veriKoku);

        servis = new AuditService(
            dbContext,
            kapsam,
            new JsonFileStore(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = veriKoku })));
    }

    public void Dispose()
    {
        dbContext.Dispose();
        baglanti.Dispose();

        try
        {
            Directory.Delete(veriKoku, recursive: true);
        }
        catch
        {
            // Geçici klasör temizliği en iyi çaba.
        }
    }

    [Fact]
    public async Task Kiraci_KENDI_kaynagindaki_YONETICI_islemini_GORUR()
    {
        // Yönetici, kiracının sitesinde bir dosya siliyor.
        await servis.WriteForSubscriptionAsync(
            "files.deleted",
            "sites/a.test/wp-config.php",
            "File or directory deleted.",
            Yonetici,
            MusteriA,
            "203.0.113.9");

        var sonuc = await servis.SearchAsync(
            new AuditLogQuery(),
            await servis.ResolveVisibilityAsync(MusteriA, UserRole.Customer));

        // "Benim siteme kim dokundu" sorusunun cevabı: yönetici. Bu satır önceden HİÇ görünmüyordu.
        Assert.Equal(1, sonuc.TotalCount);
        Assert.Equal("yonetici", Assert.Single(sonuc.Items).UserName);
    }

    [Fact]
    public async Task Kiraci_BASKA_kiracinin_kaynagindaki_kaydi_GOREMEZ()
    {
        await servis.WriteForSubscriptionAsync(
            "files.deleted",
            "sites/b.test/wp-config.php",
            "File or directory deleted.",
            Yonetici,
            MusteriB,
            "203.0.113.9");

        var sonuc = await servis.SearchAsync(
            new AuditLogQuery(),
            await servis.ResolveVisibilityAsync(MusteriA, UserRole.Customer));

        // Genişleme, izolasyonu delmemeli: konu sütunu BAŞKA bir aboneliğe işaret ediyorsa kayıt yok.
        Assert.Equal(0, sonuc.TotalCount);
    }

    [Fact]
    public async Task Konu_sutunu_FILTREYLE_de_sizdirmaz()
    {
        await servis.WriteForSubscriptionAsync(
            "files.written",
            "sites/b.test/gizli.php",
            "File content saved.",
            Yonetici,
            MusteriB,
            "203.0.113.9");

        var sonuc = await servis.SearchAsync(
            new AuditLogQuery { Search = "b.test", UserName = "yonetici" },
            await servis.ResolveVisibilityAsync(MusteriA, UserRole.Customer));

        // Kapsam ölçütlerden ÖNCE ve ayrı uygulanır; hiçbir arama terimi onu genişletemez.
        Assert.Equal(0, sonuc.TotalCount);
    }

    [Fact]
    public async Task ESKI_kayitlarin_gorunurlugu_DEGISMEZ()
    {
        // Sütun eklenmeden önce yazılmış kayıtların tamamı böyledir: konu NULL.
        await servis.WriteAsync("files.deleted", "sites/a.test/x.php", "detay", MusteriA, "203.0.113.9");
        await servis.WriteAsync("files.deleted", "sites/a.test/y.php", "detay", Yonetici, "203.0.113.9");
        await servis.WriteAsync("system.startup", "panel", "detay", null, null);

        Assert.True(await dbContext.AuditLogs.AllAsync(log => log.SubscriptionUserId == null));

        var sonuc = await servis.SearchAsync(
            new AuditLogQuery(),
            await servis.ResolveVisibilityAsync(MusteriA, UserRole.Customer));

        // GERİLEME YOK: konu boşken kapsam eskisi gibi yalnız aktöre bakar — kiracı kendi kaydını
        // görür, yöneticinin konusuz kaydını ve sahipsiz "system" kaydını görmez.
        Assert.Equal(1, sonuc.TotalCount);
        Assert.Equal("sites/a.test/x.php", Assert.Single(sonuc.Items).Resource);
    }

    [Fact]
    public async Task Alt_kullanici_da_ABONELIGIN_kaydini_gorur()
    {
        await servis.WriteForSubscriptionAsync(
            "files.moved",
            "sites/a.test/httpdocs",
            "1 of 1 item(s) moved.",
            Yonetici,
            MusteriA,
            "203.0.113.9");

        var sonuc = await servis.SearchAsync(
            new AuditLogQuery(),
            await servis.ResolveVisibilityAsync(AltKullaniciA, UserRole.Customer));

        // Parent ve alt kullanıcılar AYNI aboneliği paylaşır → aynı izi görürler (Plesk davranışı).
        Assert.Equal(1, sonuc.TotalCount);
    }

    [Fact]
    public async Task Bayi_MUSTERISININ_kaynagindaki_yonetici_islemini_gorur()
    {
        await servis.WriteForSubscriptionAsync("files.deleted", "sites/a.test/x.php", "detay", Yonetici, MusteriA, null);
        await servis.WriteForSubscriptionAsync("files.deleted", "sites/b.test/x.php", "detay", Yonetici, MusteriB, null);

        var sonuc = await servis.SearchAsync(
            new AuditLogQuery(),
            await servis.ResolveVisibilityAsync(Bayi, UserRole.Reseller));

        // Bayi kendi müşterisinin kaynağındaki izi görür, başkasının müşterisininkini görmez.
        Assert.Equal(1, sonuc.TotalCount);
        Assert.Equal("sites/a.test/x.php", Assert.Single(sonuc.Items).Resource);
    }

    [Fact]
    public async Task CSV_disa_aktarma_AYNI_kapsami_uygular()
    {
        await servis.WriteForSubscriptionAsync("files.deleted", "sites/a.test/x.php", "detay", Yonetici, MusteriA, null);
        await servis.WriteForSubscriptionAsync("files.deleted", "sites/b.test/x.php", "detay", Yonetici, MusteriB, null);

        var satirlar = new List<AuditLogDto>();
        await foreach (var item in servis.StreamAsync(new AuditLogQuery(), await servis.ResolveVisibilityAsync(MusteriA, UserRole.Customer)))
        {
            satirlar.Add(item);
        }

        // Dışa aktarma ayrı bir yol olsaydı, yeni sütunu orada uygulamayı unutmak mümkün olurdu.
        Assert.Equal("sites/a.test/x.php", Assert.Single(satirlar).Resource);
    }

    [Fact]
    public async Task Yonetici_KENDI_gorunumunde_her_seyi_gormeye_devam_eder()
    {
        await servis.WriteForSubscriptionAsync("files.deleted", "sites/a.test/x.php", "detay", Yonetici, MusteriA, null);
        await servis.WriteAsync("system.startup", "panel", "detay", null, null);

        var sonuc = await servis.SearchAsync(
            new AuditLogQuery(),
            await servis.ResolveVisibilityAsync(Yonetici, UserRole.Administrator));

        Assert.Equal(2, sonuc.TotalCount);
    }

    private void KullaniciEkle(Guid id, string ad, UserRole rol) => dbContext.Users.Add(new PanelUser
    {
        Id = id,
        UserName = ad,
        Email = $"{ad}@ornek.test",
        Role = rol,
        PasswordHash = "x",
        IsActive = true
    });
}
