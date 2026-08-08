using AdaPanel.Core.Dtos.Databases;
using AdaPanel.Infrastructure.Iis;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// CASCADE SILME, VERITABANININ SAHIBINI ADINDAN VARSAYAMAZ.
/// <para>
/// Eski kural: "adi siteyle esleşen (ya da DatabaseNameFromSite ile turetilene esit olan) her
/// veritabanini DROP et". Bu, geri alinamaz bir isi DOGRULANMAMIS bir varsayima baglamakti.
/// Panel hangi veritabaninin hangi siteye ait oldugunu ZATEN biliyor: acik site-DB bagi
/// (ISiteDatabaseLinkService) ve sahiplik kaydi (IDatabaseOwnershipService, site adini da tutar).
/// Silmeden hemen sonra RemoveBySiteAsync ile bu kayitlar temizleniyordu -- yani otorite elimizde
/// olmasina ragmen SILME KARARINDA kullanilmiyordu.
/// </para>
/// <para>
/// SitesController.ResolveLinkableDatabasesAsync dogru kurali zaten uyguluyordu; artik silme yolu
/// da ayni otoriteyi kullaniyor. Ad tahminiyle bulunanlar SILINMEZ, yalnizca bildirilir.
/// </para>
/// </summary>
public sealed class SiteCascadeDatabaseOwnershipTests
{
    private const string Site = "musteri.com";

    /// <summary>Ayni adi tasiyan ama HICBIR kaydi olmayan veritabani ASLA silinmemeli.</summary>
    [Fact]
    public void Adi_esleşen_ama_sahipsiz_veritabani_silinmez()
    {
        var silinecek = SiteCascadeDeletionService.SelectDeletableDatabases(Site, [], []);

        Assert.Empty(silinecek);
    }

    /// <summary>
    /// ...ve "yok sayilmis" gibi de davranilmaz: kullanici eskiden bunun silinecegini
    /// varsayiyordu, o yuzden "silmedik ve NEDEN" bilgisi uretilir.
    /// </summary>
    [Fact]
    public void Sahipsiz_veritabani_muhtemel_olarak_bildirilir()
    {
        var muhtemel = SiteCascadeDeletionService.SelectProbableDatabases(
            Site,
            [("mariadb", "musteri.com"), ("mysql", "baskasinin_db")],
            owned: []);

        Assert.Equal(["mariadb:musteri.com"], muhtemel);
    }

    /// <summary>Acik site-DB bagi olan veritabani silinir -- adi siteyle hic ilgisiz olsa bile.</summary>
    [Fact]
    public void Acik_bagi_olan_veritabani_silinir()
    {
        var silinecek = SiteCascadeDeletionService.SelectDeletableDatabases(
            Site,
            [new SiteDatabaseLinkDto { Engine = "mariadb", DatabaseName = "karsilastir", SiteName = Site }],
            []);

        Assert.Equal(["mariadb:karsilastir"], silinecek);
    }

    /// <summary>Sahiplik kaydi da otoritedir (DB olusturulurken hangi siteye ait oldugu yazilir).</summary>
    [Fact]
    public void Sahiplik_kaydi_olan_veritabani_silinir()
    {
        var silinecek = SiteCascadeDeletionService.SelectDeletableDatabases(
            Site,
            [],
            [("mysql", "musteri_db", Guid.NewGuid(), Site)]);

        Assert.Equal(["mysql:musteri_db"], silinecek);
    }

    /// <summary>BASKA bir siteye ait kayit bu silmeye karismaz -- capraz-kiraci DROP'un kapisi budur.</summary>
    [Fact]
    public void Baska_siteye_ait_kayit_bu_silmede_kullanilmaz()
    {
        var silinecek = SiteCascadeDeletionService.SelectDeletableDatabases(
            Site,
            [],
            [("mysql", "kurban_db", Guid.NewGuid(), "kurban.com")]);

        Assert.Empty(silinecek);
    }

    /// <summary>Ayni veritabani hem bagli hem sahipliyse iki kez silinmeye calisilmamali.</summary>
    [Fact]
    public void Ayni_veritabani_iki_kez_listelenmez()
    {
        var silinecek = SiteCascadeDeletionService.SelectDeletableDatabases(
            Site,
            [new SiteDatabaseLinkDto { Engine = "mariadb", DatabaseName = "ortak", SiteName = Site }],
            [("mariadb", "ortak", Guid.NewGuid(), Site)]);

        Assert.Equal(["mariadb:ortak"], silinecek);
    }

    /// <summary>Sahipli olan bir DB "muhtemel" listesine DUSMEZ (yoksa hem silinir hem uyarilirdi).</summary>
    [Fact]
    public void Sahipli_veritabani_muhtemel_sayilmaz()
    {
        var muhtemel = SiteCascadeDeletionService.SelectProbableDatabases(
            Site,
            [("mariadb", "musteri.com")],
            owned: ["mariadb:musteri.com"]);

        Assert.Empty(muhtemel);
    }

    /// <summary>
    /// KABLOLAMA: silme dongusu otorite kumesini kullanmali. Saf islev dogru olsa da cagrilmiyorsa
    /// hicbir sey degismezdi.
    /// </summary>
    [Fact]
    public void Silme_dongusu_otorite_kumesini_kullanir()
    {
        var kaynak = OkuKaynak("src", "AdaPanel.Infrastructure", "Iis", "SiteCascadeDeletionService.cs");

        Assert.Contains("var sahipli = await ResolveOwnedDatabasesAsync(siteName, cancellationToken);", kaynak, StringComparison.Ordinal);
        Assert.Contains("foreach (var db in sahipli)", kaynak, StringComparison.Ordinal);

        // Ad tahmini artik yalnizca UYARI uretir; DeleteDatabaseAsync ile ayni dongude olamaz.
        var muhtemelIndex = kaynak.IndexOf("ResolveProbableDatabasesAsync(siteName, sahipli", StringComparison.Ordinal);
        var silmeIndex = kaynak.IndexOf("databaseService.DeleteDatabaseAsync", StringComparison.Ordinal);
        Assert.True(muhtemelIndex > silmeIndex, "Muhtemel liste, silme dongusunden ONCE hesaplanip silmeye karisiyor olabilir.");
    }

    /// <summary>Yikici sorgu parametreleri OPT-IN olmali: gondermeyen bir istemci veri kaybetmemeli.</summary>
    [Fact]
    public void Yikici_silme_parametreleri_varsayilan_olarak_kapali()
    {
        var kaynak = OkuKaynak("src", "AdaPanel.Api", "Controllers", "SitesController.cs");

        Assert.Contains("[FromQuery] bool deleteFiles = false", kaynak, StringComparison.Ordinal);
        Assert.Contains("[FromQuery] bool deleteDatabases = false", kaynak, StringComparison.Ordinal);
    }

    private static string OkuKaynak(params string[] parcalar)
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
