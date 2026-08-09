using System.Reflection;
using AdaPanel.Api.Controllers;
using AdaPanel.Api.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// SATILAN OZELLIGI MUSTERI KULLANABILIYOR MU?
/// <para>
/// Bulunan hata: kodu tamamen yazilmis bes ozellik <c>[Authorize(Roles = "Administrator,Reseller")]</c>
/// ile abonelik sahibine KAPALIYDI -- kendi domainine SSL alamiyor, WordPress Toolkit'i (urunun
/// amiral gemisi musteri ozelligi) kullanamiyor, trafigini goremiyor (ama trafik kotasindan askiya
/// aliniyordu), "sitem neden acilmiyor"u tesihs edemiyor, disk kullanimini goremiyordu.
/// </para>
/// <para>
/// <b>Rol eklemek TEK BASINA yeterli degil.</b> Bu testler iki seyi birden korur: (1) rol geri
/// alinmasin, (2) musteriye acilan her uc kaynagin CAGIRANA ait oldugunu dogrulasin. Ikincisi
/// olmadan rol eklemek, capraz-kiraci acigi acmak olurdu -- ornegin DiskQuotaController'da hicbir
/// kapsam denetimi YOKTU ve site adi dogrudan yoldan geliyordu.
/// </para>
/// </summary>
public sealed class CustomerFeatureAccessTests
{
    /// <summary>Musteriye acilmasi GEREKEN controller'lar.</summary>
    public static TheoryData<Type> MusteriyeAcik =>
    [
        typeof(SslController),
        typeof(WordPressController),
        typeof(StatisticsController),
        typeof(SiteHealthController),
        typeof(DiskQuotaController)
    ];

    [Theory]
    [MemberData(nameof(MusteriyeAcik))]
    public void Musteri_rolu_controller_seviyesinde_TANIMLI(Type controller)
    {
        Assert.Contains("Customer", Roller(controller));
    }

    [Theory]
    [MemberData(nameof(MusteriyeAcik))]
    public void Site_bazli_controller_ALT_KULLANICI_bolum_iznine_tabi(Type controller)
    {
        // Bu uclarin tamami site detay sayfasinin altinda calisir. Alt-kullanicinin "sites"
        // bolumu kapaliyken SSL/WordPress/kota yonetebilmesi, izin matrisini delerdi.
        var bolum = controller.GetCustomAttribute<RequireSubUserSectionAttribute>();
        Assert.NotNull(bolum);
    }

    /// <summary>
    /// Musterinin ulasabildigi HER eylem kapsam denetimi yapmali.
    /// </summary>
    /// <remarks>
    /// Kaynak taramasi kullaniliyor: kapsam denetimi bir attribute degil, eylem govdesinde bir
    /// cagri. Yansimayla "cagrildi mi" sorulamaz; ileride bu controller'lara eklenecek yeni bir
    /// eylem denetimi unutursa bu test kirmizi yanar.
    /// </remarks>
    [Theory]
    [InlineData("SslController.cs")]
    [InlineData("WordPressController.cs")]
    [InlineData("StatisticsController.cs")]
    [InlineData("SiteHealthController.cs")]
    [InlineData("DiskQuotaController.cs")]
    public void Her_eylem_KAPSAM_denetiminden_geciyor(string dosya)
    {
        var kaynak = KaynakOku("src", "AdaPanel.Api", "Controllers", dosya);
        var eylemler = kaynak.Split("    [Http", StringSplitOptions.RemoveEmptyEntries).Skip(1).ToArray();

        Assert.NotEmpty(eylemler);
        foreach (var eylem in eylemler)
        {
            // Yalniz yoneticiye acik eylemler kapsam denetimi gerektirmez (zaten kapsamsiz roldur).
            if (eylem.Contains("[Authorize(Roles = \"Administrator\")]", StringComparison.Ordinal))
            {
                continue;
            }

            var denetimVar = eylem.Contains("EnsureSiteAsync", StringComparison.Ordinal)
                || eylem.Contains("GetEffectiveSitesAsync", StringComparison.Ordinal);

            Assert.True(denetimVar, $"{dosya}: kapsam denetimi olmayan bir eylem var:\n{eylem[..Math.Min(300, eylem.Length)]}");
        }
    }

    // --- Musteriye ACILMAYAN uclar: sunucu geneli olanlar ---

    [Fact]
    public void Tum_sertifikalari_yenileme_YALNIZ_yonetici()
    {
        // renew-all sunucudaki HER sitenin sertifikasini yeniler (baskasinin kaynagi) ve win-acme'yi
        // sunucu genelinde calistirir.
        Assert.Equal("Administrator", MetotRolleri(typeof(SslController), "RenewAllAsync"));
    }

    [Fact]
    public void FSRM_kurulumu_YALNIZ_yonetici()
    {
        // Sunucu geneli bir Windows rolu kurar; site basina bir islem degildir.
        Assert.Equal("Administrator", MetotRolleri(typeof(DiskQuotaController), "InstallFsrmAsync"));
    }

    [Fact]
    public void Disk_kotasini_DEGISTIRME_musteriye_kapali()
    {
        // Kota, musterinin SATIN ALDIGI sinirdir; kendi limitini yukseltebilseydi planindan kacardi.
        // Bayi kendi musterisinin kotasini yonetir (Plesk paritesi).
        foreach (var metot in new[] { "SetAsync", "RemoveAsync" })
        {
            var roller = MetotRolleri(typeof(DiskQuotaController), metot);
            Assert.DoesNotContain("Customer", roller);
            Assert.Contains("Reseller", roller);
        }
    }

    [Fact]
    public void Site_ONARIMI_musteriye_kapali_ama_TESHIS_acik()
    {
        // POST repair, tek site onarilirken SUNUCU GENELI adimlari da calistirir
        // (DomainRefreshService.RunServerWideAsync: posta sertifikasi + sunucu karsilama adi).
        Assert.DoesNotContain("Customer", MetotRolleri(typeof(SiteHealthController), "RepairAsync"));

        // Teshis (hicbir sey degistirmeyen GET) musteriye acik kalmali: "sitem neden acilmiyor"
        // sorusunu musterinin kendi yanitlayabilmesi bu ucun tek varlik sebebi.
        Assert.Equal(string.Empty, MetotRolleri(typeof(SiteHealthController), "InspectRepairAsync"));
        Assert.Equal(string.Empty, MetotRolleri(typeof(SiteHealthController), "GetAsync"));
    }

    // --- duzenek ---

    private static string Roller(Type controller) =>
        controller.GetCustomAttribute<AuthorizeAttribute>()?.Roles ?? string.Empty;

    /// <summary>Metot uzerindeki rol kisiti; yoksa bos (controller kisiti gecerlidir).</summary>
    private static string MetotRolleri(Type controller, string metotAdi)
    {
        var metot = controller.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(item => item.Name == metotAdi);

        Assert.True(metot is not null, $"{controller.Name}.{metotAdi} bulunamadi.");
        return metot!.GetCustomAttribute<AuthorizeAttribute>()?.Roles ?? string.Empty;
    }

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
}
