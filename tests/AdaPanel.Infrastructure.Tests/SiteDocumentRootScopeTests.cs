using AdaPanel.Api.Security;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Belge kökünün kiracı sınırı.
/// </summary>
/// <remarks>
/// Düzeltilen davranış (kullanıcı bildirimi): sınır SİTE idi, KİRACI olmalıydı. Alt alan adı ayrı
/// bir sitedir; en olağan kullanım "webtv.ornek.com" alt alan adını ana sitedeki
/// <c>httpdocs/iptv/public</c> klasörüne yöneltmektir ve site sınırı bunu 403 ile reddediyordu.
/// Koruma, kiracıyı başkasının klasöründen korurken KENDİ klasöründen de koruyordu.
/// </remarks>
public sealed class SiteDocumentRootScopeTests
{
    private const string AnaSite = @"C:\inetpub\wwwroot\ornek.com";
    private const string AltAlan = @"C:\inetpub\wwwroot\webtv.ornek.com";
    private const string Yabanci = @"C:\inetpub\wwwroot\baska-musteri.com";

    /// <summary>
    /// ASIL DÜZELTME: alt alan adı, kiracının ANA sitesindeki mevcut klasörü gösterebilmeli.
    /// </summary>
    [Fact]
    public void Alt_alan_adi_ayni_kiracinin_baska_sitesindeki_klasoru_gosterebilir()
    {
        var izinli = SiteDocumentRootScope.IsAllowed(
            istenenYol: Path.Combine(AnaSite, "httpdocs", "iptv", "public"),
            duzenlenenSiteKoku: AltAlan,
            kiracininSiteKokleri: [AnaSite, AltAlan]);

        Assert.True(izinli);
    }

    /// <summary>
    /// ÇAPRAZ KİRACI KORUMASI DURUYOR — düzeltmenin kaldırmadığı şey bu.
    /// </summary>
    [Fact]
    public void Baska_bir_kiracinin_klasoru_HALA_reddedilir()
    {
        var izinli = SiteDocumentRootScope.IsAllowed(
            istenenYol: Path.Combine(Yabanci, "httpdocs"),
            duzenlenenSiteKoku: AltAlan,
            kiracininSiteKokleri: [AnaSite, AltAlan]);

        Assert.False(izinli);
    }

    [Fact]
    public void Sistem_yolu_reddedilir()
    {
        Assert.False(SiteDocumentRootScope.IsAllowed(@"C:\Windows\System32", AltAlan, [AnaSite, AltAlan]));
        Assert.False(SiteDocumentRootScope.IsAllowed(@"C:\", AltAlan, [AnaSite, AltAlan]));
    }

    /// <summary>
    /// Sitenin KENDİ kökü her zaman geçerli: "public"ten proje köküne dönmek en olağan işlemdir.
    /// </summary>
    [Theory]
    [InlineData(AltAlan)]
    [InlineData(AltAlan + @"\public")]
    public void Sitenin_kendi_agaci_her_zaman_gecerli(string yol)
    {
        Assert.True(SiteDocumentRootScope.IsAllowed(yol, AltAlan, kiracininSiteKokleri: null));
    }

    /// <summary>
    /// KAPSAMSIZ ROL (operator) İÇİN DAVRANIŞ DEĞİŞMEDİ.
    /// </summary>
    /// <remarks>
    /// Düzeltmenin amacı kiracıya alan açmaktı; kapsamsız role yeni yetki vermek değil. Küme
    /// <c>null</c> geldiğinde yalnız düzenlenen sitenin kökü geçerlidir — yani eski kural.
    /// Bu nöbetçi olmasa, "null = sınırsız" gibi bir sadeleştirme sessizce yetki genişletirdi.
    /// </remarks>
    [Fact]
    public void Kapsamsiz_rolde_yalniz_duzenlenen_sitenin_koku_gecerli()
    {
        Assert.False(SiteDocumentRootScope.IsAllowed(
            istenenYol: Path.Combine(AnaSite, "httpdocs"),
            duzenlenenSiteKoku: AltAlan,
            kiracininSiteKokleri: null));
    }

    [Fact]
    public void Bos_girdiler_guvenli_tarafta_reddedilir()
    {
        Assert.False(SiteDocumentRootScope.IsAllowed(null, AltAlan, [AnaSite]));
        Assert.False(SiteDocumentRootScope.IsAllowed("   ", AltAlan, [AnaSite]));

        // Kök listesi tamamen boşsa hiçbir yol kabul edilmez. Aksi hâlde "kök okunamadı" durumu
        // sessizce "her yere izin var"a dönüşürdü -- ölçülemeyen şey serbest sayılmamalı.
        Assert.False(SiteDocumentRootScope.IsAllowed(AnaSite, duzenlenenSiteKoku: null, kiracininSiteKokleri: null));
        Assert.False(SiteDocumentRootScope.IsAllowed(AnaSite, duzenlenenSiteKoku: "  ", kiracininSiteKokleri: ["", "   "]));
    }

    /// <summary>
    /// Denetleyici GERÇEKTEN bu kapıdan geçiyor mu.
    /// </summary>
    /// <remarks>
    /// Bu depoda "koruma yazılmış ama o yolda uygulanmıyor" tekrar eden hata sınıfıdır; bu
    /// oturumda PHP izolasyonu tam bu şekilde ölü bulundu (kural vardı, varsayılan yol onu hiç
    /// çağırmıyordu).
    /// </remarks>
    [Fact]
    public void Denetleyici_kapiyi_cagiriyor()
    {
        var kaynak = File.ReadAllText(Path.Combine(
            KaynakKoku(), "src", "AdaPanel.Api", "Controllers", "SitesController.cs"));

        Assert.Contains("SiteDocumentRootScope.IsAllowed(", kaynak, StringComparison.Ordinal);
        Assert.Contains("sites.document_root_outside_site", kaynak, StringComparison.Ordinal);
    }

    private static string KaynakKoku()
    {
        var yol = AppContext.BaseDirectory;
        while (yol is not null && !File.Exists(Path.Combine(yol, "AdaPanel.sln")))
        {
            yol = Path.GetDirectoryName(yol);
        }

        Assert.NotNull(yol);
        return yol!;
    }
}
