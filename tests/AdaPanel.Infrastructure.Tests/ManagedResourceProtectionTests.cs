using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Sites;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Panelin KENDI altyapisini ve izin sozlesmelerini koruyan sinirlar.
/// </summary>
public sealed class ManagedResourceProtectionTests
{
    /// <summary>
    /// Site olusturma, ayni adli IIS sitesini onay sormadan kaldirir (kaldirilmis bir kurulumdan
    /// kalan artik siteyi devralmak icin -- bu davranis bilinclidir). Ama panelin araclari da
    /// birer IIS sitesidir: kullanici o adlardan birini yazdiginda calisan phpMyAdmin/Adminer/
    /// webmail ve yonetilen uygulama havuzu SESSIZCE yok oluyordu.
    /// </summary>
    [Fact]
    public void ManagedSiteNames_AreClosedToUserRequests()
    {
        var kaynak = OkuKaynak("src", "AdaPanel.Infrastructure", "Iis", "WebAdministrationSiteService.cs");

        Assert.Contains("EnsureSiteNameIsNotReserved", kaynak, StringComparison.Ordinal);
        Assert.Contains("sites.reserved_name", kaynak, StringComparison.Ordinal);
        Assert.Contains("Default Web Site", kaynak, StringComparison.Ordinal);

        // Kontrol, var olan siteyi KALDIRAN cagridan ONCE gelmeli.
        var kontrol = kaynak.IndexOf("EnsureSiteNameIsNotReserved(normalizedName", StringComparison.Ordinal);
        var kaldirma = kaynak.IndexOf("InvokeMethod(sites, \"Remove\", existingSite)", StringComparison.Ordinal);

        Assert.True(kontrol > 0, "Rezerve ad kontrolu yok.");
        Assert.True(kaldirma > kontrol, "Var olan site, ad kontrolunden ONCE kaldiriliyor.");
    }

    /// <summary>
    /// Bayrak KULLANICIDAN GELEMEZ: JSON'a kapali olmasa, istek govdesine eklenerek koruma
    /// devre disi birakilabilirdi.
    /// </summary>
    [Fact]
    public void TheInfrastructureFlag_CannotBeSetFromTheRequestBody()
    {
        var ozellik = typeof(CreateSiteRequest).GetProperty(nameof(CreateSiteRequest.IsManagedInfrastructure));

        Assert.NotNull(ozellik);
        Assert.Contains(
            ozellik!.GetCustomAttributes(inherit: false),
            oznitelik => oznitelik.GetType() == typeof(System.Text.Json.Serialization.JsonIgnoreAttribute));
    }

    /// <summary>Panelin kendi araclari bu bayragi GECMELI, yoksa kendi sitelerini kuramaz.</summary>
    [Fact]
    public void ThePanelsOwnToolSites_PassTheFlag()
    {
        Assert.Contains(
            "IsManagedInfrastructure = true",
            OkuKaynak("src", "AdaPanel.Infrastructure", "Mail", "WebmailService.cs"),
            StringComparison.Ordinal);

        Assert.Contains(
            "IsManagedInfrastructure = true",
            OkuKaynak("src", "AdaPanel.Infrastructure", "WebDbClient", "WebDbClientService.cs"),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// KLASOR IZNI HAVUZU TAKIP ETMELI: izolasyon sitenin havuzunu degistirir, ama izin ESKI
    /// kimlige yazilidir. Miras da kirik oldugu icin yedek erisim kalmaz -> site 401.3/500.
    /// </summary>
    [Fact]
    public void IsolatingAnAppPool_ReappliesTheFolderPermission()
    {
        var kaynak = OkuKaynak("src", "AdaPanel.Infrastructure", "Waf", "WafService.cs");

        var izolasyon = kaynak.IndexOf("public async Task<SiteSecurityConfigurationDto> IsolateAppPoolAsync", StringComparison.Ordinal);
        Assert.True(izolasyon > 0, "IsolateAppPoolAsync bulunamadi.");

        var govde = kaynak[izolasyon..];
        var sonrakiMetot = govde.IndexOf("public async Task<SiteSecurityConfigurationDto> ApplyFolderAclAsync", StringComparison.Ordinal);
        govde = sonrakiMetot > 0 ? govde[..sonrakiMetot] : govde;

        Assert.Contains("durum.FolderAclApplied", govde, StringComparison.Ordinal);
        Assert.Contains("ApplyFolderAclAsync(", govde, StringComparison.Ordinal);
    }

    /// <summary>
    /// Izin daha once uygulanmadiysa izolasyon onu KENDILIGINDEN getirmemeli: kullanicinin
    /// istemedigi bir kisitlama, bir guvenlik kazanci degil surprizdir.
    /// </summary>
    [Fact]
    public void IsolationDoesNotIntroduceAPermissionTheUserNeverAskedFor()
    {
        var kaynak = OkuKaynak("src", "AdaPanel.Infrastructure", "Waf", "WafService.cs");

        Assert.Contains("if (durum.FolderAclApplied)", kaynak, StringComparison.Ordinal);
    }

    /// <summary>
    /// icacls'e giden degerlerin dogrulamasi TEK KAYNAKTAN gelmeli. Ayni kural iki serviste
    /// kopyalanmisti; kopyalanmis bir kural, birini sikilastirip digerini unutmanin yoludur.
    /// </summary>
    [Fact]
    public void AclInputValidation_HasASingleSource()
    {
        var dosya = OkuKaynak("src", "AdaPanel.Infrastructure", "Files", "FileService.cs");
        var waf = OkuKaynak("src", "AdaPanel.Infrastructure", "Waf", "WafService.cs");

        Assert.Contains("AclInputValidator.EnsurePrincipal", dosya, StringComparison.Ordinal);
        Assert.Contains("AclInputValidator.EnsureRights", dosya, StringComparison.Ordinal);
        Assert.Contains("AclInputValidator.EnsureRights", waf, StringComparison.Ordinal);

        // Kopyalanmis beyaz liste geri gelmemeli.
        Assert.DoesNotContain("AllowedAclRights", dosya, StringComparison.Ordinal);
    }

    /// <summary>Gecerli degerler kabul, gecersizler REDDEDILIR.</summary>
    [Theory]
    [InlineData("IIS_IUSRS", "M")]
    [InlineData("SUNUCU\\kullanici", "R")]
    [InlineData("IIS AppPool\\AdaPanel.site", "F")]
    public void ValidAclInput_IsAccepted(string principal, string rights)
    {
        Assert.Equal(principal, AclInputValidator_EnsurePrincipal(principal));
        Assert.Equal(rights.ToUpperInvariant(), AclInputValidator_EnsureRights(rights));
    }

    [Theory]
    [InlineData("kullanici\" /inheritance:r \"x")]   // argumandan cikma
    [InlineData("kullanici; Remove-Item C:\\")]      // komut zinciri
    [InlineData("")]
    public void InvalidPrincipals_AreRejected(string principal)
    {
        Assert.Throws<AppException>(() => AclInputValidator_EnsurePrincipal(principal));
    }

    [Theory]
    [InlineData("(OI)(CI)F")]   // birlesik bicim: arayuz sunmuyor
    [InlineData("DE,WD,AD")]
    [InlineData("")]
    public void InvalidRights_AreRejected(string rights)
    {
        Assert.Throws<AppException>(() => AclInputValidator_EnsureRights(rights));
    }

    // AclInputValidator internal; testler InternalsVisibleTo ile erisir.
    private static string AclInputValidator_EnsurePrincipal(string principal)
        => AdaPanel.Infrastructure.Support.AclInputValidator.EnsurePrincipal(principal);

    private static string AclInputValidator_EnsureRights(string rights)
        => AdaPanel.Infrastructure.Support.AclInputValidator.EnsureRights(rights);

    private static string OkuKaynak(params string[] parts)
    {
        var klasor = AppContext.BaseDirectory;
        for (var derinlik = 0; derinlik < 8; derinlik++)
        {
            var aday = Path.Combine(new[] { klasor }.Concat(parts).ToArray());
            if (File.Exists(aday))
            {
                return File.ReadAllText(aday);
            }

            klasor = Path.Combine(klasor, "..");
        }

        throw new FileNotFoundException(string.Join('/', parts));
    }
}
