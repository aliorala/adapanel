using AdaPanel.Core.Dtos.Cluster;
using AdaPanel.Core.Dtos.System;
using AdaPanel.Infrastructure.Cluster;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// YENI SITE HANGI DUGUME GIDER.
/// <para>
/// Plesk Multi Server'da bir abonelik TEK dugume kurulur ve dugumler arasina bolunmez; yeni
/// abonelik icin dugum yuk dengelemeyle secilir (varsayilani Round Robin). Buradaki karar ayni
/// hedefi tasir ama SAYAC TUTMAZ: yerlesim, zaten olculmus olan site sayisindan turetilir. Sayac
/// yeniden baslatmada sifirlanir ve dugum listesi degistiginde anlamini yitirirdi; site sayisi ise
/// gercegin kendisidir ve gercekten dolu olan dugumu kendiliginden atlar.
/// </para>
/// <para>
/// Karar SAF bir fonksiyon oldugu icin "on dugumun besi olu, ucu dolu" gibi bir senaryo tek birim
/// testiyle kurulabiliyor; servise gomulseydi ancak on sahte sunucu ayaga kaldirarak denenebilirdi.
/// </para>
/// </summary>
public sealed class ClusterPlacementTests
{
    [Fact]
    public void En_az_siteli_dugum_secilir()
    {
        var karar = ClusterPlacement.Select([Satir("dolu", siteSayisi: 40), Satir("bos", siteSayisi: 3)]);

        Assert.Equal("bos", karar.NodeLabel);
        Assert.Equal(ClusterMessages.PlacementLeastLoaded, karar.ReasonKey);
        Assert.Equal(2, karar.CandidateCount);
    }

    /// <summary>
    /// ULASILAMAYAN dugum ADAY DEGILDIR: site olusturma cagrisi zaten basarisiz olurdu, aday
    /// saymak kullaniciya "kuruldu" demeye bir adim daha yaklasmak olurdu.
    /// </summary>
    [Fact]
    public void Ulasilamayan_dugum_ADAY_DEGILDIR()
    {
        var karar = ClusterPlacement.Select(
        [
            Satir("olu", siteSayisi: 0, ulasilabilir: false),
            Satir("saglam", siteSayisi: 25)
        ]);

        Assert.Equal("saglam", karar.NodeLabel);
        Assert.Equal(1, karar.CandidateCount);
    }

    [Fact]
    public void Kapali_dugum_ADAY_DEGILDIR()
    {
        var karar = ClusterPlacement.Select(
        [
            Satir("kapali", siteSayisi: 0, acik: false),
            Satir("acik", siteSayisi: 25)
        ]);

        Assert.Equal("acik", karar.NodeLabel);
        Assert.Equal(1, karar.CandidateCount);
    }

    [Fact]
    public void Hicbir_aday_yoksa_ACIKCA_soylenir()
    {
        var karar = ClusterPlacement.Select([Satir("olu", siteSayisi: 0, ulasilabilir: false)]);

        Assert.Null(karar.NodeId);
        Assert.Equal(ClusterMessages.NoNodeAvailable, karar.ReasonKey);
        Assert.Equal(0, karar.CandidateCount);
    }

    [Fact]
    public void Bos_liste_COKERTMEZ()
    {
        Assert.Null(ClusterPlacement.Select([]).NodeId);
        Assert.Null(ClusterPlacement.Select(null).NodeId);
    }

    /// <summary>
    /// Site sayisi esitse EN BOS DISK kazanir.
    /// </summary>
    [Fact]
    public void Site_sayisi_esitse_disk_doluluguna_bakilir()
    {
        var karar = ClusterPlacement.Select(
        [
            Satir("dolu-disk", siteSayisi: 10, diskYuzde: 91),
            Satir("bos-disk", siteSayisi: 10, diskYuzde: 22)
        ]);

        Assert.Equal("bos-disk", karar.NodeLabel);
    }

    /// <summary>
    /// Ortalama degil EN DOLU disk bakilir: sunucuyu durduran sey disklerin ortalamasi degil,
    /// dolan ilk diskidir.
    /// </summary>
    [Fact]
    public void Disk_olcusu_ORTALAMA_degil_EN_KOTU_disktir()
    {
        var bilgi = new ClusterNodeInfoDto
        {
            Overview = new SystemOverviewDto
            {
                DisksMeasured = true,
                Disks =
                [
                    new DiskUsageDto { Name = "C:", Measured = true, TotalBytes = 1000, FreeBytes = 900, UsedBytes = 100, UsagePercent = 10 },
                    new DiskUsageDto { Name = "D:", Measured = true, TotalBytes = 1000, FreeBytes = 20, UsedBytes = 980, UsagePercent = 98 }
                ]
            }
        };

        // Ortalama 54 olurdu; olculen 98.
        Assert.Equal(98, ClusterPlacement.MaxDiskUsagePercent(bilgi));
    }

    /// <summary>
    /// Her sey esitse karar KARARLI olmali: ayni girdi iki farkli cevap verirse ozellik test
    /// edilemez ve kullanici ayni formu iki kez actiginda iki farkli dugum gorurdu.
    /// </summary>
    [Fact]
    public void Tam_esitlikte_karar_KARARLIDIR()
    {
        var satirlar = new[] { Satir("zeta", 5, diskYuzde: 50), Satir("alfa", 5, diskYuzde: 50) };

        var birinci = ClusterPlacement.Select(satirlar);
        var ikinci = ClusterPlacement.Select([.. satirlar.Reverse()]);

        Assert.Equal("alfa", birinci.NodeLabel);
        Assert.Equal(birinci.NodeLabel, ikinci.NodeLabel);
    }

    /// <summary>
    /// Uzak yol dugumun KENDI kokunden kurulur; merkezin koku kullanilsaydi dugumler farkli disk
    /// duzenine sahip oldugunda site var olmayan bir dizine kurulurdu (site olusturma ucu yonetici
    /// isteginde yolu ZORLAMAZ, yani yanlis yol sessizce kabul edilir).
    /// </summary>
    [Theory]
    [InlineData(@"D:\siteler", "ornek.test", @"D:\siteler\ornek.test")]
    [InlineData(@"D:\siteler\", "ornek.test", @"D:\siteler\ornek.test")]
    [InlineData("", "ornek.test", @"C:\inetpub\wwwroot\ornek.test")]
    [InlineData(null, "ornek.test", @"C:\inetpub\wwwroot\ornek.test")]
    [InlineData(@"C:\inetpub\wwwroot", @"\ornek.test", @"C:\inetpub\wwwroot\ornek.test")]
    public void Uzak_fiziksel_yol_dugumun_KENDI_kokunden_kurulur(string? kok, string siteAdi, string beklenen)
    {
        Assert.Equal(beklenen, ClusterService.BuildRemotePath(kok, siteAdi));
    }

    private static ClusterNodeStatusDto Satir(
        string etiket,
        int siteSayisi,
        bool ulasilabilir = true,
        bool acik = true,
        double diskYuzde = 40) => new()
        {
            Node = new ClusterNodeDto { Id = Guid.NewGuid(), Label = etiket, Enabled = acik },
            Reachable = ulasilabilir,
            Info = ulasilabilir
                ? new ClusterNodeInfoDto
                {
                    SiteCount = siteSayisi,
                    Overview = new SystemOverviewDto
                    {
                        DisksMeasured = true,
                        Disks = [new DiskUsageDto { Name = "C:", Measured = true, TotalBytes = 1000,
                            FreeBytes = (long)(1000 - diskYuzde * 10), UsedBytes = (long)(diskYuzde * 10), UsagePercent = diskYuzde }]
                    }
                }
                : null
        };
}
