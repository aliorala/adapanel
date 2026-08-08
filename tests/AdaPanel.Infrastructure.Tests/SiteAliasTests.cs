using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Infrastructure.Iis;
using AdaPanel.Infrastructure.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Alan adı takma adları: aynı siteyi ikinci bir alan adından yayınlama.
/// </summary>
/// <remarks>
/// Panelde bu kavram hiç yoktu; kullanıcı <c>.com</c> ve <c>.com.tr</c> için iki ayrı site açıp
/// iki kopyayı ayrı ayrı güncellemeye çalışıyordu. Testler asıl tuzakları kilitliyor: sitenin
/// KENDİ adını takma ad yapmak (sonsuz yönlendirme), <c>www</c> çiftinin unutulması ve kaldırırken
/// IIS bağlamasının geride kalması.
/// </remarks>
public sealed class SiteAliasTests : IDisposable
{
    private readonly string dataRoot = Path.Combine(Path.GetTempPath(), $"ada-alias-{Guid.NewGuid():N}");
    private readonly string siteKoku;
    private readonly SahteSiteServisi siteServisi;
    private readonly SiteAliasService servis;

    public SiteAliasTests()
    {
        siteKoku = Path.Combine(dataRoot, "site");
        Directory.CreateDirectory(siteKoku);

        siteServisi = new SahteSiteServisi(new SiteDto
        {
            Name = "ornek",
            PhysicalPath = siteKoku,
            Bindings = [new SiteBindingDto { Protocol = "http", HostName = "ornek.com", IpAddress = "*", Port = 80 }]
        });

        servis = new SiteAliasService(
            siteServisi,
            new JsonFileStore(Microsoft.Extensions.Options.Options.Create(
                new AdaPanel.Infrastructure.Options.PanelRuntimeOptions { DataRoot = dataRoot })),
            NullLogger<SiteAliasService>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(dataRoot, recursive: true); } catch { /* en iyi çaba */ }
    }

    [Fact]
    public async Task Takma_ad_eklenince_www_cifti_de_baglanir()
    {
        // Kullanıcı "ornek.com.tr" yazınca "www.ornek.com.tr" de bekler; ikisini ayrı ayrı
        // eklettirmek, birini unutup sitenin www'de açılmamasının yolu.
        var sonuc = await servis.AddAliasAsync("ornek", new AddSiteAliasRequest { HostName = "ornek.com.tr" });

        Assert.Contains("ornek.com.tr", siteServisi.EklenenHostlar);
        Assert.Contains("www.ornek.com.tr", siteServisi.EklenenHostlar);

        var alias = Assert.Single(sonuc.Aliases);
        Assert.Equal("ornek.com.tr", alias.HostName);
        Assert.True(alias.IncludesWww);
    }

    [Fact]
    public async Task Sitenin_KENDI_alan_adi_takma_ad_yapilamaz()
    {
        // Yönlendirme açıkken bu, siteyi kendine sonsuz yönlendirirdi.
        var hata = await Assert.ThrowsAsync<AppException>(
            () => servis.AddAliasAsync("ornek", new AddSiteAliasRequest { HostName = "ORNEK.COM" }));

        Assert.Equal("alias.same_as_primary", hata.MessageKey);
    }

    [Fact]
    public async Task Bos_alan_adi_reddedilir()
    {
        var hata = await Assert.ThrowsAsync<AppException>(
            () => servis.AddAliasAsync("ornek", new AddSiteAliasRequest { HostName = "   " }));

        Assert.Equal("alias.host_required", hata.MessageKey);
    }

    [Fact]
    public async Task Yonlendirme_kurali_web_config_e_yazilir_ve_ana_alan_adini_hedefler()
    {
        await servis.AddAliasAsync("ornek", new AddSiteAliasRequest { HostName = "ornek.com.tr", RedirectToPrimary = true });

        var webConfig = await File.ReadAllTextAsync(Path.Combine(siteKoku, "web.config"));
        Assert.Contains("AdaPanel.AliasRedirect", webConfig);
        Assert.Contains("ornek\\.com\\.tr", webConfig);
        Assert.Contains("https://ornek.com/", webConfig);
    }

    [Fact]
    public async Task Yonlendirme_KAPALIYSA_kural_yazilmaz()
    {
        // "Aynı içerik" modunda yönlendirme olmamalı; kural yazmak sessizce davranışı değiştirirdi.
        await servis.AddAliasAsync("ornek", new AddSiteAliasRequest { HostName = "ornek.com.tr", RedirectToPrimary = false });

        var webConfigYolu = Path.Combine(siteKoku, "web.config");
        var icerik = File.Exists(webConfigYolu) ? await File.ReadAllTextAsync(webConfigYolu) : string.Empty;
        Assert.DoesNotContain("AdaPanel.AliasRedirect", icerik);
    }

    [Fact]
    public async Task Kaldirinca_BAGLAMALAR_da_gider()
    {
        // Yalnız kaydı silmek, alan adını IIS'te bağlı bırakırdı: site o adresten yayınlanmaya
        // devam eder ama panelde görünmez; başka bir siteye bağlamak istendiğinde de
        // "bu ad kullanımda" hatası verirdi.
        await servis.AddAliasAsync("ornek", new AddSiteAliasRequest { HostName = "ornek.com.tr" });

        var sonuc = await servis.RemoveAliasAsync("ornek", "ornek.com.tr");

        Assert.Empty(sonuc.Aliases);
        Assert.Contains("ornek.com.tr", siteServisi.KaldirilanHostlar);
        Assert.Contains("www.ornek.com.tr", siteServisi.KaldirilanHostlar);
    }

    [Fact]
    public async Task Ayni_takma_ad_iki_kez_eklenince_cogalmaz()
    {
        await servis.AddAliasAsync("ornek", new AddSiteAliasRequest { HostName = "ornek.com.tr" });
        var sonuc = await servis.AddAliasAsync("ornek", new AddSiteAliasRequest { HostName = "ornek.com.tr" });

        Assert.Single(sonuc.Aliases);
    }

    // --- düzenek ---

    /// <summary>Bağlama ekleme/kaldırma çağrılarını KAYDEDER ve site durumunu günceller.</summary>
    private sealed class SahteSiteServisi(SiteDto site) : Core.Interfaces.IIisSiteService
    {
        private readonly List<SiteBindingDto> baglamalar = [.. site.Bindings];

        public List<string> EklenenHostlar { get; } = [];
        public List<string> KaldirilanHostlar { get; } = [];

        public Task<SiteDto> GetSiteAsync(string siteName, CancellationToken cancellationToken = default)
            => Task.FromResult(new SiteDto
            {
                Name = site.Name,
                PhysicalPath = site.PhysicalPath,
                Bindings = [.. baglamalar]
            });

        public Task<SiteDto> AddBindingAsync(string siteName, SiteBindingRequest request, CancellationToken cancellationToken = default)
        {
            EklenenHostlar.Add(request.HostName);
            baglamalar.Add(new SiteBindingDto
            {
                Protocol = request.Protocol,
                HostName = request.HostName,
                IpAddress = request.IpAddress,
                Port = request.Port
            });
            return GetSiteAsync(siteName, cancellationToken);
        }

        public Task<SiteDto> RemoveBindingAsync(string siteName, RemoveSiteBindingRequest request, CancellationToken cancellationToken = default)
        {
            KaldirilanHostlar.Add(request.HostName);
            baglamalar.RemoveAll(item =>
                string.Equals(item.HostName, request.HostName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.Protocol, request.Protocol, StringComparison.OrdinalIgnoreCase));
            return GetSiteAsync(siteName, cancellationToken);
        }

        public Task<IReadOnlyCollection<SiteDto>> GetSitesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDto> CreateSiteAsync(CreateSiteRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDto> UpdatePhysicalPathAsync(string siteName, string physicalPath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteSiteAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDto> StartSiteAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDto> StopSiteAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<SiteDto>> ImportExistingSitesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
