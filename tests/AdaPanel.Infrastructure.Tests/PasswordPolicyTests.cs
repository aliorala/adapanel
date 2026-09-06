using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Users;
using AdaPanel.Core.Entities;
using AdaPanel.Core.Enums;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// PAROLA KURALI TEK KAYNAKTAN GEÇER.
/// </summary>
/// <remarks>
/// <para>
/// <b>Ölçülen hata:</b> "en az 12 karakter" kuralı üç ayrı yerde, üç ayrı biçimde duruyordu —
/// <c>CreateUserRequest</c> üzerinde bir veri açıklaması, <c>UpdateSubUserAsync</c> gövdesinde elle
/// yazılmış bir sayı ve <c>CreateSubUserAsync</c>'te HİÇBİR ŞEY. Yani abonelik altı ek kullanıcı
/// (Plesk "additional user" karşılığı) tek karakterlik parolayla açılabiliyordu; üstelik o login
/// aboneliğin bütün sitelerine, veritabanlarına ve postasına erişiyor.
/// </para>
/// <para>
/// <b>Neden veri açıklaması yetmiyordu:</b> öznitelikler yalnız MVC model bağlama yolunda çalışır.
/// Aynı servisler MCP araçlarından ve panelin iç akışlarından da çağrılıyor; oralarda öznitelik hiç
/// değerlendirilmez. Kural bu yüzden veriyi YAZAN noktanın yanında da uygulanıyor.
/// </para>
/// </remarks>
public sealed class PasswordPolicyTests
{
    [Theory]
    [InlineData("")]
    [InlineData("kisa")]
    [InlineData("onbirkarakt")] // 11
    public void Kisa_parola_REDDEDILIR(string parola)
    {
        var hata = Assert.Throws<InvalidOperationException>(() => PasswordPolicy.Ensure(parola));
        Assert.Equal("users.password_too_short", hata.Message);
    }

    [Fact]
    public void Bos_deger_de_REDDEDILIR()
    {
        Assert.Throws<InvalidOperationException>(() => PasswordPolicy.Ensure(null));
    }

    [Fact]
    public void Sinirdaki_parola_KABUL_edilir()
    {
        // Tam sinir: 12 karakter gecmeli, 11 gecmemeli. "Kural var ama hep reddediyor" durumu da
        // bir hatadir.
        PasswordPolicy.Ensure(new string('a', PasswordPolicy.MinimumLength));
        Assert.Equal(12, PasswordPolicy.MinimumLength);
    }

    [Fact]
    public async Task Ek_kullanici_olustururken_kisa_parola_REDDEDILIR()
    {
        // KAPATILAN DELIK: bu yolda uzunluk HIC denetlenmiyordu.
        using var duzenek = Duzenek.Kur();

        var hata = await Assert.ThrowsAsync<InvalidOperationException>(() => duzenek.Servis.CreateSubUserAsync(
            duzenek.MusteriId,
            new CreateSubUserRequest
            {
                UserName = "ek-kullanici",
                Email = "ek@ornek.test",
                Password = "123"
            },
            ipAddress: null,
            CancellationToken.None));

        Assert.Equal("users.password_too_short", hata.Message);
        // Kullanici satiri HIC olusmamali: yarim kalmis bir kayit, "neden giremiyorum" sorusunu
        // cevapsiz birakirdi.
        Assert.Equal(1, await duzenek.DbContext.Users.CountAsync());
    }

    [Fact]
    public async Task Kullanici_olustururken_kisa_parola_REDDEDILIR()
    {
        using var duzenek = Duzenek.Kur();

        var hata = await Assert.ThrowsAsync<InvalidOperationException>(() => duzenek.Servis.CreateUserAsync(
            new CreateUserRequest
            {
                UserName = "yeni-musteri",
                Email = "yeni@ornek.test",
                Password = "123",
                Role = "Customer",
                SendWelcomeEmail = false
            },
            duzenek.MusteriId,
            ipAddress: null,
            CancellationToken.None));

        Assert.Equal("users.password_too_short", hata.Message);
        Assert.Equal(1, await duzenek.DbContext.Users.CountAsync());
    }

    private sealed class Duzenek : IDisposable
    {
        private readonly SqliteConnection baglanti;

        private Duzenek(SqliteConnection baglanti, AdaPanelDbContext dbContext, UserManagementService servis, Guid musteriId)
        {
            this.baglanti = baglanti;
            DbContext = dbContext;
            Servis = servis;
            MusteriId = musteriId;
        }

        public AdaPanelDbContext DbContext { get; }

        public UserManagementService Servis { get; }

        /// <summary>Ek kullanıcıyı oluşturan TOP-LEVEL müşteri (aboneliğin sahibi).</summary>
        public Guid MusteriId { get; }

        public static Duzenek Kur()
        {
            var baglanti = new SqliteConnection("Filename=:memory:");
            baglanti.Open();
            var secenekler = new DbContextOptionsBuilder<AdaPanelDbContext>().UseSqlite(baglanti).Options;
            var dbContext = new AdaPanelDbContext(secenekler);
            dbContext.Database.EnsureCreated();

            var musteri = new PanelUser
            {
                UserName = "musteri",
                Email = "musteri@ornek.test",
                Role = UserRole.Customer,
                PasswordHash = "x",
                IsActive = true
            };
            dbContext.Users.Add(musteri);
            dbContext.SaveChanges();

            var servis = new UserManagementService(
                dbContext,
                new PasswordHasher<PanelUser>(),
                new ResellerSiteAssignmentTests.KayitEdenKapsam([]),
                new ThrowingNotificationService(),
                new ThrowingPreferencesService(),
                new ThrowingSecurityService(),
                NullLogger<UserManagementService>.Instance,
                new RecordingAuditService(),
                new ResellerSiteAssignmentTests.KullanilmayanSiteServisi(),
                new ResellerSiteAssignmentTests.KullanilmayanDbSahiplik(),
                new ResellerSiteAssignmentTests.KullanilmayanZamanlayici(),
                new ResellerSiteAssignmentTests.KullanilmayanCascade(),
                new ThrowingApiTokenService(),
                new SahipsizSunucuSahibiServisi());

            return new Duzenek(baglanti, dbContext, servis, musteri.Id);
        }

        public void Dispose()
        {
            DbContext.Dispose();
            baglanti.Dispose();
        }
    }
}
