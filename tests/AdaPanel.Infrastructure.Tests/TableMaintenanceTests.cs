using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Tablo bakiminin SAF kararlari: hangi motor onarilabilir, bir denetim sonucu sorunlu mu.
/// <para>
/// NEDEN BU TESTLER KRITIK: bu kararlar yanlis verildiginde sonuc SESSIZDIR -- panel "onarildi"
/// der, hicbir sey yapilmamistir. Belgelenmis davranis: InnoDB'ye REPAIR TABLE gonderilirse HATA
/// VERMEZ; "note: The storage engine for the table doesn't support repair" ardindan "status: OK"
/// doner ve mysqlcheck cikis kodu 0'dir. Modern kurulumlarin neredeyse tamami InnoDB oldugundan,
/// motora bakmadan onarim calistiran bir panel kullanicilarinin cogunu kandirir.
/// </para>
/// <para>
/// ISPConfig'in acik kaynak kodu tam bu hatayi yapiyor: satirin son iki karakterine bakip "OK" ise
/// basarili sayiyor. InnoDB'nin ise yaramaz "status: OK" satiri o testten gecer.
/// </para>
/// </summary>
public sealed class TableMaintenanceTests
{
    // REPAIR TABLE'in GERCEKTEN calistigi motorlar (MySQL: MyISAM/ARCHIVE/CSV, MariaDB ayrica Aria).
    [Theory]
    [InlineData("MyISAM")]
    [InlineData("myisam")]
    [InlineData("Aria")]
    [InlineData("ARCHIVE")]
    [InlineData("CSV")]
    public void RepairableEngines_AreAccepted(string motor)
    {
        Assert.True(AdaPanel.Infrastructure.Database.TableMaintenance.CanRepair(motor));
    }

    /// <summary>
    /// EN KRITIK TEST: InnoDB onarilabilir sayilmamali. Bu tek satir, ozelligin bir numarali
    /// hatasina karsi koruma saglar.
    /// </summary>
    [Theory]
    [InlineData("InnoDB")]
    [InlineData("innodb")]
    [InlineData("MEMORY")]
    [InlineData("MRG_MyISAM")]
    [InlineData("")]
    [InlineData(null)]
    public void EnginesThatCannotBeRepaired_AreNeverOffered(string? motor)
    {
        Assert.False(AdaPanel.Infrastructure.Database.TableMaintenance.CanRepair(motor));
    }

    // InnoDB onarilamaz ama YENIDEN INSA edilebilir (OPTIMIZE -> ALTER TABLE ... FORCE).
    // Bu bir onarim degildir ve ekranda da oyle adlandirilmamalidir.
    [Fact]
    public void InnoDb_IsOfferedRebuildInsteadOfRepair()
    {
        Assert.True(AdaPanel.Infrastructure.Database.TableMaintenance.CanRebuild("InnoDB"));
        Assert.False(AdaPanel.Infrastructure.Database.TableMaintenance.CanRebuild("MyISAM"));
    }

    /// <summary>
    /// InnoDB'ye REPAIR gonderilirse gelen GERCEK satirlar. Son satir "OK" oldugu icin naif bir
    /// ayristirici basarili sayar; dogru cevap "desteklenmiyor"dur.
    /// </summary>
    [Fact]
    public void TheInnoDbRepairTrap_IsNotReportedAsHealthy()
    {
        var satirlar = new[]
        {
            ("note", "The storage engine for the table doesn't support repair"),
            ("status", "OK")
        };

        Assert.Equal("unsupported", AdaPanel.Infrastructure.Database.TableMaintenance.Classify(satirlar));
    }

    /// <summary>
    /// OPTIMIZE'in benzer gorunen mesaji ZIT anlamlidir: tablo GERCEKTEN yeniden insa edildi.
    /// Ayirt eden kelime "instead". Ikisini karistirmak yanlis alarm uretir.
    /// </summary>
    [Fact]
    public void TheOptimizeNote_MeansSuccessNotFailure()
    {
        var satirlar = new[]
        {
            ("note", "Table does not support optimize, doing recreate + analyze instead"),
            ("status", "OK")
        };

        Assert.Equal("healthy", AdaPanel.Infrastructure.Database.TableMaintenance.Classify(satirlar));
    }

    // SON SATIRA BAKMAK YETMEZ: hata ortada olup son satir "OK" olabilir.
    [Fact]
    public void AnErrorInTheMiddle_IsNotHiddenByATrailingOkRow()
    {
        var satirlar = new[]
        {
            ("error", "Table 'x' is marked as crashed and should be repaired"),
            ("status", "OK")
        };

        Assert.Equal("problem", AdaPanel.Infrastructure.Database.TableMaintenance.Classify(satirlar));
    }

    // "Table is already up to date" SAGLIKLIDIR: motor "denetlemeye gerek yok" diyor.
    // Sorun saymak sahte alarm uretirdi.
    [Fact]
    public void AlreadyUpToDate_IsHealthyNotAProblem()
    {
        var satirlar = new[] { ("status", "Table is already up to date") };

        Assert.Equal("healthy", AdaPanel.Infrastructure.Database.TableMaintenance.Classify(satirlar));
    }

    [Fact]
    public void ACleanCheck_IsHealthy()
    {
        Assert.Equal("healthy", AdaPanel.Infrastructure.Database.TableMaintenance.Classify(new[] { ("status", "OK") }));
    }

    // warning da sorundur: yalnizca "error" aramak, bozulmanin bir kismini gorunmez kilardi.
    [Fact]
    public void Warnings_CountAsProblemsToo()
    {
        var satirlar = new[] { ("warning", "1 client is using or hasn't closed the table properly"), ("status", "OK") };

        Assert.Equal("problem", AdaPanel.Infrastructure.Database.TableMaintenance.Classify(satirlar));
    }

    /// <summary>
    /// Tablo/veritabani adlari kullanicidan gelir ve CHECK/REPAIR TABLE parametre KABUL ETMEZ --
    /// ad sorguya metin olarak eklenmek zorundadir. Kacirma bu yuzden sart.
    /// </summary>
    [Theory]
    [InlineData("wp_posts", "`wp_posts`")]
    [InlineData("tablo`adi", "`tablo``adi`")]
    [InlineData("bos degil", "`bos degil`")]
    public void Identifiers_AreQuotedAndInnerBackticksDoubled(string girdi, string beklenen)
    {
        Assert.Equal(beklenen, AdaPanel.Infrastructure.Database.TableMaintenance.Quote(girdi));
    }
}
