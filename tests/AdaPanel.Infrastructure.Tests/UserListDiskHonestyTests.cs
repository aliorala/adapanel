using AdaPanel.Core.Entities;
using AdaPanel.Core.Interfaces;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// KULLANICI LİSTESİ, disk sayısının EKSİK olabileceğini satır satır söyler.
/// </summary>
/// <remarks>
/// <para>
/// <b>Kapatılan açık:</b> disk kullanımı bütçeli bir ağaç yürüyüşüyle ölçülür ve bütçe dolduğunda
/// (ya da bir site IIS'ten okunamadığında) dönen sayı gerçek kullanımın ALT SINIRIDIR. Bu bilgi
/// kaynakta vardı ama <c>GetDiskUsageMbAsync</c> düz bir sayı döndürdüğü için yolda kayboluyordu:
/// liste "1,2 GB" yazıyor, sayının kesildiğini GİZLİYORDU. Yönetici bu listeye bakarak kota
/// büyütür, müşteri uyarır, hesap askıya alır — üçü de yanlış veriye dayanırdı.
/// </para>
/// <para>
/// <b>Neden satır başına:</b> yürüyüşün bütçesi ABONELİK başınadır, yani bir müşterinin sayısı
/// kesilirken komşusununki tam olabilir. Listeye tek bir "eksik olabilir" damgası vurmak, hangi
/// satıra güvenileceğini söylemezdi.
/// </para>
/// <para>
/// <b>Neden GERÇEK <see cref="UserManagementService"/>:</b> ölçülen şey tam olarak bu servisin
/// DTO'yu doldurma biçimidir (<c>MapUser</c>). Sahte bir eşleyiciyle yazılsaydı test, kuralın
/// kendisini değil kendi taklidini doğrulardı. Kapsam servisi ise sahtedir ve bilerek: burada
/// ölçülen "ağaç nasıl yürünür" değil, "ölçümün sonucu listeye TAŞINIYOR mu".
/// </para>
/// </remarks>
public sealed class UserListDiskHonestyTests : IDisposable
{
    private readonly SqliteConnection baglanti;
    private readonly AdaPanelDbContext dbContext;
    private readonly ResellerSiteAssignmentTests.KayitEdenKapsam kapsam = new([]);
    private readonly UserManagementService servis;
    private readonly Guid yoneticiId;

    public UserListDiskHonestyTests()
    {
        baglanti = new SqliteConnection("Filename=:memory:");
        baglanti.Open();
        dbContext = new AdaPanelDbContext(
            new DbContextOptionsBuilder<AdaPanelDbContext>().UseSqlite(baglanti).Options);
        dbContext.Database.EnsureCreated();

        var yonetici = new PanelUser
        {
            UserName = "yonetici",
            Email = "yonetici@ornek.test",
            Role = Core.Enums.UserRole.Administrator,
            PasswordHash = "x",
            IsActive = true
        };
        var musteri = new PanelUser
        {
            UserName = "musteri",
            Email = "musteri@ornek.test",
            Role = Core.Enums.UserRole.Customer,
            PasswordHash = "x",
            IsActive = true
        };
        dbContext.Users.AddRange(yonetici, musteri);
        dbContext.SaveChanges();
        yoneticiId = yonetici.Id;

        servis = new UserManagementService(
            dbContext,
            new PasswordHasher<PanelUser>(),
            kapsam,
            new ThrowingNotificationService(),
            new ThrowingPreferencesService(),
            new ThrowingSecurityService(),
            NullLogger<UserManagementService>.Instance,
            new RecordingAuditService(),
            new ResellerSiteAssignmentTests.KullanilmayanSiteServisi(),
            new ResellerSiteAssignmentTests.KullanilmayanDbSahiplik(),
            new ResellerSiteAssignmentTests.KullanilmayanZamanlayici(),
            new ResellerSiteAssignmentTests.KullanilmayanCascade(),
            new ThrowingApiTokenService());
    }

    public void Dispose()
    {
        dbContext.Dispose();
        baglanti.Dispose();
    }

    /// <summary>
    /// KESİLMİŞ ÖLÇÜM LİSTEDE "TAM" GÖRÜNMEZ: sayı düşürülmez ama eksik olduğu söylenir.
    /// </summary>
    /// <remarks>
    /// Sayının KORUNMASI da kuralın parçası: alt sınır bile "bu abonelik en az şu kadar yer
    /// kaplıyor" sorusunu cevaplar. Eksik ölçümde sayıyı gizlemek (0 ya da boş göstermek), dolmuş
    /// bir kotayı boş göstermek olurdu — yani başka bir yalan.
    /// </remarks>
    [Fact]
    public async Task Kesilmis_olcum_listede_EKSIK_oldugunu_soyler()
    {
        kapsam.DiskKullanimi = new DiskUsage(1200, false, "files.size_truncated_entries");

        var satirlar = await servis.GetUsersAsync(yoneticiId);

        var musteri = Assert.Single(satirlar, satir => satir.Role == "Customer");
        Assert.Equal(1200, musteri.DiskUsageMb);
        Assert.False(musteri.DiskMeasured);
        Assert.Equal("files.size_truncated_entries", musteri.DiskUsageTruncatedReasonKey);
    }

    /// <summary>MUTASYON KONTROLÜ: TAM ölçülen satır "eksik" damgalanmaz.</summary>
    /// <remarks>
    /// Bu olmadan "bayrağı hep false yaz" da yukarıdaki testi geçerdi ve panel her satıra hak
    /// edilmemiş bir uyarı asardı; her yerde çıkan bir uyarı, gerçekten eksik olan satırda da
    /// okunmaz olur.
    /// </remarks>
    [Fact]
    public async Task Tam_olculen_satir_EKSIK_damgalanmaz()
    {
        kapsam.DiskKullanimi = new DiskUsage(512, true, string.Empty);

        var satirlar = await servis.GetUsersAsync(yoneticiId);

        var musteri = Assert.Single(satirlar, satir => satir.Role == "Customer");
        Assert.Equal(512, musteri.DiskUsageMb);
        Assert.True(musteri.DiskMeasured);
        Assert.Equal(string.Empty, musteri.DiskUsageTruncatedReasonKey);
    }

    /// <summary>
    /// MÜŞTERİ OLMAYAN SATIRIN SIFIRI "ÖLÇÜLEMEDİ" DEĞİLDİR.
    /// </summary>
    /// <remarks>
    /// Yöneticinin/bayinin aboneliği (ve disk kapsamı) yoktur; ölçülecek bir şey olmaması ile
    /// ölçememek AYNI ŞEY DEĞİLDİR. Bu satırları "eksik" işaretlemek, listedeki uyarıyı gürültüye
    /// çevirir ve gerçekten eksik olan müşteri satırını görünmez kılardı. Aynı ayrım trafik
    /// kotasında da var: sitesi olmayan aboneliğin sıfırı ÖLÇÜLMÜŞ bir sıfırdır.
    /// </remarks>
    [Fact]
    public async Task Musteri_OLMAYAN_satir_olculmus_SIFIR_gosterir()
    {
        // Kapsam servisi kesilmiş bir ölçüm döndürse BİLE yönetici satırı ondan etkilenmemeli:
        // o satır için disk hiç sorulmaz.
        kapsam.DiskKullanimi = new DiskUsage(1200, false, "files.size_truncated_entries");

        var satirlar = await servis.GetUsersAsync(yoneticiId);

        var yonetici = Assert.Single(satirlar, satir => satir.Role == "Administrator");
        Assert.Equal(0, yonetici.DiskUsageMb);
        Assert.True(yonetici.DiskMeasured);
    }
}
