using AdaPanel.Core.Dtos.Users;
using AdaPanel.Core.Entities;
using AdaPanel.Core.Enums;
using AdaPanel.Core.Interfaces;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Security;
using AdaPanel.Infrastructure.Services;
using AdaPanel.Infrastructure.Support;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Yöneticinin müşteriyi ELLE askıya aldığı yolda da denetim kaydı İSTENEN değil YAPILAN sayıyı
/// yazar — ve durdurulamayan siteleri ADIYLA söyler.
/// </summary>
/// <remarks>
/// <para>
/// <b>ÖLÇÜLEN AÇIK.</b> <c>UserManagementService.ApplySuspensionStateAsync</c> siteleri tek tek
/// durduruyor, tek bir sitenin hatasını <c>catch</c> ile yutup devam ediyor — doğrusu da budur,
/// bir site yüzünden askı yarım kalmamalı — ama denetim kaydı koşulsuz <c>scope.Sites.Count</c>
/// yazıyordu. Beş siteden üçü durdurulamasa bile kayıt "5 site(s) stopped" diyordu.
/// </para>
/// <para>
/// <b>Bu, ZATEN DÜZELTİLMİŞ olanın KARDEŞİDİR.</b> Otomatik uygulama
/// (<c>SubscriptionEnforcementService.Suspend/ResumeAsync</c>) bir tur önce düzeltildi; yöneticinin
/// müşteriyi elle askıya aldığı bu yol — panelde en çok kullanılan yol — atlanmıştı. Bu depoda
/// defalarca ölçülen ikinci kalıp tam olarak budur: koruma bir yola konur, kardeşi sessizce eski
/// hâlde kalır.
/// </para>
/// <para>
/// <b>Bedeli iki yönde de somut.</b> Askıya almada: ödeme yapmayan müşterinin siteleri YAYINDA
/// kalır ve kayıt aksini söyler; yönetici arızayı ancak trafik faturası gelince görür. Askı
/// kaldırmada: parasını ödemiş müşterinin sitesi KAPALI kalır ve kayıt açıldığını söyler; müşteri
/// şikâyet eder, yönetici kayda bakar, kayıt "başladı" dediği için başka yerde arar.
/// </para>
/// <para>
/// Test gerçek davranışı ölçer: bellek içi SQLite, gerçek <c>UserManagementService</c>, gerçek
/// <c>CustomerScopeService</c> ve seçilen siteyi GERÇEKTEN kıran bir IIS çifti. Kaynak metni
/// taramaz.
/// </para>
/// </remarks>
public sealed class ElleAskiKaydiDurustlukTests : IDisposable
{
    private readonly string kok;
    private readonly SqliteConnection baglanti;
    private readonly AdaPanelDbContext dbContext;
    private readonly ICustomerScopeService kapsamlar;
    private readonly AskiDenetimKaydiDurustlukTests.YakalayanDenetim denetim = new();
    private readonly AskiDenetimKaydiDurustlukTests.SecilenSiteyiKiranServis siteler = new();
    private readonly UserManagementService servis;
    private readonly Guid yoneticiId;
    private readonly Guid musteriId;

    public ElleAskiKaydiDurustlukTests()
    {
        kok = Path.Combine(Path.GetTempPath(), "adapanel-elle-aski-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(kok);

        baglanti = new SqliteConnection("Filename=:memory:");
        baglanti.Open();
        dbContext = new AdaPanelDbContext(
            new DbContextOptionsBuilder<AdaPanelDbContext>().UseSqlite(baglanti).Options);
        dbContext.Database.EnsureCreated();

        var yonetici = new PanelUser
        {
            UserName = "yonetici",
            Email = "yonetici@ornek.test",
            Role = UserRole.Administrator,
            PasswordHash = "x",
            IsActive = true
        };
        var musteri = new PanelUser
        {
            UserName = "musteri",
            Email = "musteri@ornek.test",
            Role = UserRole.Customer,
            PasswordHash = "x",
            IsActive = true
        };
        dbContext.Users.AddRange(yonetici, musteri);
        dbContext.SaveChanges();
        yoneticiId = yonetici.Id;
        musteriId = musteri.Id;

        var store = new JsonFileStore(
            Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = kok }));
        kapsamlar = new CustomerScopeService(
            store,
            new AskiDenetimKaydiDurustlukTests.PlansizServis(),
            new AskiDenetimKaydiDurustlukTests.TavansizServis(),
            siteler);

        servis = new UserManagementService(
            dbContext,
            new PasswordHasher<PanelUser>(),
            kapsamlar,
            new ThrowingNotificationService(),
            new ThrowingPreferencesService(),
            new ThrowingSecurityService(),
            NullLogger<UserManagementService>.Instance,
            denetim,
            siteler,
            new ResellerSiteAssignmentTests.KullanilmayanDbSahiplik(),
            new ResellerSiteAssignmentTests.KullanilmayanZamanlayici(),
            new ResellerSiteAssignmentTests.KullanilmayanCascade(),
            new ThrowingApiTokenService(),
            new SahipsizSunucuSahibiServisi());
    }

    public void Dispose()
    {
        dbContext.Dispose();
        baglanti.Dispose();
        // Gecici klasor BILEREK SILINMIYOR: bu turda ajanlar hicbir seyi silmez.
    }

    /// <summary>Müşteriyi <paramref name="aktif"/> yapar; askı/askı kaldırma bu geçişte uygulanır.</summary>
    private Task<UserSummaryDto> AktifligiDegistir(bool aktif) =>
        servis.UpdateUserAsync(
            musteriId,
            new UpdateUserRequest
            {
                Email = "musteri@ornek.test",
                Role = nameof(UserRole.Customer),
                IsActive = aktif,
                AssignedSites = ["a.test", "b.test", "c.test"],
                MaxSites = 5,
                MaxDiskMb = 1024
            },
            yoneticiId,
            "127.0.0.1");

    [Fact]
    public async Task Durdurulamayan_site_varsa_kayit_YAPILAN_sayiyi_ve_ADLARI_yazar()
    {
        siteler.Kiranlar.Add("b.test");

        await AktifligiDegistir(false);

        var kayit = denetim.Son("users.suspended");
        Assert.Contains("2 of 3", kayit, StringComparison.Ordinal);
        Assert.Contains("STILL RUNNING", kayit, StringComparison.Ordinal);
        Assert.Contains("b.test", kayit, StringComparison.Ordinal);

        // BASARILI siteler "hâlâ yayinda" listesinde GORUNMEMELI. Sayiyi duzeltip listeyi yanlis
        // doldurmak, yoneticiyi CALISAN bir siteyi kovalamaya gonderirdi.
        var uyariBolumu = kayit[kayit.IndexOf("STILL RUNNING", StringComparison.Ordinal)..];
        Assert.DoesNotContain("a.test", uyariBolumu, StringComparison.Ordinal);
        Assert.DoesNotContain("c.test", uyariBolumu, StringComparison.Ordinal);
    }

    /// <summary>KARDEŞ YÖN: askıyı kaldırma da aynı dürüstlüğü uygular.</summary>
    [Fact]
    public async Task Baslatilamayan_site_varsa_kayit_YAPILAN_sayiyi_ve_ADLARI_yazar()
    {
        await AktifligiDegistir(false);
        siteler.Kiranlar.Add("a.test");

        await AktifligiDegistir(true);

        var kayit = denetim.Son("users.resumed");
        Assert.Contains("2 of 3", kayit, StringComparison.Ordinal);
        Assert.Contains("STILL STOPPED", kayit, StringComparison.Ordinal);
        Assert.Contains("a.test", kayit, StringComparison.Ordinal);
    }

    /// <summary>
    /// FAZLA KAPATMA NÖBETÇİSİ: her şey yolundayken kayıt SADE kalmalı.
    /// </summary>
    /// <remarks>
    /// "Her kayda uyarı yaz" çözümü yukarıdaki iki testi de geçerdi ve her başarılı askıyı şüpheli
    /// gösterirdi. Sürekli uyaran bir kayıt, uyarmayan kadar kullanışsızdır.
    /// </remarks>
    [Fact]
    public async Task Hepsi_basariliysa_kayit_uyari_ICERMEZ()
    {
        await AktifligiDegistir(false);

        var kayit = denetim.Son("users.suspended");
        Assert.Contains("3 site(s) stopped", kayit, StringComparison.Ordinal);
        Assert.DoesNotContain("STILL RUNNING", kayit, StringComparison.Ordinal);
    }

    /// <summary>
    /// Cümle TEK KAYNAKTAN gelir; otomatik ve elle yollar AYNI kelimeleri kullanır.
    /// </summary>
    /// <remarks>
    /// Kural üç yerde elle yazılsaydı, dördüncü yol da sessizce geride kalırdı — kapatılan hatanın
    /// tam şekli budur. Ayrıca YAPILAN sayının İSTENENDEN başarısızlar düşülerek bulunması,
    /// listeyle ayrışabilecek ikinci bir sayaç bırakmaz.
    /// </remarks>
    [Fact]
    public void Kayit_cumlesi_tek_kaynaktan_uretilir()
    {
        Assert.Equal("3 site(s) stopped.", SiteSuspensionReport.Stopped(3, []));
        Assert.Equal(
            "1 of 3 site(s) stopped; STILL RUNNING: b.test, c.test.",
            SiteSuspensionReport.Stopped(3, ["b.test", "c.test"]));

        Assert.Equal("2 site(s) started.", SiteSuspensionReport.Started(2, []));
        Assert.Equal(
            "1 of 2 site(s) started; STILL STOPPED: a.test.",
            SiteSuspensionReport.Started(2, ["a.test"]));
    }
}
