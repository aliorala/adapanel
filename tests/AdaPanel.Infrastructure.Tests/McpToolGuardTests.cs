using System.Security.Claims;
using System.Text.RegularExpressions;
using AdaPanel.Api.Mcp;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Dtos.Users;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Security;
using AdaPanel.Infrastructure.Support;
using ModelContextProtocol;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// MCP ARACLARI PANELIN KURALLARINA GERCEKTEN TABI MI?
/// <para>
/// BULUNAN IKI HATA — ikisi de AYNI kok nedenden: kural bir MVC FILTRESINDE yaziliydi ve
/// <c>/mcp</c> MVC boru hattindan GECMEZ.
/// </para>
/// <list type="number">
/// <item>
/// <b>Abonelik askisi:</b> <c>SubscriptionSuspensionFilter</c> MVC filtresi oldugu icin askiya
/// alinmis bir abonelik, panelde "Baslat" dugmesine basamazken <c>AdaPanelWriteTools</c> uzerinden
/// AYNI isi yapmaya devam edebiliyordu — is modelinin yaptirimi yapay zeka kapisindan deliniyordu.
/// </item>
/// <item>
/// <b>Alt-kullanici bolum izinleri:</b> <c>RequireSubUserSectionAttribute</c> de MVC filtresi;
/// bolumleri kisitlanmis bir alt-kullanici kendi anahtariyla <c>sites_list</c>/<c>site_health</c>
/// gibi araclara erisebiliyordu.
/// </item>
/// </list>
/// <para>
/// Sahte kapsam yerine GERCEK <see cref="CustomerScopeService"/> kullaniliyor: alt-kullanicinin
/// parent aboneligini miras almasi ve bolum listesinin normalize edilmesi tam da burada olculmek
/// isteniyor.
/// </para>
/// </summary>
public sealed class McpToolGuardTests : IDisposable
{
    private static readonly Guid Musteri = Guid.Parse("aaaaaaaa-1111-1111-1111-111111111111");
    private static readonly Guid AltKullanici = Guid.Parse("bbbbbbbb-2222-2222-2222-222222222222");

    private readonly string dataRoot = Path.Combine(Path.GetTempPath(), $"ada-mcp-{Guid.NewGuid():N}");
    private readonly CustomerScopeService kapsamlar;

    public McpToolGuardTests()
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

    // --- 1) Abonelik askisi MCP YAZMA araclarini baglar ---

    [Fact]
    public async Task Askidaki_abonelik_MCP_ile_YAZAMAZ()
    {
        await kapsamlar.SetScopeAsync(Musteri, ["ornek"], 5, 1024);
        await kapsamlar.SuspendAsync(Musteri, "traffic");

        var hata = await Assert.ThrowsAsync<McpProtocolException>(
            () => McpCapability.EnsureWriteAllowedAsync(kapsamlar, Kullanici(Musteri, "Customer"), "sites", CancellationToken.None));

        // Sebep gizlenmiyor: model/kullanici "neden calismiyor" diye tahmin etmemeli.
        Assert.Contains("traffic", hata.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Askida_OLMAYAN_abonelik_MCP_ile_YAZABILIR()
    {
        await kapsamlar.SetScopeAsync(Musteri, ["ornek"], 5, 1024);

        await McpCapability.EnsureWriteAllowedAsync(kapsamlar, Kullanici(Musteri, "Customer"), "sites", CancellationToken.None);
    }

    [Fact]
    public async Task Askidaki_aboneligin_ALT_KULLANICISI_da_YAZAMAZ()
    {
        await kapsamlar.SetScopeAsync(Musteri, ["ornek"], 5, 1024);
        await kapsamlar.SuspendAsync(Musteri, "expired");
        await kapsamlar.SetParentSubscriptionAsync(AltKullanici, Musteri);

        await Assert.ThrowsAsync<McpProtocolException>(
            () => McpCapability.EnsureWriteAllowedAsync(kapsamlar, Kullanici(AltKullanici, "Customer"), "sites", CancellationToken.None));
    }

    [Fact]
    public async Task Askidaki_abonelik_MCP_ile_OKUYABILIR()
    {
        // Askidaki musteri neden askida oldugunu gorebilmeli; okumayi da kapatmak sorunu
        // cozulemez hale getirirdi (panel tarafinda da ayni karar verilmisti).
        await kapsamlar.SetScopeAsync(Musteri, ["ornek"], 5, 1024);
        await kapsamlar.SuspendAsync(Musteri, "manual");

        await McpCapability.EnsureReadAllowedAsync(kapsamlar, Kullanici(Musteri, "Customer"), "sites", CancellationToken.None);
    }

    // --- 2) Alt-kullanici bolum izinleri MCP'de de gecerli ---

    [Fact]
    public async Task Bolum_izni_olmayan_alt_kullanici_ARACA_ERISEMEZ()
    {
        await kapsamlar.SetScopeAsync(Musteri, ["ornek"], 5, 1024);
        await kapsamlar.SetParentSubscriptionAsync(AltKullanici, Musteri);
        await kapsamlar.SetSubUserSectionsAsync(AltKullanici, ["mail"]);

        await Assert.ThrowsAsync<McpProtocolException>(
            () => McpCapability.EnsureReadAllowedAsync(kapsamlar, Kullanici(AltKullanici, "Customer"), "sites", CancellationToken.None));
    }

    [Fact]
    public async Task Bolum_izni_OLAN_alt_kullanici_erisebilir()
    {
        await kapsamlar.SetScopeAsync(Musteri, ["ornek"], 5, 1024);
        await kapsamlar.SetParentSubscriptionAsync(AltKullanici, Musteri);
        await kapsamlar.SetSubUserSectionsAsync(AltKullanici, ["mail"]);

        await McpCapability.EnsureReadAllowedAsync(kapsamlar, Kullanici(AltKullanici, "Customer"), "mail", CancellationToken.None);
    }

    [Fact]
    public async Task Top_level_musteri_bolum_kisitindan_ETKILENMEZ()
    {
        // Izin listesi yalniz alt-kullanicida bulunur; abonelik sahibini kisitlamak, ozelligin
        // anlamini degistirirdi.
        await kapsamlar.SetScopeAsync(Musteri, ["ornek"], 5, 1024);

        await McpCapability.EnsureReadAllowedAsync(kapsamlar, Kullanici(Musteri, "Customer"), "sites", CancellationToken.None);
    }

    [Fact]
    public async Task Yonetici_bolum_kisitindan_ETKILENMEZ()
    {
        await McpCapability.EnsureReadAllowedAsync(kapsamlar, Kullanici(Guid.NewGuid(), "Administrator"), "sites", CancellationToken.None);
    }

    // --- 3) Rol denetimleri ---

    [Fact]
    public void Sunucu_geneli_arac_MUSTERIYE_kapali()
    {
        Assert.Throws<McpProtocolException>(() => McpCapability.RequireAdministrator(Kullanici(Musteri, "Customer")));
        McpCapability.RequireAdministrator(Kullanici(Guid.NewGuid(), "Administrator"));
    }

    [Fact]
    public void DNS_araclari_MUSTERIYE_kapali()
    {
        // REST ikizi DnsController [Authorize(Roles = "Administrator,Reseller")] tasiyor; ayni
        // yetenegin iki kapisi ayni kilidi tasimali.
        Assert.Throws<McpProtocolException>(() => McpCapability.RequireDnsRole(Kullanici(Musteri, "Customer")));
        McpCapability.RequireDnsRole(Kullanici(Guid.NewGuid(), "Reseller"));
        McpCapability.RequireDnsRole(Kullanici(Guid.NewGuid(), "Administrator"));
    }

    // --- 4) Kablolama: HER araca kapi konmus mu ---

    /// <summary>
    /// Kuralin var olmasi yetmez: bir araca eklenmeyi unutulursa o arac sessizce muaf olur.
    /// Bu test sinifi kapatir — yeni bir arac eklendiginde kapisiz kalirsa burada patlar.
    /// </summary>
    [Theory]
    [InlineData("AdaPanelReadTools.cs")]
    [InlineData("AdaPanelWriteTools.cs")]
    public void Her_MCP_aracinin_bir_KAPISI_var(string dosya)
    {
        var kaynak = KaynakOku("src", "AdaPanel.Api", "Mcp", dosya);
        var araclar = Regex.Matches(kaynak, @"\[McpServerTool\(Name = ""(?<ad>[a-z_]+)""\)\]");

        // OLCEMEDIK ile SORUN YOK ayrimi: tarama bozulursa test sessizce gecerdi.
        Assert.True(araclar.Count >= 3, $"{dosya} icinde arac bulunamadi; tarama bozulmus olabilir.");

        var kapilar = new[]
        {
            "McpCapability.EnsureReadAllowedAsync",
            "McpCapability.EnsureWriteAllowedAsync",
            "McpCapability.RequireAdministrator",
            "McpCapability.RequireDnsRole"
        };

        var parcalar = kaynak.Split("[McpServerTool");
        foreach (Match arac in araclar)
        {
            var ad = arac.Groups["ad"].Value;

            // whoami yalnizca ISTEGIN KENDI kimligini yansitir (kullanici adi, rol, anahtar
            // yetkileri); panelden hicbir veri okumaz, dolayisiyla kapisi da yoktur.
            if (ad == "whoami")
            {
                continue;
            }

            var govde = parcalar.First(parca => parca.Contains($"Name = \"{ad}\"", StringComparison.Ordinal));
            Assert.True(
                kapilar.Any(kapi => govde.Contains(kapi, StringComparison.Ordinal)),
                $"'{ad}' aracinda yetki/izin kapisi YOK: panelde kapali bir kapi MCP'de acik kalir.");
        }
    }

    [Fact]
    public void Yazan_araclarin_HEPSI_aski_kapisindan_gecer()
    {
        var kaynak = KaynakOku("src", "AdaPanel.Api", "Mcp", "AdaPanelWriteTools.cs");
        var araclar = Regex.Matches(kaynak, @"\[McpServerTool\(Name = ""(?<ad>[a-z_]+)""\)\]");
        var parcalar = kaynak.Split("[McpServerTool");

        Assert.True(araclar.Count >= 3);
        foreach (Match arac in araclar)
        {
            var ad = arac.Groups["ad"].Value;
            var govde = parcalar.First(parca => parca.Contains($"Name = \"{ad}\"", StringComparison.Ordinal));
            Assert.True(
                govde.Contains("McpCapability.EnsureWriteAllowedAsync", StringComparison.Ordinal),
                $"'{ad}' yazma araci abonelik askisini ATLIYOR.");
        }
    }

    [Fact]
    public void Aski_karari_ortak_kuraldan_okunur()
    {
        // Kural yeniden yazilmamali: SubscriptionSuspension zaten var ve MVC tarafi da onu kullaniyor.
        var kaynak = KaynakOku("src", "AdaPanel.Api", "Mcp", "McpCapability.cs");
        Assert.Contains("SubscriptionSuspension.EnsureAllowedAsync", kaynak, StringComparison.Ordinal);
        Assert.Contains("CustomerAccess.EnsureSectionAsync", kaynak, StringComparison.Ordinal);
    }

    [Fact]
    public void Bolum_izni_karari_MVC_ozniteliginde_KOPYALANMAMIS()
    {
        // Oznitelik yalniz kablolama yapmali; iki kopya olsaydi biri guncellenip digeri unutulurdu.
        var kaynak = KaynakOku("src", "AdaPanel.Api", "Security", "RequireSubUserSectionAttribute.cs");
        Assert.Contains("CustomerAccess.EnsureSectionAsync", kaynak, StringComparison.Ordinal);
        Assert.DoesNotContain("GetSubUserSectionsAsync", kaynak, StringComparison.Ordinal);
    }

    // --- duzenek ---

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

    private sealed class TavansizServis : IResellerQuotaService
    {
        public Task<ResellerQuotaDto?> GetQuotaAsync(Guid resellerId, CancellationToken cancellationToken = default)
            => Task.FromResult<ResellerQuotaDto?>(null);

        public Task<ResellerQuotaDto> SetQuotaAsync(Guid resellerId, ResellerQuotaRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RemoveQuotaAsync(Guid resellerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    /// <summary>Kapsam okumasi IIS'e hic dokunmaz; dokunsaydi test bunu ANINDA gorurdu.</summary>
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
