using System.Reflection;
using System.Security.Claims;
using AdaPanel.Api.Controllers;
using AdaPanel.Api.Security;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Mail;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Security;
using AdaPanel.Infrastructure.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// R3: yeni posta uçları (askı, alias güncelleme, yönlendirme, DKIM) ÇAPRAZ-KİRACI korumalıdır.
/// Başka kiracının alan adındaki adres için Customer/Reseller 403 alır ve posta servisi HİÇ çağrılmaz.
/// Gerçek <see cref="CustomerScopeService"/> kullanılır; sahte kapsam kapsam hatasını gizlerdi.
/// </summary>
public sealed class MailLifecycleAccessTests : IDisposable
{
    private static readonly Guid MusteriA = Guid.Parse("aaaaaaaa-1111-1111-1111-111111111111");
    private static readonly Guid MusteriB = Guid.Parse("bbbbbbbb-2222-2222-2222-222222222222");
    private static readonly Guid BayiBos = Guid.Parse("dddddddd-4444-4444-4444-444444444444");
    private static readonly Guid Yonetici = Guid.Parse("eeeeeeee-5555-5555-5555-555555555555");

    private readonly string kok = Path.Combine(Path.GetTempPath(), "ada-mailacc-" + Guid.NewGuid().ToString("N"));
    private readonly JsonFileStore store;
    private readonly CustomerScopeService kapsamlar;
    private readonly List<string> cagrilar = [];

    public MailLifecycleAccessTests()
    {
        Directory.CreateDirectory(kok);
        store = new JsonFileStore(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = kok }));
        var siteler = new PlanLimitsW2bTests.DurumluSiteler(Path.Combine(kok, "siteler"));
        siteler.Ekle("a.test", "Started");
        siteler.Ekle("b.test", "Started");
        kapsamlar = new CustomerScopeService(store, new AskiDenetimKaydiDurustlukTests.PlansizServis(), new AskiDenetimKaydiDurustlukTests.TavansizServis(), siteler);
        kapsamlar.SetScopeAsync(MusteriA, ["a.test"], 5, 1024).GetAwaiter().GetResult();
        kapsamlar.SetScopeAsync(MusteriB, ["b.test"], 5, 1024).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        try { Directory.Delete(kok, recursive: true); } catch { /* en iyi caba */ }
    }

    private static ClaimsPrincipal Kullanici(Guid id, string rol) => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.NameIdentifier, id.ToString()), new Claim(ClaimTypes.Role, rol)], "test"));

    private MailController Kur(ClaimsPrincipal kullanici)
    {
        var posta = MailServerIdentityTests.IdentityProxy.Make<IMailService>((metot, args) =>
        {
            cagrilar.Add(metot);
            return metot switch
            {
                nameof(IMailService.GetForwardAsync) or nameof(IMailService.SetForwardAsync) => Task.FromResult(new MailForwardDto { Address = "x" }),
                nameof(IMailService.RotateDkimAsync) => Task.FromResult(new MailDkimRotationDto { Domain = "a.test" }),
                _ => Task.FromResult(new MailWorkspaceDto())
            };
        });
        var servisler = new ServiceCollection().AddSingleton(store).BuildServiceProvider();
        return new MailController(posta, null!, kapsamlar, null!, new AskiDenetimKaydiDurustlukTests.YakalayanDenetim())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = kullanici, RequestServices = servisler } }
        };
    }

    private static async Task<int> KodAsync(Func<Task> is_) => (await Assert.ThrowsAsync<AppException>(is_)).HttpStatusCode;

    public static IEnumerable<object[]> Roller()
    {
        yield return ["Customer", MusteriA];
        yield return ["Reseller", BayiBos];
    }

    [Theory]
    [MemberData(nameof(Roller))]
    public async Task Baska_kiracinin_alan_adindaki_adres_icin_HER_yeni_uc_403_verir_ve_posta_servisi_CAGRILMAZ(string rol, Guid kimlik)
    {
        var c = Kur(Kullanici(kimlik, rol));
        var t = CancellationToken.None;

        Assert.Equal(403, await KodAsync(() => c.SetAccountActiveAsync(new() { Address = "kurban@b.test", Active = false }, t)));
        Assert.Equal(403, await KodAsync(() => c.UpdateAliasAsync(new() { Address = "info@b.test", Active = false }, t)));
        Assert.Equal(403, await KodAsync(() => c.GetForwardAsync("kurban@b.test", t)));
        Assert.Equal(403, await KodAsync(() => c.SetForwardAsync(new() { Address = "kurban@b.test", Enabled = true, Target = "saldirgan@evil.test" }, t)));
        Assert.Equal(403, await KodAsync(() => c.RotateDkimAsync(new() { Domain = "b.test" }, t)));
        Assert.Empty(cagrilar);
    }

    [Fact]
    public async Task Alias_hedefi_BASKA_kiracinin_alan_adindaysa_reddedilir()
    {
        var c = Kur(Kullanici(MusteriA, "Customer"));
        Assert.Equal(403, await KodAsync(() => c.UpdateAliasAsync(new() { Address = "info@a.test", Target = "kurban@b.test" }, CancellationToken.None)));
        Assert.Empty(cagrilar);
    }

    [Fact]
    public async Task Kendi_alan_adindaki_uclar_calisir()
    {
        var c = Kur(Kullanici(MusteriA, "Customer"));
        var t = CancellationToken.None;
        await c.SetAccountActiveAsync(new() { Address = "ben@a.test", Active = false }, t);
        await c.UpdateAliasAsync(new() { Address = "info@a.test", Target = "ben@a.test" }, t);
        await c.SetForwardAsync(new() { Address = "ben@a.test", Enabled = true, Target = "disari@elsewhere.test" }, t);
        await c.RotateDkimAsync(new() { Domain = "a.test" }, t);
        Assert.Equal(4, cagrilar.Count);
    }

    // --- Yönetici kilidi ---

    [Fact]
    public async Task Yoneticinin_askiya_aldigi_kutuyu_musteri_yeniden_ACAMAZ_yonetici_acabilir()
    {
        var t = CancellationToken.None;
        await Kur(Kullanici(Yonetici, "Administrator")).SetAccountActiveAsync(new() { Address = "ben@a.test", Active = false }, t);
        cagrilar.Clear();

        var musteri = Kur(Kullanici(MusteriA, "Customer"));
        Assert.Equal(403, await KodAsync(() => musteri.SetAccountActiveAsync(new() { Address = "ben@a.test", Active = true }, t)));
        Assert.Empty(cagrilar); // kilit sunucuya hic ulasmadan reddedildi

        await Kur(Kullanici(Yonetici, "Administrator")).SetAccountActiveAsync(new() { Address = "ben@a.test", Active = true }, t);
        cagrilar.Clear();
        await musteri.SetAccountActiveAsync(new() { Address = "ben@a.test", Active = true }, t); // kilit dustu
        Assert.Single(cagrilar);
    }

    [Fact]
    public async Task Musterinin_kendi_askisi_kilit_YAZMAZ_kendisi_yeniden_acabilir()
    {
        var t = CancellationToken.None;
        var musteri = Kur(Kullanici(MusteriA, "Customer"));
        await musteri.SetAccountActiveAsync(new() { Address = "ben@a.test", Active = false }, t);
        await musteri.SetAccountActiveAsync(new() { Address = "ben@a.test", Active = true }, t);
        Assert.False(await AdminLockStore.IsLockedAsync(store, "mail", "ben@a.test", t));
    }

    [Fact]
    public async Task Kutu_silinince_yonetici_kilidi_temizlenir()
    {
        var t = CancellationToken.None;
        await Kur(Kullanici(Yonetici, "Administrator")).SetAccountActiveAsync(new() { Address = "ben@a.test", Active = false }, t);
        Assert.True(await AdminLockStore.IsLockedAsync(store, "mail", "ben@a.test", t));
        await Kur(Kullanici(Yonetici, "Administrator")).DeleteAccountAsync("ben@a.test", t);
        Assert.False(await AdminLockStore.IsLockedAsync(store, "mail", "ben@a.test", t));
    }
}
