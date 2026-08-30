using System.Security.Claims;
using AdaPanel.Api.Security;
using AdaPanel.Infrastructure.Security;
using AdaPanel.Core.Entities;
using AdaPanel.Core.Enums;
using AdaPanel.Data.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// OTURUM CEREZI ARTIK GERCEKLE KARSILASTIRILIYOR.
/// <para>
/// Cerez kimlik dogrulamasinda <c>OnValidatePrincipal</c> HIC YOKTU: cerez bir kez verildikten
/// sonra 12 saat (kalicysa 14 gun) sorgusuz gecerliydi. Sonuc, guvenlik olayina verilebilecek ilk
/// yanitin calismamasiydi -- parola degistirmek, kullaniciyi pasife almak, ROLUNU dusurmek ve
/// kullaniciyi SILMEK acik oturumu dusurmuyordu. API anahtari yolunda (PanelTokenAuthentication-
/// Handler) ayni kontrol ZATEN vardi; eksik olan yalnizca cerezdi.
/// </para>
/// <para>
/// Olcut olarak <c>UpdatedAtUtc</c> KULLANILAMAZDI: o alan basarili girişte de BASARISIZ giriste
/// de guncelleniyor, yani saldirganin kurbanin hesabina yaptigi basarisiz bir deneme kurbani
/// oturumdan atardi. Onun yerine parola karmasindan turetilen bir "kimlik damgasi" cereze yazilir.
/// </para>
/// </summary>
public sealed class CookieSessionValidationTests
{
    [Fact]
    public async Task Giriste_cereze_kimlik_damgasi_yazilir()
    {
        await using var duzenek = await Duzenek.OlusturAsync();

        var kimlik = duzenek.Kimlik();
        await CookieSessionValidator.OnSigningInAsync(duzenek.GirisBaglami(kimlik));

        Assert.Equal(
            CookieSessionValidator.ComputeStamp(duzenek.Kullanici.PasswordHash),
            kimlik.FindFirst(CookieSessionValidator.CredentialStampClaim)?.Value);
    }

    [Fact]
    public async Task Gecerli_oturum_kabul_edilir()
    {
        await using var duzenek = await Duzenek.OlusturAsync();

        var baglam = await duzenek.DogrulaAsync();

        Assert.NotNull(baglam.Principal);
        Assert.False(duzenek.CikisYapildi);
    }

    /// <summary>PAROLA DEGISTI: calinmis cerez ayni anda olmeli. Duzeltmenin asil amaci budur.</summary>
    [Fact]
    public async Task Parola_degisince_cerez_dusurulur()
    {
        await using var duzenek = await Duzenek.OlusturAsync();

        duzenek.Kullanici.PasswordHash = "YENI-PAROLA-KARMASI";
        await duzenek.DbContext.SaveChangesAsync();

        var baglam = await duzenek.DogrulaAsync();

        Assert.Null(baglam.Principal);
        Assert.True(duzenek.CikisYapildi);
    }

    [Fact]
    public async Task Kullanici_pasife_alininca_cerez_dusurulur()
    {
        await using var duzenek = await Duzenek.OlusturAsync();

        duzenek.Kullanici.IsActive = false;
        await duzenek.DbContext.SaveChangesAsync();

        Assert.Null((await duzenek.DogrulaAsync()).Principal);
        Assert.True(duzenek.CikisYapildi);
    }

    [Fact]
    public async Task Kullanici_silinince_cerez_dusurulur()
    {
        await using var duzenek = await Duzenek.OlusturAsync();

        duzenek.DbContext.Users.Remove(duzenek.Kullanici);
        await duzenek.DbContext.SaveChangesAsync();

        Assert.Null((await duzenek.DogrulaAsync()).Principal);
        Assert.True(duzenek.CikisYapildi);
    }

    /// <summary>
    /// ROL DUSURULUNCE de dusmeli: rol yetkilendirmenin tamamidir, bir yoneticiyi Customer'a
    /// cevirmek o kisi cikis yapana kadar hicbir sey ifade etmiyordu.
    /// </summary>
    [Fact]
    public async Task Rol_degisince_cerez_dusurulur()
    {
        await using var duzenek = await Duzenek.OlusturAsync();

        duzenek.Kullanici.Role = UserRole.Customer;
        await duzenek.DbContext.SaveChangesAsync();

        Assert.Null((await duzenek.DogrulaAsync()).Principal);
        Assert.True(duzenek.CikisYapildi);
    }

    /// <summary>
    /// DAMGASIZ (bu surumden onceki) cerez REDDEDILIR. "Damga yoksa gecerli say" demek, tam da
    /// kapatilan durumu -- eski cerezin sorgusuz gecmesini -- kalici hale getirirdi. Bedeli,
    /// surume geciste bir kez yeniden giris yapmaktir.
    /// </summary>
    [Fact]
    public async Task Damgasiz_eski_cerez_reddedilir()
    {
        await using var duzenek = await Duzenek.OlusturAsync();

        Assert.Null((await duzenek.DogrulaAsync(damgaEkle: false)).Principal);
        Assert.True(duzenek.CikisYapildi);
    }

    /// <summary>
    /// HER ISTEKTE veritabanina gidilmez: panelin bir sayfasi onlarca istek atar. Aralik icinde
    /// ikinci bir dogrulama ATLANIR (ve bu yuzden o pencerede degisiklik gorunmez -- bilincli).
    /// </summary>
    [Fact]
    public async Task Dogrulama_aralikla_yapilir()
    {
        await using var duzenek = await Duzenek.OlusturAsync();

        var ozellikler = new AuthenticationProperties();
        await duzenek.DogrulaAsync(ozellikler: ozellikler);

        // Ilk turda zaman damgasi yazildi ve cerezin tazelenmesi istendi.
        Assert.NotEmpty(ozellikler.Items);

        // Simdi parola degissin: aralik dolmadigi icin ikinci tur DB'ye hic gitmez.
        duzenek.Kullanici.PasswordHash = "YENI-PAROLA-KARMASI";
        await duzenek.DbContext.SaveChangesAsync();

        Assert.NotNull((await duzenek.DogrulaAsync(ozellikler: ozellikler)).Principal);
        Assert.False(duzenek.CikisYapildi);

        // Aralik makul bir ust sinirda olmali: ASP.NET Identity'nin 30 dakikalik varsayilani bir
        // hosting panelinde fazla genistir.
        Assert.True(CookieSessionValidator.ValidationInterval <= TimeSpan.FromMinutes(5));
    }

    [Fact]
    public void Damga_parola_karmasini_ele_vermez()
    {
        var karma = "AQAAAAIAAYagAAAAEExampleHashValue==";
        var damga = CookieSessionValidator.ComputeStamp(karma);

        Assert.DoesNotContain(damga, karma, StringComparison.Ordinal);
        Assert.NotEqual(CookieSessionValidator.ComputeStamp(karma + "x"), damga);
    }

    // --- duzenek ---

    private sealed class Duzenek : IAsyncDisposable
    {
        private readonly SqliteConnection baglanti;

        // Cerezin VERILDIGI andaki degerler. Testler entity'yi SONRA degistirerek "cerez
        // verildikten sonra degisti" durumunu uretir; damga/rol talebi canli entity'den okunsaydi
        // degisiklik cereze de yansir ve test hicbir seyi olcmezdi.
        private readonly string girisAnindakiKarma;
        private readonly UserRole girisAnindakiRol;

        private Duzenek(SqliteConnection baglanti, AdaPanelDbContext dbContext, PanelUser kullanici)
        {
            this.baglanti = baglanti;
            DbContext = dbContext;
            Kullanici = kullanici;
            girisAnindakiKarma = kullanici.PasswordHash;
            girisAnindakiRol = kullanici.Role;
        }

        public AdaPanelDbContext DbContext { get; }

        public PanelUser Kullanici { get; }

        public bool CikisYapildi { get; private set; }

        public static async Task<Duzenek> OlusturAsync()
        {
            var baglanti = new SqliteConnection("Data Source=:memory:");
            await baglanti.OpenAsync();
            var secenekler = new DbContextOptionsBuilder<AdaPanelDbContext>().UseSqlite(baglanti).Options;
            var dbContext = new AdaPanelDbContext(secenekler);
            await dbContext.Database.EnsureCreatedAsync();

            var kullanici = new PanelUser
            {
                Id = Guid.NewGuid(),
                UserName = "admin",
                Email = "admin@ornek.test",
                Role = UserRole.Administrator,
                PasswordHash = "ILK-PAROLA-KARMASI",
                IsActive = true
            };

            dbContext.Users.Add(kullanici);
            await dbContext.SaveChangesAsync();

            return new Duzenek(baglanti, dbContext, kullanici);
        }

        /// <summary>Bu düzeneğin ürettiği çerezlerin oturum kimliği (çıkış testleri değiştirir).</summary>
        public string OturumKimligi { get; set; } = SessionRevocationStore.NewSessionId();

        /// <summary>Doğrulamada kullanılacak kara liste; varsayılan boş.</summary>
        public SessionRevocationStore KaraListe { get; set; } = OturumTestOrtami.BosKaraListe();

        public ClaimsIdentity Kimlik(bool damgaEkle = false, bool oturumKimligiEkle = true)
        {
            var kimlik = new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, Kullanici.Id.ToString()),
                    new Claim(ClaimTypes.Name, Kullanici.UserName),
                    new Claim(ClaimTypes.Role, girisAnindakiRol.ToString())
                ],
                CookieAuthenticationDefaults.AuthenticationScheme);

            if (oturumKimligiEkle)
            {
                kimlik.AddClaim(OturumTestOrtami.OturumKimligiTalebi(OturumKimligi));
            }

            if (damgaEkle)
            {
                kimlik.AddClaim(new Claim(
                    CookieSessionValidator.CredentialStampClaim,
                    CookieSessionValidator.ComputeStamp(girisAnindakiKarma)));
            }

            return kimlik;
        }

        public CookieSigningInContext GirisBaglami(ClaimsIdentity kimlik) => new(
            HttpBaglami(),
            Sema(),
            new CookieAuthenticationOptions(),
            new ClaimsPrincipal(kimlik),
            new AuthenticationProperties(),
            new CookieOptions());

        public async Task<CookieValidatePrincipalContext> DogrulaAsync(
            bool damgaEkle = true,
            AuthenticationProperties? ozellikler = null,
            bool oturumKimligiEkle = true)
        {
            // Cerez, o ANDAKI parola karmasiyla damgalanmis gibi kurulur; testler karmayi SONRA
            // degistirerek "cerez verildikten sonra degisti" durumunu uretir.
            var bilet = new AuthenticationTicket(
                new ClaimsPrincipal(Kimlik(damgaEkle, oturumKimligiEkle)),
                ozellikler ?? new AuthenticationProperties(),
                CookieAuthenticationDefaults.AuthenticationScheme);

            var baglam = new CookieValidatePrincipalContext(
                HttpBaglami(),
                Sema(),
                new CookieAuthenticationOptions(),
                bilet);

            await CookieSessionValidator.OnValidatePrincipalAsync(baglam);
            return baglam;
        }

        private static AuthenticationScheme Sema() => new(
            CookieAuthenticationDefaults.AuthenticationScheme,
            displayName: null,
            handlerType: typeof(CookieAuthenticationHandler));

        private HttpContext HttpBaglami()
        {
            var servisler = new ServiceCollection();
            servisler.AddSingleton(DbContext);
            servisler.AddSingleton(KaraListe);
            servisler.AddSingleton<IAuthenticationService>(new SahteKimlikServisi(() => CikisYapildi = true));

            return new DefaultHttpContext { RequestServices = servisler.BuildServiceProvider() };
        }

        public async ValueTask DisposeAsync()
        {
            await DbContext.DisposeAsync();
            await baglanti.DisposeAsync();
        }
    }

    /// <summary>Yalniz cikis cagrisini kaydeder: cerezin tarayicidan SILINDIGINI olcmek icin.</summary>
    private sealed class SahteKimlikServisi(Action cikisYapildi) : IAuthenticationService
    {
        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme) => throw new NotSupportedException();
        public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => throw new NotSupportedException();
        public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => throw new NotSupportedException();
        public Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal principal, AuthenticationProperties? properties) => throw new NotSupportedException();

        public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties)
        {
            cikisYapildi();
            return Task.CompletedTask;
        }
    }
}
