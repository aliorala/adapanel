using System.Text.Json;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Cluster;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Cluster;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// UZAK DUGUM BELIRTECI: duz metin DURMAZ ve panele GERI DONMEZ.
/// <para>
/// Bu belirtec, uzak sunucuda yonetici yetkisi demektir. Panel yapilandirma dosyalari yedeklere
/// girer ve yedek, panelden cok daha fazla elden ele dolasir; sirrin orada duz durmasi, tek bir
/// yedek kopyasiyla butun kume sunucularinin ele gecmesi demektir. Yanitta donmesi ise sirri
/// tarayici gecmisine, ekran goruntusune ve hata ayiklama gunlugune tasir.
/// </para>
/// </summary>
public sealed class ClusterNodeSecretTests : IDisposable
{
    private const string Belirtec = "adap_cok_gizli_dugum_anahtari_42";

    private readonly string veriKoku = Path.Combine(
        Path.GetTempPath(),
        "adapanel-cluster-sir-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Belirtec_diskte_SIFRELENIR()
    {
        var (servis, _) = Duzenek();
        await KaydetAsync(servis);

        var dosya = Path.Combine(veriKoku, "config", "cluster", "nodes.json");
        var icerik = await File.ReadAllTextAsync(dosya);

        Assert.DoesNotContain(Belirtec, icerik, StringComparison.Ordinal);
        // Alan adi degisse bile bu kontrol tutar: korunan sey bicim degil, sirrin KENDISI.
        Assert.Contains("tokenProtected", icerik, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Belirtec_LISTE_yanitinda_DONMEZ()
    {
        var (servis, _) = Duzenek();
        await KaydetAsync(servis);

        var dugumler = await servis.GetNodesAsync();
        var json = JsonSerializer.Serialize(dugumler);

        Assert.DoesNotContain(Belirtec, json, StringComparison.Ordinal);
        // Arayuzun bilmesi gereken tek sey: anahtar VAR mi.
        Assert.True(dugumler[0].HasToken);
    }

    /// <summary>
    /// Disari cikan tip belirtec alani TASIMAZ. Bu yapisal bir engeldir: tek tip kullanilsaydi bir
    /// gun birinin <c>Ok(record)</c> yazmasi sizinti icin yeterli olurdu.
    /// </summary>
    [Fact]
    public void Disa_acilan_tipte_belirtec_ALANI_YOK()
    {
        var alanlar = typeof(ClusterNodeDto).GetProperties().Select(ozellik => ozellik.Name).ToArray();

        Assert.DoesNotContain(alanlar, ad => ad.Contains("Token", StringComparison.OrdinalIgnoreCase) && ad != "HasToken");
        Assert.DoesNotContain(alanlar, ad => ad.Contains("Secret", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Belirtec_yazilmadan_guncelleme_MEVCUDU_KORUR()
    {
        var (servis, depo) = Duzenek();
        var dugum = await KaydetAsync(servis);

        await servis.UpdateAsync(dugum.Id, new UpdateClusterNodeRequest
        {
            Label = "Frankfurt-2",
            Address = "https://dugum.ornek.test:9443",
            ApiToken = string.Empty,
            Enabled = true
        });

        var kayit = await depo.GetAsync(dugum.Id);
        Assert.Equal(Belirtec, depo.ResolveToken(kayit!));
        Assert.Equal("https://dugum.ornek.test:9443", kayit!.Address);
    }

    [Fact]
    public async Task Belirtec_yazilirsa_DEGISIR()
    {
        var (servis, depo) = Duzenek();
        var dugum = await KaydetAsync(servis);

        await servis.UpdateAsync(dugum.Id, new UpdateClusterNodeRequest
        {
            Label = "Frankfurt",
            Address = "https://dugum.ornek.test",
            ApiToken = "adap_yeni_anahtar",
            Enabled = true
        });

        var kayit = await depo.GetAsync(dugum.Id);
        Assert.Equal("adap_yeni_anahtar", depo.ResolveToken(kayit!));
    }

    /// <summary>
    /// DPAPI korumasi MAKINEYE baglidir: yapilandirma baska bir sunucuya kopyalanirsa cozme
    /// basarisiz olur. Cokmek yerine bos donmek dogrudur — yonetici anahtari yeniden girer.
    /// </summary>
    [Fact]
    public async Task Cozulemeyen_belirtec_COKERTMEZ_bos_doner()
    {
        var dosyaDeposu = new JsonFileStore(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = veriKoku }));
        var depo = new ClusterNodeStore(dosyaDeposu, new PatlayanKoruyucu(), NullLogger<ClusterNodeStore>.Instance);

        var kayit = await depo.AddAsync(new ClusterNodeRecord { Label = "x", Address = "https://x.test" }, Belirtec);

        Assert.Equal(string.Empty, depo.ResolveToken(kayit));
    }

    [Fact]
    public async Task Belirtecsiz_kayit_REDDEDILIR()
    {
        var (servis, _) = Duzenek();

        var hata = await Assert.ThrowsAsync<AppException>(
            () => servis.RegisterAsync(new RegisterClusterNodeRequest
            {
                Label = "Frankfurt",
                Address = "https://dugum.ornek.test",
                ApiToken = "   "
            }));

        Assert.Equal(ClusterMessages.TokenRequired, hata.MessageKey);
    }

    /// <summary>
    /// Ayni adres iki kez kaydedilemez. Yalniz duzen kaygisi degil: ayni sunucu iki kez kayitliysa
    /// site sayisi iki kez toplanir ve yerlesim karari, aslinda dolu olan bir dugumu bos sanabilir.
    /// </summary>
    [Fact]
    public async Task Ayni_adres_IKI_KEZ_kaydedilemez()
    {
        var (servis, _) = Duzenek();
        await KaydetAsync(servis);

        var hata = await Assert.ThrowsAsync<AppException>(
            () => servis.RegisterAsync(new RegisterClusterNodeRequest
            {
                Label = "Baska-Etiket",
                Address = "https://DUGUM.ornek.test/",
                ApiToken = "adap_baska"
            }));

        Assert.Equal(ClusterMessages.DuplicateAddress, hata.MessageKey);
    }

    /// <summary>
    /// TLS DOGRULAMASI VARSAYILAN OLARAK ACIK. Istek nesnesinin varsayilani kapali olsaydi,
    /// alani hic gondermeyen bir istemci sessizce dogrulamasiz bir dugum kaydederdi.
    /// </summary>
    [Fact]
    public async Task TLS_dogrulamasi_VARSAYILAN_ACIK()
    {
        Assert.True(new RegisterClusterNodeRequest().VerifyTlsCertificate);
        Assert.True(new UpdateClusterNodeRequest().VerifyTlsCertificate);
        Assert.True(new ClusterNodeDto().VerifyTlsCertificate);

        var (servis, depo) = Duzenek();
        // Istek TLS alanini HIC belirtmiyor.
        var dugum = await servis.RegisterAsync(new RegisterClusterNodeRequest
        {
            Label = "Frankfurt",
            Address = "https://dugum.ornek.test",
            ApiToken = Belirtec
        });

        Assert.True(dugum.VerifyTlsCertificate);
        Assert.True((await depo.GetAsync(dugum.Id))!.VerifyTlsCertificate);
    }

    [Fact]
    public async Task Kisa_etiket_REDDEDILIR()
    {
        var (servis, _) = Duzenek();

        var hata = await Assert.ThrowsAsync<AppException>(
            () => servis.RegisterAsync(new RegisterClusterNodeRequest
            {
                Label = "x",
                Address = "https://dugum.ornek.test",
                ApiToken = Belirtec
            }));

        Assert.Equal(ClusterMessages.InvalidLabel, hata.MessageKey);
    }

    /// <summary>
    /// SSRF kapisi KAYIT ANINDA da isliyor: servis, adres denetimini atlamamali.
    /// </summary>
    [Theory]
    [InlineData("https://localhost:8888")]
    [InlineData("https://169.254.169.254")]
    [InlineData("http://10.0.0.5")]
    public async Task Ic_adres_KAYDEDILEMEZ(string adres)
    {
        var (servis, _) = Duzenek();

        var hata = await Assert.ThrowsAsync<AppException>(
            () => servis.RegisterAsync(new RegisterClusterNodeRequest
            {
                Label = "Tuzak",
                Address = adres,
                ApiToken = Belirtec
            }));

        Assert.Equal(ClusterMessages.AddressForbidden, hata.MessageKey);
        Assert.Equal(403, hata.HttpStatusCode);
    }

    /// <summary>
    /// DNS ile ic aga cozulen ad da KAYDEDILEMEZ (ikinci katman servis uzerinden bagli mi).
    /// </summary>
    [Fact]
    public async Task DNS_ile_ic_aga_cozulen_ad_KAYDEDILEMEZ()
    {
        var dosyaDeposu = new JsonFileStore(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = veriKoku }));
        var depo = new ClusterNodeStore(dosyaDeposu, new SahteKoruyucu(), NullLogger<ClusterNodeStore>.Instance);
        var servis = new ClusterService(depo, new BosIstemci(), NullLogger<ClusterService>.Instance)
        {
            Resolver = (_, _) => Task.FromResult(new[] { System.Net.IPAddress.Parse("10.0.0.5") })
        };

        var hata = await Assert.ThrowsAsync<AppException>(
            () => servis.RegisterAsync(new RegisterClusterNodeRequest
            {
                Label = "Gizli-Tuzak",
                Address = "https://masum-gorunumlu.ornek.com",
                ApiToken = Belirtec
            }));

        Assert.Equal(ClusterMessages.AddressForbidden, hata.MessageKey);
    }

    // ---------------------------------------------------------------------------------------

    private (ClusterService Servis, ClusterNodeStore Depo) Duzenek()
    {
        var dosyaDeposu = new JsonFileStore(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = veriKoku }));
        var depo = new ClusterNodeStore(dosyaDeposu, new SahteKoruyucu(), NullLogger<ClusterNodeStore>.Instance);
        var servis = new ClusterService(depo, new BosIstemci(), NullLogger<ClusterService>.Instance)
        {
            Resolver = (_, _) => Task.FromResult(new[] { System.Net.IPAddress.Parse("93.184.216.34") })
        };

        return (servis, depo);
    }

    private static Task<ClusterNodeDto> KaydetAsync(ClusterService servis) =>
        servis.RegisterAsync(new RegisterClusterNodeRequest
        {
            Label = "Frankfurt",
            Address = "https://dugum.ornek.test",
            ApiToken = Belirtec
        });

    private sealed class SahteKoruyucu : ISecretProtector
    {
        public string Protect(string plaintext) =>
            Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("korunmus:" + plaintext));

        public string Unprotect(string protectedValue) =>
            System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(protectedValue))["korunmus:".Length..];
    }

    /// <summary>Baska makineye tasinmis yapilandirmayi canlandirir: cozme daima patlar.</summary>
    private sealed class PatlayanKoruyucu : ISecretProtector
    {
        public string Protect(string plaintext) => "BOZUK";

        public string Unprotect(string protectedValue) => throw new InvalidOperationException("baska makine");
    }

    private sealed class BosIstemci : IClusterNodeClient
    {
        public Task<ClusterCallResult<ClusterNodeInfoDto>> FetchInfoAsync(
            ClusterNodeRecord node,
            string token,
            CancellationToken cancellationToken) =>
            Task.FromResult(new ClusterCallResult<ClusterNodeInfoDto>(null, ClusterMessages.NodeUnreachable, "test", 0));

        public Task<ClusterCallResult<JsonElement>> PostAsync(
            ClusterNodeRecord node,
            string token,
            string relativePath,
            object payload,
            CancellationToken cancellationToken) =>
            Task.FromResult(new ClusterCallResult<JsonElement>(default, ClusterMessages.NodeUnreachable, "test", 0));
    }

    public void Dispose()
    {
        if (Directory.Exists(veriKoku))
        {
            Directory.Delete(veriKoku, recursive: true);
        }
    }
}
