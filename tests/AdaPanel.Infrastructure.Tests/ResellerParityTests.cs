using System.Security.Claims;
using AdaPanel.Api.Controllers;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Dtos.Users;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Security;
using AdaPanel.Infrastructure.Services;
using AdaPanel.Infrastructure.Subscriptions;
using AdaPanel.Infrastructure.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// BAYI PARITESI: kendi planlarini tanimlayabilme + kendi TAVANI.
/// <para>
/// Iki bulgu kapatiliyor:
/// </para>
/// <list type="number">
/// <item>
/// <b>Bayi kendi servis planini olusturamiyordu.</b> ServicePlansController tumuyle
/// Administrator'a kilitliydi; Plesk'te bayinin ilk yaptigi is kendi paketlerini tanimlamaktir ve
/// bu olmadan bayi kanali islemez.
/// </item>
/// <item>
/// <b>Bayi kotasi yoktu.</b> Bayiye "toplam N site / N GB" tavani atanamiyordu; bayi yalnizca
/// musterilerine kota veriyor, kendisi sinirsiz dagitiyordu. Ustelik tavan customer-scopes.json'a
/// yazilamazdi: UserManagementService musteri OLMAYAN her kullanicinin kapsam kaydini siliyor
/// (olculdu) -- yani oraya yazilan tavan, bayi her duzenlendiginde sessizce yok olurdu.
/// </item>
/// </list>
/// </summary>
public sealed class ResellerParityTests : IDisposable
{
    private static readonly Guid Bayi = Guid.Parse("aaaaaaaa-1111-1111-1111-111111111111");
    private static readonly Guid OtekiBayi = Guid.Parse("bbbbbbbb-2222-2222-2222-222222222222");
    private static readonly Guid Musteri = Guid.Parse("cccccccc-3333-3333-3333-333333333333");
    private static readonly Guid IkinciMusteri = Guid.Parse("dddddddd-4444-4444-4444-444444444444");

    private readonly string dataRoot = Path.Combine(Path.GetTempPath(), $"ada-bayi-{Guid.NewGuid():N}");
    private readonly ServicePlanService planlar;
    private readonly SahteTavanDeposu tavanlar = new();
    private readonly CustomerScopeService kapsamlar;

    public ResellerParityTests()
    {
        Directory.CreateDirectory(dataRoot);
        var store = new JsonFileStore(
            Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = dataRoot }));
        planlar = new ServicePlanService(store);
        kapsamlar = new CustomerScopeService(store, planlar, tavanlar, new KullanilmayanSiteServisi());
    }

    public void Dispose()
    {
        try { Directory.Delete(dataRoot, recursive: true); } catch { /* en iyi caba */ }
    }

    // --- 1) Bayi KENDI planini yonetir ---

    [Fact]
    public async Task Bayi_kendi_planini_OLUSTURUR_ve_sahibi_KENDISI_olur()
    {
        var sonuc = await PlanControllerı(Bayi, "Reseller").CreateAsync(new ServicePlanRequest { Name = "Bayi Baslangic", MaxSites = 3 }, default);

        var plan = Govde(sonuc);
        Assert.Equal(Bayi.ToString("N"), plan.OwnerId);
    }

    [Fact]
    public async Task Yoneticinin_plani_SISTEM_plani_olur()
    {
        var sonuc = await PlanControllerı(Bayi, "Administrator").CreateAsync(new ServicePlanRequest { Name = "Sistem" }, default);
        Assert.Equal(string.Empty, Govde(sonuc).OwnerId);
    }

    [Fact]
    public async Task Bayi_BASKA_bayinin_planina_DOKUNAMAZ()
    {
        var yabanci = await planlar.CreatePlanAsync(new ServicePlanRequest { Name = "Rakip" }, OtekiBayi);

        var kontrol = PlanControllerı(Bayi, "Reseller");

        var guncelleme = await Assert.ThrowsAsync<AppException>(
            () => kontrol.UpdateAsync(yabanci.Id, new ServicePlanRequest { Name = "Calindi" }, default));
        Assert.Equal("users.forbidden", guncelleme.MessageKey);

        var silme = await Assert.ThrowsAsync<AppException>(() => kontrol.DeleteAsync(yabanci.Id, default));
        Assert.Equal("users.forbidden", silme.MessageKey);
    }

    [Fact]
    public async Task Bayi_SISTEM_planini_DEGISTIREMEZ()
    {
        // Plan bir kopya degil BAG: sistem planini duzenleyen bayi, o plana bagli BUTUN
        // abonelikleri (baska bayilerinkiler + yoneticinin dogrudan musterileri) aninda etkilerdi.
        var sistem = await planlar.CreatePlanAsync(new ServicePlanRequest { Name = "Sistem" });

        var hata = await Assert.ThrowsAsync<AppException>(
            () => PlanControllerı(Bayi, "Reseller").UpdateAsync(sistem.Id, new ServicePlanRequest { Name = "x" }, default));
        Assert.Equal("users.forbidden", hata.MessageKey);
    }

    [Fact]
    public async Task Bayi_listede_KENDI_ve_SISTEM_planlarini_gorur_RAKIBININKINI_gormez()
    {
        await planlar.CreatePlanAsync(new ServicePlanRequest { Name = "Sistem" });
        await planlar.CreatePlanAsync(new ServicePlanRequest { Name = "Benim" }, Bayi);
        await planlar.CreatePlanAsync(new ServicePlanRequest { Name = "Rakip" }, OtekiBayi);

        var gorunen = Govde(await PlanControllerı(Bayi, "Reseller").GetAsync(default)).Select(plan => plan.Name).ToArray();

        Assert.Contains("Sistem", gorunen);
        Assert.Contains("Benim", gorunen);
        // Plan adi fiyat/paket bilgisidir; rakibin paket yapisi panelden okunabilmemeli.
        Assert.DoesNotContain("Rakip", gorunen);
    }

    [Fact]
    public async Task Yonetici_HEPSINI_gorur()
    {
        await planlar.CreatePlanAsync(new ServicePlanRequest { Name = "Benim" }, Bayi);
        await planlar.CreatePlanAsync(new ServicePlanRequest { Name = "Rakip" }, OtekiBayi);

        Assert.Equal(2, Govde(await PlanControllerı(Bayi, "Administrator").GetAsync(default)).Count);
    }

    [Fact]
    public async Task Duzenleme_sahipligi_DEVREDEMEZ()
    {
        // Sahiplik yalniz olusturmada yazilir; duzenleme istegiyle devredilebilseydi bayi planı
        // kendi ustune alabilir ya da sistem planina cevirebilirdi.
        var plan = await planlar.CreatePlanAsync(new ServicePlanRequest { Name = "Benim" }, Bayi);
        var guncel = await planlar.UpdatePlanAsync(plan.Id, new ServicePlanRequest { Name = "Yeni" });

        Assert.Equal(Bayi.ToString("N"), guncel.OwnerId);
    }

    // --- 2) Bayi tavani: plan tanimlarken ---

    [Fact]
    public async Task Tavanin_USTUNDE_plan_tanimlanamaz()
    {
        tavanlar.Ata(Bayi, maxSites: 10, maxDiskMb: 5000);

        var hata = await Assert.ThrowsAsync<AppException>(
            () => PlanControllerı(Bayi, "Reseller").CreateAsync(new ServicePlanRequest { Name = "Sisirilmis", MaxSites = 100, MaxDiskMb = 1000 }, default));

        Assert.Equal("reseller.site_quota_exceeded", hata.MessageKey);
        Assert.Equal(403, hata.HttpStatusCode);
    }

    [Fact]
    public async Task Tavani_olan_bayi_SINIRSIZ_plan_tanimlayamaz()
    {
        // 0 = sinirsiz. Serbest birakilsaydi tavan tek hamlede anlamsizlasirdi.
        tavanlar.Ata(Bayi, maxSites: 10, maxDiskMb: 5000);

        var hata = await Assert.ThrowsAsync<AppException>(
            () => PlanControllerı(Bayi, "Reseller").CreateAsync(new ServicePlanRequest { Name = "Sinirsiz", MaxSites = 0, MaxDiskMb = 0 }, default));
        Assert.Equal("reseller.site_quota_exceeded", hata.MessageKey);
    }

    [Fact]
    public async Task Tavana_SIGAN_plan_kabul_edilir()
    {
        tavanlar.Ata(Bayi, maxSites: 10, maxDiskMb: 5000);

        var plan = Govde(await PlanControllerı(Bayi, "Reseller").CreateAsync(
            new ServicePlanRequest { Name = "Uygun", MaxSites = 4, MaxDiskMb = 1000 }, default));

        Assert.Equal(4, plan.MaxSites);
    }

    [Fact]
    public async Task Tavan_TANIMSIZSA_mevcut_davranis_korunur()
    {
        // Tavani olmayan bayi bugunku gibi calismaya devam etmeli; yoksa surum, calisan
        // kurulumlari kirardi.
        var plan = Govde(await PlanControllerı(Bayi, "Reseller").CreateAsync(
            new ServicePlanRequest { Name = "Sinirsiz", MaxSites = 0 }, default));

        Assert.Equal(0, plan.MaxSites);
    }

    // --- 3) Bayi tavani: musterilere DAGITIM toplami ---

    [Fact]
    public async Task Dagitilan_TOPLAM_tavani_asamaz()
    {
        tavanlar.Ata(Bayi, maxSites: 10, maxDiskMb: 10_000);
        await kapsamlar.SetOwnerAsync(Musteri, Bayi);
        await kapsamlar.SetOwnerAsync(IkinciMusteri, Bayi);
        await kapsamlar.SetScopeAsync(Musteri, [], maxSites: 8, maxDiskMb: 8000);

        // 8 + 5 = 13 > 10
        var hata = await Assert.ThrowsAsync<AppException>(() => ResellerQuotaGuard.EnsureAllocationFitsAsync(
            tavanlar, kapsamlar, Bayi, IkinciMusteri, newMaxSites: 5, newMaxDiskMb: 1000));

        Assert.Equal("reseller.site_quota_exceeded", hata.MessageKey);
    }

    [Fact]
    public async Task Tavana_sigan_dagitim_kabul_edilir()
    {
        tavanlar.Ata(Bayi, maxSites: 10, maxDiskMb: 10_000);
        await kapsamlar.SetOwnerAsync(Musteri, Bayi);
        await kapsamlar.SetOwnerAsync(IkinciMusteri, Bayi);
        await kapsamlar.SetScopeAsync(Musteri, [], maxSites: 6, maxDiskMb: 6000);

        await ResellerQuotaGuard.EnsureAllocationFitsAsync(
            tavanlar, kapsamlar, Bayi, IkinciMusteri, newMaxSites: 4, newMaxDiskMb: 4000);
    }

    [Fact]
    public async Task AYNI_musterinin_limiti_iki_kez_SAYILMAZ()
    {
        // Hedef musterinin ESKI payi dusulmezse, ayni plani yeniden baglamak bile tavani asiyor
        // gorunurdu.
        tavanlar.Ata(Bayi, maxSites: 10, maxDiskMb: 10_000);
        await kapsamlar.SetOwnerAsync(Musteri, Bayi);
        await kapsamlar.SetScopeAsync(Musteri, [], maxSites: 9, maxDiskMb: 9000);

        await ResellerQuotaGuard.EnsureAllocationFitsAsync(
            tavanlar, kapsamlar, Bayi, Musteri, newMaxSites: 9, newMaxDiskMb: 9000);
    }

    [Fact]
    public async Task Bayinin_KENDI_siteleri_de_tavandan_duser()
    {
        // Aksi halde bayi hic musteri acmadan dogrudan site kurarak tavani tamamen atlardi.
        tavanlar.Ata(Bayi, maxSites: 10, maxDiskMb: 10_000);
        await kapsamlar.SetScopeAsync(Bayi, ["a", "b", "c", "d", "e", "f", "g"], maxSites: 0, maxDiskMb: 0);
        await kapsamlar.SetOwnerAsync(Musteri, Bayi);

        var hata = await Assert.ThrowsAsync<AppException>(() => ResellerQuotaGuard.EnsureAllocationFitsAsync(
            tavanlar, kapsamlar, Bayi, Musteri, newMaxSites: 5, newMaxDiskMb: 1000));
        Assert.Equal("reseller.site_quota_exceeded", hata.MessageKey);
    }

    // --- 4) Plan BAGLAMA ucu: gizleme sus olmamali, tavan sistem planiyla atlanamamali ---

    [Fact]
    public async Task Bayi_BASKA_bayinin_planini_musterisine_BAGLAYAMAZ()
    {
        // Liste ucu rakibin planini gizliyor; baglama ucu kabul etseydi gizleme yalniz sus olurdu.
        // Plan kimligi tahmin edilemez ama sizabilir (ekran goruntusu, gunluk, destek yazismasi).
        var yabanci = await planlar.CreatePlanAsync(new ServicePlanRequest { Name = "Rakip", MaxSites = 1 }, OtekiBayi);
        await kapsamlar.SetOwnerAsync(Musteri, Bayi);

        var hata = await Assert.ThrowsAsync<AppException>(() => AbonelikControllerı(Bayi, "Reseller")
            .SetPlanAsync(Musteri, new SetSubscriptionPlanRequest { PlanId = yabanci.Id }, default));

        Assert.Equal("users.forbidden", hata.MessageKey);
    }

    [Fact]
    public async Task Bayi_SISTEM_planiyla_tavanini_ATLAYAMAZ()
    {
        // Bayi sistem planlarini da baglayabiliyor; denetim planin SAHIBINE degil dagitilan
        // TOPLAMA bakmasaydi, 10 sitelik tavani olan bayi 100 sitelik sistem planini baglayarak
        // tavanini tek hamlede atlardi.
        var buyuk = await planlar.CreatePlanAsync(new ServicePlanRequest { Name = "Kurumsal", MaxSites = 100, MaxDiskMb = 100_000 });
        tavanlar.Ata(Bayi, maxSites: 10, maxDiskMb: 10_000);
        await kapsamlar.SetOwnerAsync(Musteri, Bayi);

        var hata = await Assert.ThrowsAsync<AppException>(() => AbonelikControllerı(Bayi, "Reseller")
            .SetPlanAsync(Musteri, new SetSubscriptionPlanRequest { PlanId = buyuk.Id }, default));

        Assert.Equal("reseller.site_quota_exceeded", hata.MessageKey);
    }

    [Fact]
    public async Task Tavana_sigan_plan_BAGLANIR()
    {
        var uygun = await planlar.CreatePlanAsync(new ServicePlanRequest { Name = "Kucuk", MaxSites = 3, MaxDiskMb = 3000 }, Bayi);
        tavanlar.Ata(Bayi, maxSites: 10, maxDiskMb: 10_000);
        await kapsamlar.SetOwnerAsync(Musteri, Bayi);

        await AbonelikControllerı(Bayi, "Reseller").SetPlanAsync(Musteri, new SetSubscriptionPlanRequest { PlanId = uygun.Id }, default);

        Assert.Equal(3, (await kapsamlar.GetScopeAsync(Musteri)).MaxSites);
    }

    [Fact]
    public async Task Yonetici_plan_baglarken_bayi_tavanina_TAKILMAZ()
    {
        // Tavan bayinin dagitim siniridir; yonetici zaten tavanin ustundedir.
        var buyuk = await planlar.CreatePlanAsync(new ServicePlanRequest { Name = "Kurumsal", MaxSites = 100 });
        tavanlar.Ata(Bayi, maxSites: 10, maxDiskMb: 10_000);
        await kapsamlar.SetOwnerAsync(Musteri, Bayi);

        await AbonelikControllerı(Bayi, "Administrator").SetPlanAsync(Musteri, new SetSubscriptionPlanRequest { PlanId = buyuk.Id }, default);

        Assert.Equal(100, (await kapsamlar.GetScopeAsync(Musteri)).MaxSites);
    }

    [Fact]
    public async Task Bayi_KENDI_tavanini_okur_BASKASININKINI_okuyamaz()
    {
        tavanlar.Ata(Bayi, maxSites: 10, maxDiskMb: 10_000);
        await kapsamlar.SetOwnerAsync(Musteri, Bayi);
        await kapsamlar.SetScopeAsync(Musteri, [], maxSites: 4, maxDiskMb: 4000);

        var kendi = Govde(await AbonelikControllerı(Bayi, "Reseller").GetResellerQuotaAsync(Bayi, default));
        Assert.Equal(10, kendi.MaxSites);
        Assert.Equal(4, kendi.AllocatedSites);
        Assert.Equal(1, kendi.CustomerCount);

        var hata = await Assert.ThrowsAsync<AppException>(
            () => AbonelikControllerı(Bayi, "Reseller").GetResellerQuotaAsync(OtekiBayi, default));
        Assert.Equal("users.forbidden", hata.MessageKey);
    }

    // --- 5) Tavan, kapsam okumasinda GORUNUR (mevcut kota denetimleri kendiliginden calisir) ---

    [Fact]
    public async Task Tavan_kapsam_kaydi_YOKKEN_bile_okunur()
    {
        // Bayinin kapsam kaydi olmayabilir (UserManagementService musteri olmayanin kaydini siler).
        // Tavan yalniz kayittan okunsaydi, bayinin site kotasi hicbir zaman devreye girmezdi.
        tavanlar.Ata(Bayi, maxSites: 7, maxDiskMb: 700);

        var kapsam = await kapsamlar.GetScopeAsync(Bayi);

        Assert.Equal(7, kapsam.MaxSites);
        Assert.Equal(700, kapsam.MaxDiskMb);
    }

    [Fact]
    public async Task Tavan_PLAN_limitlerinden_once_gelir()
    {
        tavanlar.Ata(Bayi, maxSites: 7, maxDiskMb: 700);
        await kapsamlar.SetScopeAsync(Bayi, ["a"], maxSites: 99, maxDiskMb: 9900);

        var kapsam = await kapsamlar.GetScopeAsync(Bayi);

        Assert.Equal(7, kapsam.MaxSites);
        Assert.Single(kapsam.Sites); // siteler DEGISMEZ: tavan yalniz limitleri belirler
    }

    [Fact]
    public async Task Tavani_olmayan_kullanicinin_kapsami_DEGISMEZ()
    {
        await kapsamlar.SetScopeAsync(Musteri, ["a"], maxSites: 3, maxDiskMb: 300);

        var kapsam = await kapsamlar.GetScopeAsync(Musteri);

        Assert.Equal(3, kapsam.MaxSites);
        Assert.Equal(300, kapsam.MaxDiskMb);
    }

    // --- duzenek ---

    private ServicePlansController PlanControllerı(Guid userId, string rol) => new(planlar, tavanlar)
    {
        ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = Kullanici(userId, rol) }
        }
    };

    private SubscriptionsController AbonelikControllerı(Guid userId, string rol) =>
        new(new SahteDayatma(), kapsamlar, planlar, tavanlar)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = Kullanici(userId, rol) }
            }
        };

    private static ClaimsPrincipal Kullanici(Guid userId, string rol) => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.NameIdentifier, userId.ToString()), new Claim(ClaimTypes.Role, rol)],
        "test"));

    private static T Govde<T>(ActionResult<ApiResponse<T>> sonuc)
    {
        var ok = Assert.IsType<OkObjectResult>(sonuc.Result);
        var yanit = Assert.IsType<ApiResponse<T>>(ok.Value);
        Assert.NotNull(yanit.Data);
        return yanit.Data!;
    }

    /// <summary>
    /// Tavan deposu: gercek servis rol dogrulamasi icin DbContext ister, bu testlerin konusu ise
    /// tavanin ETKISI. Depolama davranisi ResellerQuotaService'in kendi testlerinde.
    /// </summary>
    private sealed class SahteTavanDeposu : IResellerQuotaService
    {
        private readonly Dictionary<Guid, ResellerQuotaDto> kayitlar = [];

        public void Ata(Guid resellerId, int maxSites, int maxDiskMb) =>
            kayitlar[resellerId] = new ResellerQuotaDto { ResellerId = resellerId, MaxSites = maxSites, MaxDiskMb = maxDiskMb };

        public Task<ResellerQuotaDto?> GetQuotaAsync(Guid resellerId, CancellationToken cancellationToken = default)
            => Task.FromResult(kayitlar.TryGetValue(resellerId, out var kayit) ? kayit : null);

        public Task<ResellerQuotaDto> SetQuotaAsync(Guid resellerId, ResellerQuotaRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RemoveQuotaAsync(Guid resellerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    /// <summary>Askı/kullanım hesabı bu testlerin konusu değil; SubscriptionEnforcementTests kapsıyor.</summary>
    private sealed class SahteDayatma : ISubscriptionEnforcementService
    {
        public Task<SubscriptionUsageDto> GetUsageAsync(Guid userId, CancellationToken cancellationToken = default)
            => Task.FromResult(new SubscriptionUsageDto { UserId = userId });

        public Task<IReadOnlyCollection<SubscriptionEnforcementChangeDto>> EnforceAllAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SuspendAsync(Guid userId, string reason, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ResumeAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

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
