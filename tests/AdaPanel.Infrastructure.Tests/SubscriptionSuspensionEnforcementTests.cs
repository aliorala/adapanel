using System.Security.Claims;
using AdaPanel.Api.Security;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Dtos.Users;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Security;
using AdaPanel.Infrastructure.Support;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// ASKI FIILEN BAGLAYICI MI?
/// <para>
/// Bulunan hata: aski yalnizca <c>customer-scopes.json</c> icindeki <c>SuspendedAtUtc</c> alanina
/// yaziliyordu ve API'de HICBIR YERDE sorulmuyordu. Askiya alinan musteri panele giriyor,
/// durdurulmus sitesini "Baslat" ile geri kaldiriyordu; abonelik is modelinin en temel yaptirimi
/// yoktu. Bu testler yaptirimin gercekten calistigini VE fazla calismadigini (okuma, oturum,
/// parola degistirme serbest) dogrular.
/// </para>
/// <para>
/// Sahte kapsam yerine GERCEK <see cref="CustomerScopeService"/> kullaniliyor: alt-kullanicinin
/// parent aboneligini miras almasi (ResolveEffective) tam da burada olculmek isteniyor -- sahte
/// bir kapsamla yazilsaydi test, kuralin kendisini degil kendi taklidini dogrulardi.
/// </para>
/// </summary>
public sealed class SubscriptionSuspensionEnforcementTests : IDisposable
{
    private static readonly Guid Musteri = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid AltKullanici = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Bayi = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private readonly string dataRoot = Path.Combine(Path.GetTempPath(), $"ada-aski-{Guid.NewGuid():N}");
    private readonly CustomerScopeService kapsamlar;

    public SubscriptionSuspensionEnforcementTests()
    {
        Directory.CreateDirectory(dataRoot);
        var store = new JsonFileStore(
            Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = dataRoot }));
        kapsamlar = new CustomerScopeService(store, new PlansizServis(), new TavansizServis(), new KullanilmayanSiteServisi());
    }

    public void Dispose()
    {
        try { Directory.Delete(dataRoot, recursive: true); } catch { /* en iyi caba */ }
    }

    // --- 1) Durum degistiren istek askida REDDEDILIR ---

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task Askidaki_musteri_DURUM_DEGISTIREN_istegi_YAPAMAZ(string yontem)
    {
        await KurAsync(Musteri, askida: true);

        var hata = await Assert.ThrowsAsync<AppException>(() => Dene(Musteri, "Customer", yontem, "/api/v1/sites/ornek/start"));

        Assert.Equal("subscription.suspended_readonly", hata.MessageKey);
        Assert.Equal(403, hata.HttpStatusCode);
        // Sebep gizlenmiyor: kullanici "neden calismiyor" diye tahmin etmemeli.
        Assert.Equal("traffic", hata.Details);
    }

    [Fact]
    public async Task Askidaki_musteri_OKUYABILIR()
    {
        // Askidaki musteri panele girip NEDEN askida oldugunu, kotasini ve faturasini gormeli;
        // cozum tam da orada. Okumayi da kapatmak, sorunu cozulemez hale getirirdi.
        await KurAsync(Musteri, askida: true);

        await Dene(Musteri, "Customer", "GET", "/api/v1/subscriptions/me");
        await Dene(Musteri, "Customer", "HEAD", "/api/v1/sites");
        await Dene(Musteri, "Customer", "OPTIONS", "/api/v1/sites");
    }

    [Theory]
    [InlineData("/api/v1/auth/logout")]
    [InlineData("/api/v1/auth/change-password")]
    [InlineData("/api/v1/auth/2fa/begin")]
    public async Task Oturum_ve_hesap_guvenligi_yollari_MUAF(string yol)
    {
        // Aski bir odeme/kota durumudur, guvenlik olayi degil: musterinin oturumunu kapatma ve
        // parolasini degistirme yetenegini elinden almak askinin amaciyla ilgisiz bir zarardir.
        await KurAsync(Musteri, askida: true);
        await Dene(Musteri, "Customer", "POST", yol);
    }

    [Fact]
    public async Task Aski_YOKSA_yazma_serbest()
    {
        await KurAsync(Musteri, askida: false);
        await Dene(Musteri, "Customer", "POST", "/api/v1/sites/ornek/start");
    }

    // --- 2) Aski YALNIZ askidaki aboneligin sahibini baglar ---

    [Fact]
    public async Task Yonetici_baskasinin_askisindan_ETKILENMEZ()
    {
        // Askiyi cozecek olan yonetici. Bir yonetici hesabina yanlislikla kapsam kaydi dusmus olsa
        // bile panel kilitlenmemeli.
        await KurAsync(Musteri, askida: true);
        await Dene(Musteri, "Administrator", "POST", "/api/v1/subscriptions/x/resume");
    }

    [Fact]
    public async Task Bayi_KENDI_aboneligi_askidaysa_baglanir()
    {
        await KurAsync(Bayi, askida: true);

        var hata = await Assert.ThrowsAsync<AppException>(() => Dene(Bayi, "Reseller", "POST", "/api/v1/sites"));
        Assert.Equal("subscription.suspended_readonly", hata.MessageKey);
    }

    [Fact]
    public async Task Bayi_askida_DEGILSE_musterisi_askidayken_bile_calisir()
    {
        // Bayi kendi musterisini yonetebilmeli: musterinin askisi bayiyi baglamaz.
        await KurAsync(Musteri, askida: true);
        await Dene(Bayi, "Reseller", "POST", "/api/v1/subscriptions/x/resume");
    }

    // --- 3) Alt-kullanici parent'in askisini MIRAS ALIR ---

    [Fact]
    public async Task Alt_kullanici_parentin_askisina_TAKILIR()
    {
        // Alt-kullanici parent'in aboneligini paylasir; aski yalnizca top-level musteriyi
        // baglasaydi, askidaki abonelik altindaki ek kullanici uzerinden calismaya devam ederdi.
        await KurAsync(Musteri, askida: true);
        await kapsamlar.SetParentSubscriptionAsync(AltKullanici, Musteri);

        var hata = await Assert.ThrowsAsync<AppException>(() => Dene(AltKullanici, "Customer", "POST", "/api/v1/files/upload"));
        Assert.Equal("subscription.suspended_readonly", hata.MessageKey);
    }

    [Fact]
    public async Task Alt_kullanici_parent_askida_DEGILKEN_calisir()
    {
        await KurAsync(Musteri, askida: false);
        await kapsamlar.SetParentSubscriptionAsync(AltKullanici, Musteri);

        await Dene(AltKullanici, "Customer", "POST", "/api/v1/files/upload");
    }

    // --- 4) Kural TEK KAYNAKTAN uygulaniyor mu (kablolama) ---

    /// <summary>
    /// Kuralin VAR olmasi yetmez: kuresel filtre olarak kayitli DEGILSE hicbir uc korunmaz.
    /// Controller'a tek tek eklenen bir denetim, yeni controller'da unutulacagi icin fiilen yoktur.
    /// </summary>
    [Fact]
    public void Aski_filtresi_KURESEL_olarak_kayitli()
    {
        var kaynak = KaynakOku("src", "AdaPanel.Api", "Program.cs");
        Assert.Contains("options.Filters.Add<SubscriptionSuspensionFilter>()", kaynak, StringComparison.Ordinal);
    }

    [Fact]
    public void Filtre_karari_ortak_kuraldan_okur()
    {
        // Filtre kendi kopyasini tasimamali; karar SubscriptionSuspension'da tek yerde.
        var kaynak = KaynakOku("src", "AdaPanel.Api", "Security", "SubscriptionSuspensionFilter.cs");
        Assert.Contains("SubscriptionSuspension.EnsureAllowedAsync", kaynak, StringComparison.Ordinal);
    }

    // --- duzenek ---

    private async Task KurAsync(Guid userId, bool askida)
    {
        await kapsamlar.SetScopeAsync(userId, ["ornek"], 5, 1024);
        if (askida)
        {
            await kapsamlar.SuspendAsync(userId, "traffic");
        }
    }

    private Task Dene(Guid userId, string rol, string yontem, string yol) =>
        SubscriptionSuspension.EnsureAllowedAsync(kapsamlar, Kullanici(userId, rol), yontem, yol, CancellationToken.None);

    private static ClaimsPrincipal Kullanici(Guid userId, string rol) => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.NameIdentifier, userId.ToString()), new Claim(ClaimTypes.Role, rol)],
        "test"));

    private static string KaynakOku(params string[] parcalar)
    {
        var kok = AppContext.BaseDirectory;
        while (kok is not null && !File.Exists(Path.Combine(kok, "AdaPanel.sln")))
        {
            kok = Path.GetDirectoryName(kok);
        }

        Assert.NotNull(kok);
        return File.ReadAllText(Path.Combine([kok!, .. parcalar]));
    }

    /// <summary>Plan bagi bu testlerin konusu degil; kapsam yalnizca kayittaki degerlerle calisir.</summary>
    private sealed class PlansizServis : IServicePlanService
    {
        public Task<ServicePlanDto?> GetPlanAsync(string id, CancellationToken cancellationToken = default)
            => Task.FromResult<ServicePlanDto?>(null);

        public Task<IReadOnlyCollection<ServicePlanDto>> GetPlansAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyCollection<ServicePlanDto>>([]);

        public Task<ServicePlanDto> CreatePlanAsync(ServicePlanRequest request, Guid? ownerId = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ServicePlanDto> UpdatePlanAsync(string id, ServicePlanRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeletePlanAsync(string id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    /// <summary>Bayi tavani bu testlerin konusu degil.</summary>
    private sealed class TavansizServis : IResellerQuotaService
    {
        public Task<ResellerQuotaDto?> GetQuotaAsync(Guid resellerId, CancellationToken cancellationToken = default)
            => Task.FromResult<ResellerQuotaDto?>(null);

        public Task<ResellerQuotaDto> SetQuotaAsync(Guid resellerId, ResellerQuotaRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RemoveQuotaAsync(Guid resellerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    /// <summary>GetScopeAsync IIS'e hic dokunmaz; dokunsaydi test bunu ANINDA gorurdu.</summary>
    private sealed class KullanilmayanSiteServisi : IIisSiteService
    {
        public Task<IReadOnlyCollection<SiteDto>> GetSitesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDto> GetSiteAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDto> CreateSiteAsync(CreateSiteRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDto> UpdatePhysicalPathAsync(string siteName, string physicalPath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteSiteAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDto> StartSiteAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDto> StopSiteAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDto> AddBindingAsync(string siteName, SiteBindingRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDto> RemoveBindingAsync(string siteName, RemoveSiteBindingRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<SiteDto>> ImportExistingSitesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
