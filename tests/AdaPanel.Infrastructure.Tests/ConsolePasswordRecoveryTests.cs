using System.Security.Claims;
using System.Text.Json.Nodes;
using AdaPanel.Api.Security;
using AdaPanel.Core.Common;
using AdaPanel.Core.Entities;
using AdaPanel.Core.Enums;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Security;
using AdaPanel.Infrastructure.Support;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// SUNUCU KONSOLUNDAN yönetici parolası belirleme (<c>AdaPanel.Api.exe --parola-belirle</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Korunan olay:</b> kullanıcı panel parolasını unuttu; <c>recover-access.flag</c> yalnız
/// kilitleri/IP engellerini temizlediği için parola sıfırlanamadı ve elde kalan tek çare
/// <c>data\adapanel.db</c>'yi SİLMEK oldu — panel kullanıcıları, ayarları ve kurulu bileşen
/// kayıtları da gitti.
/// </para>
/// <para>
/// Burada sabitlenen şey bir özellik değil, üç HATA SINIFI:
/// </para>
/// <list type="number">
/// <item><b>Arka kapıya dönüşme.</b> Sunucudan çalışan bir parola sıfırlama, YÖNETİCİ olmayan bir
/// hesap için de çalışırsa panelin kiracı izolasyonunu tek hamlede geçersiz kılar.</item>
/// <item><b>Yarım kesme.</b> Parola değişip de oturumlar ve API anahtarları ayakta kalırsa erişim
/// kesilmiş olmaz, yalnız kesildiği izlenimi doğar.</item>
/// <item><b>Sırrın ikinci deposu.</b> Denetim kaydı yedeklere yayılır ve panelde okunur; parolanın
/// oraya düşmesi, korumaya çalıştığı şeyi ifşa etmek olurdu.</item>
/// </list>
/// </remarks>
public sealed class ConsolePasswordRecoveryTests
{
    private const string YeniParola = "Sunucu-Konsolu-Parolasi-2026";
    private const string EskiParola = "Baslangic-Parolasi-2026";

    // --- 1) KİM seçilebilir ---

    [Fact]
    public async Task Listede_yalnizca_YONETICILER_var()
    {
        await using var duzenek = await Duzenek.OlusturAsync();

        var liste = await ConsolePasswordRecovery.ListAdministratorsAsync(duzenek.DbContext);

        Assert.Equal(
            new[] { "yonetici", "yonetici-2fa" },
            liste.Select(hesap => hesap.UserName).OrderBy(ad => ad, StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(UserRole.Operator)]
    [InlineData(UserRole.Customer)]
    [InlineData(UserRole.Reseller)]
    public async Task YONETICI_OLMAYAN_hesabin_parolasi_BELIRLENEMEZ(UserRole rol)
    {
        // Bu araç sunucuda oturum açmış kişiye MÜŞTERİ hesabı devralma imkânı vermek için değil,
        // panelin sahibinin kendi hesabına dönmesi için var. Aksi hâlde panelin kapsam denetimi
        // sunucu konsolundan atlanabilirdi.
        await using var duzenek = await Duzenek.OlusturAsync();
        var hedef = duzenek.RolIle(rol);
        var oncekiKarma = hedef.PasswordHash;

        var hata = await Assert.ThrowsAsync<AppException>(() => duzenek.ParolaBelirleAsync(hedef.Id));

        Assert.Equal(ConsolePasswordRecovery.TargetNotAdministratorKey, hata.MessageKey);
        Assert.Equal(oncekiKarma, (await duzenek.TazeleAsync(hedef)).PasswordHash);

        // Reddedilen denemede DENETİM KAYDI da, ANAHTAR İPTALİ de olmamalı: aksi hâlde araç,
        // yetkisi olmayan bir hedef için bile hizmet kesintisi üretebilirdi.
        Assert.Empty(await duzenek.DbContext.AuditLogs.ToListAsync());
        Assert.Empty(await duzenek.DbContext.ApiTokens.Where(t => t.RevokedAtUtc != null).ToListAsync());
    }

    [Fact]
    public async Task Kullanici_adi_ya_da_EPOSTA_ile_yonetici_bulunur()
    {
        await using var duzenek = await Duzenek.OlusturAsync();

        var adla = await ConsolePasswordRecovery.FindAdministratorAsync(duzenek.DbContext, "  YONETICI ");
        var epostayla = await ConsolePasswordRecovery.FindAdministratorAsync(duzenek.DbContext, "yonetici@ornek.test");

        Assert.Equal(duzenek.Yonetici.Id, adla.Id);
        Assert.Equal(duzenek.Yonetici.Id, epostayla.Id);
    }

    [Fact]
    public async Task Bulunamayan_ile_YONETICI_OLMAYAN_ayri_hatalardir()
    {
        // İkisini tek mesajla geçmek, müşteri hesabının adını yazan kişiye "böyle bir kullanıcı yok"
        // demek olurdu; kişi doğru hesabı aramaya devam ederdi.
        await using var duzenek = await Duzenek.OlusturAsync();

        var yok = await Assert.ThrowsAsync<AppException>(
            () => ConsolePasswordRecovery.FindAdministratorAsync(duzenek.DbContext, "olmayan"));
        var yoneticiDegil = await Assert.ThrowsAsync<AppException>(
            () => ConsolePasswordRecovery.FindAdministratorAsync(duzenek.DbContext, "musteri"));

        Assert.Equal(ConsolePasswordRecovery.TargetNotFoundKey, yok.MessageKey);
        Assert.Equal(ConsolePasswordRecovery.TargetNotAdministratorKey, yoneticiDegil.MessageKey);
    }

    // --- 2) Parola kuralı TEK kaynaktan ---

    [Theory]
    [InlineData("")]
    [InlineData("kisa")]
    [InlineData("onbirkarakt")] // 11 — sınırın bir altı
    public async Task KISA_parola_REDDEDILIR_ve_hicbir_sey_yazilmaz(string parola)
    {
        // Kural PasswordPolicy'den gelir; konsol yolu için İKİNCİ bir kural yazılmadı.
        await using var duzenek = await Duzenek.OlusturAsync();
        var oncekiKarma = duzenek.Yonetici.PasswordHash;

        var hata = await Assert.ThrowsAsync<InvalidOperationException>(
            () => duzenek.ParolaBelirleAsync(duzenek.Yonetici.Id, parola));

        Assert.Equal("users.password_too_short", hata.Message);
        Assert.Equal(oncekiKarma, (await duzenek.TazeleAsync(duzenek.Yonetici)).PasswordHash);
        Assert.Empty(await duzenek.DbContext.AuditLogs.ToListAsync());
    }

    [Fact]
    public async Task Sinirdaki_parola_KABUL_edilir()
    {
        // "Kural var ama her şeyi reddediyor" da bir arızadır.
        await using var duzenek = await Duzenek.OlusturAsync();
        var parola = new string('a', PasswordPolicy.MinimumLength);

        await duzenek.ParolaBelirleAsync(duzenek.Yonetici.Id, parola);

        Assert.Equal(PasswordVerificationResult.Success, duzenek.Dogrula(duzenek.Yonetici, parola));
    }

    // --- 3) Mutlu yol: parola gerçekten değişir ---

    [Fact]
    public async Task Yonetici_parolasi_BELIRLENIR()
    {
        await using var duzenek = await Duzenek.OlusturAsync();

        var sonuc = await duzenek.ParolaBelirleAsync(duzenek.Yonetici.Id);

        Assert.Equal("yonetici", sonuc.UserName);
        Assert.Equal(PasswordVerificationResult.Success, duzenek.Dogrula(duzenek.Yonetici, YeniParola));
        Assert.Equal(PasswordVerificationResult.Failed, duzenek.Dogrula(duzenek.Yonetici, EskiParola));
    }

    [Fact]
    public async Task GIRIS_KILIDI_acilir()
    {
        // Parolayı belirlemenin amacı kullanıcıyı içeri sokmaktır; hatalı denemelerden kalan kilit
        // yerinde kalsaydı kişi yeni parolayla da giremezdi ve araç işe yaramazdı.
        await using var duzenek = await Duzenek.OlusturAsync();
        duzenek.Yonetici.FailedLoginCount = 5;
        duzenek.Yonetici.LockedUntilUtc = DateTime.UtcNow.AddHours(1);
        await duzenek.DbContext.SaveChangesAsync();

        await duzenek.ParolaBelirleAsync(duzenek.Yonetici.Id);

        var taze = await duzenek.TazeleAsync(duzenek.Yonetici);
        Assert.Equal(0, taze.FailedLoginCount);
        Assert.Null(taze.LockedUntilUtc);
    }

    // --- 4) Yarım kesme olmasın: oturum + anahtar ---

    [Fact]
    public async Task API_ANAHTARLARI_dusurulur()
    {
        await using var duzenek = await Duzenek.OlusturAsync();
        duzenek.AnahtarEkle(duzenek.Yonetici.Id, "mcp-1");
        duzenek.AnahtarEkle(duzenek.Yonetici.Id, "mcp-2");
        duzenek.AnahtarEkle(duzenek.Musteri.Id, "baskasinin");
        await duzenek.DbContext.SaveChangesAsync();

        var sonuc = await duzenek.ParolaBelirleAsync(duzenek.Yonetici.Id);

        Assert.Equal(2, sonuc.RevokedApiTokens);
        Assert.Equal(
            2,
            await duzenek.DbContext.ApiTokens.CountAsync(t => t.UserId == duzenek.Yonetici.Id && t.RevokedAtUtc != null));

        // BAŞKASININ anahtarına dokunulmaz: parola sıfırlama, ilgisiz bir kullanıcının
        // entegrasyonlarını kesen bir yan etkiye dönüşemez.
        Assert.Empty(await duzenek.DbContext.ApiTokens
            .Where(t => t.UserId == duzenek.Musteri.Id && t.RevokedAtUtc != null)
            .ToListAsync());
    }

    [Fact]
    public async Task ACIK_OTURUMLAR_duser()
    {
        // Damgayı elle karşılaştırmak yalnız hesaplamayı ölçerdi; burada panelin GERÇEK çerez
        // doğrulayıcısı çalıştırılıyor ve asıl soru soruluyor: çerez reddediliyor mu?
        await using var duzenek = await Duzenek.OlusturAsync();
        var eskiDamga = CookieSessionValidator.ComputeStamp(duzenek.Yonetici.PasswordHash);

        await duzenek.ParolaBelirleAsync(duzenek.Yonetici.Id);

        var taze = await duzenek.TazeleAsync(duzenek.Yonetici);
        var eskiCerez = await duzenek.CerezDogrulaAsync(taze, eskiDamga);
        Assert.Null(eskiCerez.Principal);

        // Yeni parolayla açılan oturum ÇALIŞMALI; aksi hâlde koruma "her şeyi reddet"e dönerdi.
        var yeniCerez = await duzenek.CerezDogrulaAsync(taze, CookieSessionValidator.ComputeStamp(taze.PasswordHash));
        Assert.NotNull(yeniCerez.Principal);
    }

    // --- 5) Denetim kaydı: iz kalır, parola kalmaz ---

    [Fact]
    public async Task Islem_DENETIM_KAYDINA_yazilir()
    {
        await using var duzenek = await Duzenek.OlusturAsync();

        await duzenek.ParolaBelirleAsync(duzenek.Yonetici.Id, isletmen: @"SUNUCU\yonetici");

        var kayit = Assert.Single(await duzenek.DbContext.AuditLogs.ToListAsync());
        Assert.Equal("auth.password_set_from_console", kayit.Action);
        Assert.Equal(duzenek.Yonetici.Id, kayit.UserId);
        Assert.Contains("yonetici", kayit.Details, StringComparison.Ordinal);
        Assert.Contains(@"SUNUCU\yonetici", kayit.Details, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PAROLA_denetim_kaydina_DUSMEZ()
    {
        await using var duzenek = await Duzenek.OlusturAsync();

        await duzenek.ParolaBelirleAsync(duzenek.Yonetici.Id);

        Assert.All(
            await duzenek.DbContext.AuditLogs.ToListAsync(),
            kayit =>
            {
                Assert.DoesNotContain(YeniParola, kayit.Details, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(YeniParola, kayit.Action, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(YeniParola, kayit.Resource, StringComparison.OrdinalIgnoreCase);
            });
    }

    // --- 6) İki adımlı doğrulama kararı ---

    [Fact]
    public async Task IKI_ADIMLI_dogrulama_VARSAYILAN_olarak_KORUNUR()
    {
        // BİLİNÇLİ KARAR: parolayı sıfırlarken 2FA'yı da kapatmak, ikinci faktörü tam da var olma
        // sebebinin gerçekleştiği anda işlevsiz kılardı — sunucuya bir kez erişen herkes tek
        // hamlede tam yetkili oturum açardı. Bu test, biri "kolaylık olsun" diye varsayılanı
        // değiştirirse KIRILIR.
        await using var duzenek = await Duzenek.OlusturAsync();
        await duzenek.KurtarmaKoduYazAsync(duzenek.YoneticiIkiAdimli.Id);

        var sonuc = await duzenek.ParolaBelirleAsync(duzenek.YoneticiIkiAdimli.Id);

        var taze = await duzenek.TazeleAsync(duzenek.YoneticiIkiAdimli);
        Assert.False(sonuc.TwoFactorDisabled);
        Assert.True(taze.IsTwoFactorEnabled);
        Assert.NotNull(taze.TwoFactorSecretProtected);
        Assert.NotEmpty(duzenek.KurtarmaKodlariniOku(duzenek.YoneticiIkiAdimli.Id));
    }

    [Fact]
    public async Task IKI_ADIMLI_dogrulama_ACIKCA_istenince_KAPATILIR()
    {
        // Doğrulayıcısını da kaybetmiş kullanıcı için çıkış yolu var; bedeli AÇIK bir istektir.
        await using var duzenek = await Duzenek.OlusturAsync();
        await duzenek.KurtarmaKoduYazAsync(duzenek.YoneticiIkiAdimli.Id);

        var sonuc = await duzenek.ParolaBelirleAsync(duzenek.YoneticiIkiAdimli.Id, ikiAdimliKapat: true);

        var taze = await duzenek.TazeleAsync(duzenek.YoneticiIkiAdimli);
        Assert.True(sonuc.TwoFactorDisabled);
        Assert.False(taze.IsTwoFactorEnabled);
        Assert.Null(taze.TwoFactorSecretProtected);
        Assert.Null(taze.PendingTwoFactorSecretProtected);

        // Kurtarma kodları da düşer: yaşamaya devam etselerdi, kullanıcı 2FA'yı yeniden açtığında
        // eski bir çıktıda duran kodlar hâlâ geçerli olurdu.
        Assert.Empty(duzenek.KurtarmaKodlariniOku(duzenek.YoneticiIkiAdimli.Id));
    }

    [Fact]
    public async Task PASIF_hesap_SESSIZCE_gecilmez()
    {
        // "Parolayı değiştirdim ama hâlâ giremiyorum" duvarı; araç bunu çağırana bildirmek zorunda.
        await using var duzenek = await Duzenek.OlusturAsync();
        duzenek.Yonetici.IsActive = false;
        await duzenek.DbContext.SaveChangesAsync();

        var sonuc = await duzenek.ParolaBelirleAsync(duzenek.Yonetici.Id);

        Assert.False(sonuc.IsActive);
    }

    // --- düzenek ---

    private sealed class Duzenek : IAsyncDisposable
    {
        private readonly SqliteConnection baglanti;
        private readonly string veriKoku;
        private readonly IPasswordHasher<PanelUser> hasher = new PasswordHasher<PanelUser>();

        private Duzenek(SqliteConnection baglanti, AdaPanelDbContext dbContext, string veriKoku)
        {
            this.baglanti = baglanti;
            this.veriKoku = veriKoku;
            DbContext = dbContext;
            DosyaDeposu = new JsonFileStore(
                Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = veriKoku }));
        }

        public AdaPanelDbContext DbContext { get; }

        public JsonFileStore DosyaDeposu { get; }

        public PanelUser Yonetici { get; private set; } = null!;

        public PanelUser YoneticiIkiAdimli { get; private set; } = null!;

        public PanelUser Operator { get; private set; } = null!;

        public PanelUser Musteri { get; private set; } = null!;

        public PanelUser Bayi { get; private set; } = null!;

        public static async Task<Duzenek> OlusturAsync()
        {
            var baglanti = new SqliteConnection("Data Source=:memory:");
            await baglanti.OpenAsync();
            var secenekler = new DbContextOptionsBuilder<AdaPanelDbContext>().UseSqlite(baglanti).Options;
            var dbContext = new AdaPanelDbContext(secenekler);
            await dbContext.Database.EnsureCreatedAsync();

            var veriKoku = Path.Combine(Path.GetTempPath(), $"ada-konsol-{Guid.NewGuid():N}");
            Directory.CreateDirectory(veriKoku);

            var duzenek = new Duzenek(baglanti, dbContext, veriKoku);
            duzenek.Yonetici = duzenek.Kullanici("yonetici", UserRole.Administrator);
            duzenek.YoneticiIkiAdimli = duzenek.Kullanici("yonetici-2fa", UserRole.Administrator, ikiAdimli: true);
            duzenek.Operator = duzenek.Kullanici("operator", UserRole.Operator);
            duzenek.Musteri = duzenek.Kullanici("musteri", UserRole.Customer);
            duzenek.Bayi = duzenek.Kullanici("bayi", UserRole.Reseller);
            dbContext.Users.AddRange(
                duzenek.Yonetici, duzenek.YoneticiIkiAdimli, duzenek.Operator, duzenek.Musteri, duzenek.Bayi);
            await dbContext.SaveChangesAsync();
            return duzenek;
        }

        public PanelUser RolIle(UserRole rol) => rol switch
        {
            UserRole.Operator => Operator,
            UserRole.Customer => Musteri,
            UserRole.Reseller => Bayi,
            _ => Yonetici
        };

        public Task<ConsolePasswordResetOutcome> ParolaBelirleAsync(
            Guid hedefId,
            string parola = YeniParola,
            bool ikiAdimliKapat = false,
            string isletmen = @"SUNUCU\Administrator")
            => ConsolePasswordRecovery.SetPasswordAsync(
                DbContext, hasher, DosyaDeposu, hedefId, parola, ikiAdimliKapat, isletmen);

        public PasswordVerificationResult Dogrula(PanelUser kullanici, string parola)
        {
            var taze = DbContext.Users.AsNoTracking().Single(user => user.Id == kullanici.Id);
            return hasher.VerifyHashedPassword(taze, taze.PasswordHash, parola);
        }

        public async Task<PanelUser> TazeleAsync(PanelUser kullanici)
            => await DbContext.Users.AsNoTracking().SingleAsync(user => user.Id == kullanici.Id);

        public void AnahtarEkle(Guid kullaniciId, string ad)
            => DbContext.ApiTokens.Add(new PanelApiToken
            {
                UserId = kullaniciId,
                Name = ad,
                TokenHash = Guid.NewGuid().ToString("N"),
                Suffix = "a9f3"
            });

        /// <summary>Kurtarma kodu deposunu panelin kullandığı BİÇİMDE doldurur.</summary>
        public async Task KurtarmaKoduYazAsync(Guid kullaniciId)
        {
            var yol = Path.Combine(veriKoku, "config", "2fa");
            Directory.CreateDirectory(yol);
            await File.WriteAllTextAsync(
                Path.Combine(yol, $"{kullaniciId:N}.json"),
                """{"codes":["aaaa","bbbb"]}""");
        }

        public IReadOnlyList<string> KurtarmaKodlariniOku(Guid kullaniciId)
        {
            var yol = Path.Combine(veriKoku, "config", "2fa", $"{kullaniciId:N}.json");
            if (!File.Exists(yol))
            {
                return [];
            }

            var kokDugum = JsonNode.Parse(File.ReadAllText(yol));
            return kokDugum?["codes"]?.AsArray().Select(deger => deger!.ToString()).ToList() ?? [];
        }

        /// <summary>Panelin GERÇEK çerez doğrulayıcısını çalıştırır.</summary>
        public async Task<CookieValidatePrincipalContext> CerezDogrulaAsync(PanelUser kullanici, string damga)
        {
            var kimlik = new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, kullanici.Id.ToString()),
                    new Claim(ClaimTypes.Name, kullanici.UserName),
                    new Claim(ClaimTypes.Role, kullanici.Role.ToString()),
                    new Claim(CookieSessionValidator.CredentialStampClaim, damga),
                    OturumTestOrtami.OturumKimligiTalebi()
                ],
                CookieAuthenticationDefaults.AuthenticationScheme);

            var servisler = new ServiceCollection();
            servisler.AddSingleton(DbContext);
            servisler.AddSingleton(OturumTestOrtami.BosKaraListe());
            servisler.AddSingleton<IAuthenticationService>(new SessizKimlikServisi());
            var http = new DefaultHttpContext { RequestServices = servisler.BuildServiceProvider() };

            var baglam = new CookieValidatePrincipalContext(
                http,
                new AuthenticationScheme(
                    CookieAuthenticationDefaults.AuthenticationScheme,
                    displayName: null,
                    handlerType: typeof(CookieAuthenticationHandler)),
                new CookieAuthenticationOptions(),
                new AuthenticationTicket(
                    new ClaimsPrincipal(kimlik),
                    new AuthenticationProperties(),
                    CookieAuthenticationDefaults.AuthenticationScheme));

            await CookieSessionValidator.OnValidatePrincipalAsync(baglam);
            return baglam;
        }

        private PanelUser Kullanici(string ad, UserRole rol, bool ikiAdimli = false)
        {
            var kullanici = new PanelUser
            {
                Id = Guid.NewGuid(),
                UserName = ad,
                Email = $"{ad}@ornek.test",
                Role = rol,
                PreferredLanguage = "tr",
                IsActive = true,
                IsTwoFactorEnabled = ikiAdimli,
                TwoFactorSecretProtected = ikiAdimli ? "korunmus-gizli-anahtar" : null,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            };
            kullanici.PasswordHash = hasher.HashPassword(kullanici, EskiParola);
            return kullanici;
        }

        public async ValueTask DisposeAsync()
        {
            await DbContext.DisposeAsync();
            await baglanti.DisposeAsync();
            try
            {
                Directory.Delete(veriKoku, recursive: true);
            }
            catch (IOException)
            {
                // Geçici klasör silinemezse test sonucunu etkilemez.
            }
        }
    }

    /// <summary>Çerez doğrulayıcısı reddederken SignOutAsync çağırır; testte sessizce yutulur.</summary>
    private sealed class SessizKimlikServisi : IAuthenticationService
    {
        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme)
            => Task.FromResult(AuthenticateResult.NoResult());

        public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties)
            => Task.CompletedTask;

        public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties)
            => Task.CompletedTask;

        public Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal principal, AuthenticationProperties? properties)
            => Task.CompletedTask;

        public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties)
            => Task.CompletedTask;
    }
}
