using System.Reflection;
using System.Security.Claims;
using AdaPanel.Api.Controllers;
using AdaPanel.Api.Mcp;
using AdaPanel.Api.Security;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Dtos.Users;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Security;
using AdaPanel.Infrastructure.Support;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using ModelContextProtocol;
using Xunit;
using static AdaPanel.Infrastructure.Tests.DnsZoneFileTests;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// A6 (ürün kararı): Customer kendi sitesinin DNS'ini yönetir — BAŞKASININKINE dokunamaz.
/// <para>
/// GERÇEK <see cref="CustomerScopeService"/> kullanılır (sahte kapsam, kapsam hatasını gizlerdi) ve
/// denetleyici doğrudan çağrılır: kapsam denetimi, rol özniteliği ve MVC filtreleri ayrı ayrı ölçülür.
/// </para>
/// </summary>
public sealed class DnsCustomerAccessTests : IDisposable
{
    private static readonly Guid MusteriA = Guid.Parse("aaaaaaaa-1111-1111-1111-111111111111");
    private static readonly Guid MusteriB = Guid.Parse("bbbbbbbb-2222-2222-2222-222222222222");
    private static readonly Guid AltKullanici = Guid.Parse("cccccccc-3333-3333-3333-333333333333");

    private readonly string dataRoot = Path.Combine(Path.GetTempPath(), $"ada-dnsacc-{Guid.NewGuid():N}");
    private readonly CustomerScopeService kapsamlar;

    public DnsCustomerAccessTests()
    {
        Directory.CreateDirectory(dataRoot);
        var store = new JsonFileStore(
            Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = dataRoot }));
        kapsamlar = new CustomerScopeService(store, new PlansizServis(), new TavansizServis(), new KullanilmayanSiteServisi());
        kapsamlar.SetScopeAsync(MusteriA, ["siteA"], 5, 1024).GetAwaiter().GetResult();
        kapsamlar.SetScopeAsync(MusteriB, ["siteB"], 5, 1024).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        try { Directory.Delete(dataRoot, recursive: true); } catch { /* en iyi caba */ }
    }

    private static ClaimsPrincipal Kullanici(Guid id, string rol) => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.NameIdentifier, id.ToString()), new Claim(ClaimTypes.Role, rol)], "test"));

    private (DnsController Denetleyici, IzleyenDns Dns, AskiDenetimKaydiDurustlukTests.YakalayanDenetim Denetim) Kur(ClaimsPrincipal kullanici)
    {
        var dns = new IzleyenDns();
        var denetim = new AskiDenetimKaydiDurustlukTests.YakalayanDenetim();
        var denetleyici = new DnsController(dns, kapsamlar, denetim)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = kullanici } }
        };
        return (denetleyici, dns, denetim);
    }

    private static async Task<int> KodAsync(Func<Task> is_)
    {
        var hata = await Assert.ThrowsAsync<AppException>(is_);
        return hata.HttpStatusCode;
    }

    // --- Rol / öznitelik kablolaması ---

    [Fact]
    public void Sinif_Customer_rolunu_icerir_ve_alt_kullanici_sites_bolum_iznine_tabidir()
    {
        var yetki = typeof(DnsController).GetCustomAttribute<AuthorizeAttribute>();
        Assert.NotNull(yetki);
        Assert.Equal("Administrator,Reseller,Customer", yetki!.Roles);

        var bolum = typeof(DnsController).GetCustomAttribute<RequireSubUserSectionAttribute>();
        Assert.NotNull(bolum);
    }

    [Theory]
    [InlineData("SetZoneModeAsync")]
    [InlineData("TransferNowAsync")]
    [InlineData("InstallWindowsDnsAsync")]
    [InlineData("ResetDefaultsAsync")]
    [InlineData("SetDnssecAsync")]
    public void Sunucu_duzeyi_uclar_YALNIZ_yoneticiye_kilitli_kalir(string metot)
    {
        var yetki = typeof(DnsController).GetMethod(metot)!.GetCustomAttribute<AuthorizeAttribute>();

        Assert.NotNull(yetki);
        Assert.Equal("Administrator", yetki!.Roles);
    }

    // --- Sahiplik: Customer A, Customer B'nin sitesine DOKUNAMAZ ---

    [Fact]
    public async Task Musteri_BASKASININ_sitesinin_bolgesini_OKUYAMAZ_ve_DISA_AKTARAMAZ()
    {
        var (d, dns, _) = Kur(Kullanici(MusteriA, "Customer"));

        Assert.Equal(403, await KodAsync(() => d.GetAsync("siteB", CancellationToken.None)));
        Assert.Equal(403, await KodAsync(() => d.ExportAsync("siteB", CancellationToken.None)));
        Assert.Empty(dns.OkunanSiteler);
    }

    [Fact]
    public async Task Musteri_BASKASININ_sitesine_kayit_YAZAMAZ_ve_SILEMEZ()
    {
        var (d, dns, denetim) = Kur(Kullanici(MusteriA, "Customer"));

        Assert.Equal(403, await KodAsync(() => d.UpsertRecordAsync("siteB", new UpsertDnsRecordRequest { Type = "A", Host = "@", Value = "203.0.113.5" }, CancellationToken.None)));
        Assert.Equal(403, await KodAsync(() => d.DeleteRecordAsync("siteB", "x1", CancellationToken.None)));
        Assert.Empty(dns.Yazilanlar);
        Assert.Empty(dns.Silinenler);
        Assert.Throws<InvalidOperationException>(() => denetim.Son("dns.record_saved"));
    }

    [Fact]
    public async Task Musteri_BASKASININ_sitesine_ice_aktarim_YAPAMAZ()
    {
        var (d, dns, _) = Kur(Kullanici(MusteriA, "Customer"));
        const string metin = "www IN A 203.0.113.5";

        Assert.Equal(403, await KodAsync(() => d.ImportPreviewAsync("siteB", new DnsZoneImportPreviewRequest { ZoneText = metin }, CancellationToken.None)));
        Assert.Equal(403, await KodAsync(() => d.ImportApplyAsync("siteB", new DnsZoneImportApplyRequest { ZoneText = metin, PreviewHash = "x" }, CancellationToken.None)));
        Assert.Empty(dns.Yazilanlar);
        Assert.Empty(dns.OkunanSiteler);
    }

    [Fact]
    public async Task Musteri_KENDI_sitesinde_okur_yazar_siler_ve_ice_aktarir()
    {
        var (d, dns, denetim) = Kur(Kullanici(MusteriA, "Customer"));

        Assert.IsType<OkObjectResult>((await d.GetAsync("siteA", CancellationToken.None)).Result);
        Assert.IsType<OkObjectResult>((await d.ExportAsync("siteA", CancellationToken.None)).Result);
        Assert.IsType<OkObjectResult>((await d.UpsertRecordAsync("siteA", new UpsertDnsRecordRequest { Type = "A", Host = "app", Value = "203.0.113.5" }, CancellationToken.None)).Result);
        Assert.IsType<OkObjectResult>((await d.DeleteRecordAsync("siteA", "x1", CancellationToken.None)).Result);
        Assert.Single(dns.Yazilanlar);
        Assert.Single(dns.Silinenler);

        const string metin = "yeni IN A 203.0.113.9";
        var onizleme = ((await d.ImportPreviewAsync("siteA", new DnsZoneImportPreviewRequest { ZoneText = metin }, CancellationToken.None)).Result as OkObjectResult)!;
        var hash = ((AdaPanel.Core.Dtos.Common.ApiResponse<DnsZoneImportPreviewDto>)onizleme.Value!).Data!.PreviewHash;
        Assert.IsType<OkObjectResult>((await d.ImportApplyAsync("siteA", new DnsZoneImportApplyRequest { ZoneText = metin, PreviewHash = hash }, CancellationToken.None)).Result);
        Assert.Equal(2, dns.Yazilanlar.Count);

        // Her mutasyon iz bırakır ve içe aktarım kaydı DEĞER içermez.
        Assert.Contains("added=1", denetim.Son("dns.zone_imported"), StringComparison.Ordinal);
        Assert.DoesNotContain("203.0.113.9", denetim.Son("dns.zone_imported"), StringComparison.Ordinal);
        Assert.DoesNotContain("203.0.113.9", denetim.Son("dns.import_previewed"), StringComparison.Ordinal);
    }

    // --- Paylaşımlı bölge: kapsam dışı üye varsa 403 ---

    [Fact]
    public async Task Paylasimli_bolgede_kapsam_disi_uye_varsa_musteri_YAZAMAZ_ve_SILEMEZ()
    {
        var (d, dns, _) = Kur(Kullanici(MusteriA, "Customer"));
        dns.PaylasimUyeleri = ["siteB"]; // aynı yerel bölgeyi B'nin sitesi de kullanıyor

        Assert.Equal(403, await KodAsync(() => d.UpsertRecordAsync("siteA", new UpsertDnsRecordRequest { Type = "A", Host = "app", Value = "203.0.113.5" }, CancellationToken.None)));
        Assert.Equal(403, await KodAsync(() => d.DeleteRecordAsync("siteA", "x1", CancellationToken.None)));
        Assert.Equal(403, await KodAsync(() => d.ImportApplyAsyncBasit("siteA", dns)));
        Assert.Empty(dns.Yazilanlar);
        Assert.Empty(dns.Silinenler);
    }

    [Fact]
    public async Task Paylasimli_bolgede_TUM_uyeler_musterinin_ise_yazabilir()
    {
        await kapsamlar.SetScopeAsync(MusteriA, ["siteA", "siteA2"], 5, 1024);
        var (d, dns, _) = Kur(Kullanici(MusteriA, "Customer"));
        dns.PaylasimUyeleri = ["siteA2"];

        Assert.IsType<OkObjectResult>((await d.UpsertRecordAsync("siteA", new UpsertDnsRecordRequest { Type = "A", Host = "app", Value = "203.0.113.5" }, CancellationToken.None)).Result);
    }

    [Fact]
    public async Task Yonetici_paylasimli_bolgede_kapsamdan_muaf()
    {
        var (d, dns, _) = Kur(Kullanici(Guid.NewGuid(), "Administrator"));
        dns.PaylasimUyeleri = ["siteB"];

        Assert.IsType<OkObjectResult>((await d.UpsertRecordAsync("siteA", new UpsertDnsRecordRequest { Type = "A", Host = "app", Value = "203.0.113.5" }, CancellationToken.None)).Result);
    }

    [Fact]
    public async Task Kapsamli_olmayan_rol_onay_geri_cagrisindan_GECEMEZ()
    {
        var onay = DnsRecordApproval.Create(kapsamlar, Kullanici(Guid.NewGuid(), "Operator"), "siteA");
        Assert.Equal(403, await KodAsync(() => onay(["siteA"], CancellationToken.None)));
    }

    // --- Askı: DNS yolları askıdan muaf DEĞİL ---

    [Theory]
    [InlineData("POST", "/api/v1/sites/siteA/dns/records")]
    [InlineData("DELETE", "/api/v1/sites/siteA/dns/records/abc")]
    [InlineData("PUT", "/api/v1/sites/siteA/dns/zone")]
    [InlineData("POST", "/api/v1/sites/siteA/dns/import/apply")]
    public async Task Askidaki_musteri_DNS_YAZMA_isteklerinde_kesilir(string yontem, string yol)
    {
        await kapsamlar.SuspendAsync(MusteriA, "manual");

        var hata = await Assert.ThrowsAsync<AppException>(() => SubscriptionSuspension.EnsureAllowedAsync(
            kapsamlar, Kullanici(MusteriA, "Customer"), yontem, yol, CancellationToken.None));
        Assert.Equal("subscription.suspended_readonly", hata.MessageKey);
    }

    [Fact]
    public async Task Askidaki_musteri_DNS_OKUYABILIR()
    {
        await kapsamlar.SuspendAsync(MusteriA, "manual");

        await SubscriptionSuspension.EnsureAllowedAsync(
            kapsamlar, Kullanici(MusteriA, "Customer"), "GET", "/api/v1/sites/siteA/dns", CancellationToken.None);
        await SubscriptionSuspension.EnsureAllowedAsync(
            kapsamlar, Kullanici(MusteriA, "Customer"), "GET", "/api/v1/sites/siteA/dns/export", CancellationToken.None);
    }

    // --- Alt-kullanıcı bölüm izni ---

    [Fact]
    public async Task Sites_bolum_izni_olmayan_alt_kullanici_DNS_ye_giremez()
    {
        await kapsamlar.SetParentSubscriptionAsync(AltKullanici, MusteriA);
        await kapsamlar.SetSubUserSectionsAsync(AltKullanici, ["mail"]);

        var kod = await KodAsync(() => CustomerAccess.EnsureSectionAsync(kapsamlar, Kullanici(AltKullanici, "Customer"), "sites", CancellationToken.None));
        Assert.Equal(403, kod);
    }

    // --- MCP: aynı kurallar ---

    [Fact]
    public void MCP_DNS_rol_kapisi_musteriye_acik_ama_baska_roller_kapali()
    {
        McpCapability.RequireDnsRole(Kullanici(MusteriA, "Customer"));
        McpCapability.RequireDnsRole(Kullanici(Guid.NewGuid(), "Reseller"));
        McpCapability.RequireDnsRole(Kullanici(Guid.NewGuid(), "Administrator"));
        Assert.Throws<McpProtocolException>(() => McpCapability.RequireDnsRole(Kullanici(Guid.NewGuid(), "Operator")));
    }

    // --- duzenek ---

    /// <summary>Okunan ve yazılan her şeyi kaydeder; paylaşımlı bölge üyeliğini onay geri çağrısına verir.</summary>
    private sealed class IzleyenDns : IDnsService
    {
        public List<string> OkunanSiteler { get; } = [];
        public List<UpsertDnsRecordRequest> Yazilanlar { get; } = [];
        public List<string> Silinenler { get; } = [];
        public string[] PaylasimUyeleri { get; set; } = [];

        private static SiteDnsZoneDto Bolge(string site) => new()
        {
            SiteName = site, ZoneName = "ornek.com", NameServers = ["ns1.ornek.com"], Records = []
        };

        public Task<SiteDnsZoneDto> GetZoneAsync(string siteName, CancellationToken cancellationToken = default)
        { OkunanSiteler.Add(siteName); return Task.FromResult(Bolge(siteName)); }

        public async Task<SiteDnsZoneDto> UpsertRecordAsync(string siteName, UpsertDnsRecordRequest request,
            Func<IReadOnlyList<string>, CancellationToken, Task> approval, CancellationToken cancellationToken)
        {
            await approval(PaylasimUyeleri, cancellationToken);
            Yazilanlar.Add(request);
            return Bolge(siteName);
        }

        public async Task<SiteDnsZoneDto> DeleteRecordAsync(string siteName, string recordId,
            Func<IReadOnlyList<string>, CancellationToken, Task> approval, CancellationToken cancellationToken)
        {
            await approval(PaylasimUyeleri, cancellationToken);
            Silinenler.Add(recordId);
            return Bolge(siteName);
        }

        public Task<SiteDnsZoneDto> UpsertRecordAsync(string siteName, UpsertDnsRecordRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException("onaysiz yazma YASAK");
        public Task<SiteDnsZoneDto> DeleteRecordAsync(string siteName, string recordId, CancellationToken cancellationToken = default) => throw new NotSupportedException("onaysiz silme YASAK");
        public Task<SiteDnsZoneDto> UpsertRecordsAsync(string siteName, IReadOnlyList<UpsertDnsRecordRequest> requests, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDnsZoneDto> InstallWindowsDnsAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDnsZoneDto> UpdateZoneAsync(string siteName, UpdateDnsZoneRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDnsZoneDto> ResetDefaultsAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDnsZoneDto> SetZoneModeAsync(string siteName, SetDnsZoneModeRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDnsZoneDto> TransferZoneNowAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> RepairDefaultsAsync(string siteName, bool applyChanges = true, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<string>?> GetPublishedRecordKeysAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SubdomainRecordResult> EnsureSubdomainRecordAsync(string parentSiteName, string label, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteZoneAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task EnsureZonePublishedAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DnsServerHealthDto> GetServerHealthAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DnsServerHealthDto> RepairServerAsync(bool applyDelayedAutoStart, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DnsServerHealthDto?> RunBindWatchdogAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DnsTemplateDto> GetTemplateAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DnsTemplateDto> UpdateTemplateAsync(UpdateDnsTemplateRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDnsZoneDto> SetDnssecAsync(string siteName, bool enabled, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class PlansizServis : IServicePlanService
    {
        public Task<ServicePlanDto?> GetPlanAsync(string id, CancellationToken cancellationToken = default) => Task.FromResult<ServicePlanDto?>(null);
        public Task<IReadOnlyCollection<ServicePlanDto>> GetPlansAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyCollection<ServicePlanDto>>([]);
        public Task<ServicePlanDto> CreatePlanAsync(ServicePlanRequest request, Guid? ownerId = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ServicePlanDto> UpdatePlanAsync(string id, ServicePlanRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeletePlanAsync(string id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class TavansizServis : IResellerQuotaService
    {
        public Task<ResellerQuotaDto?> GetQuotaAsync(Guid resellerId, CancellationToken cancellationToken = default) => Task.FromResult<ResellerQuotaDto?>(null);
        public Task<ResellerQuotaDto> SetQuotaAsync(Guid resellerId, ResellerQuotaRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RemoveQuotaAsync(Guid resellerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
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

internal static class DnsDenetleyiciTestYardimcilari
{
    /// <summary>Geçerli (özet eşleşen) bir içe aktarım uygular: kapsam onayı dışında hiçbir şey sınanmıyor.</summary>
    public static async Task<ActionResult<AdaPanel.Core.Dtos.Common.ApiResponse<DnsZoneImportResultDto>>> ImportApplyAsyncBasit(
        this DnsController denetleyici, string site, IDnsService dns)
    {
        const string metin = "yeni IN A 203.0.113.9";
        var bolge = await dns.GetZoneAsync(site);
        return await denetleyici.ImportApplyAsync(site, new DnsZoneImportApplyRequest
        {
            ZoneText = metin,
            PreviewHash = AdaPanel.Infrastructure.Dns.DnsZoneImport.ComputeHash(bolge.ZoneName, metin)
        }, CancellationToken.None);
    }
}
