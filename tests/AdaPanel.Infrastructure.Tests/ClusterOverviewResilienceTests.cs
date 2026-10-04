using System.Diagnostics;
using System.Text.Json;
using AdaPanel.Core.Dtos.Cluster;
using AdaPanel.Core.Dtos.Health;
using AdaPanel.Core.Dtos.System;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Cluster;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// BIR DUGUMUN OLMESI MERKEZ PANELI COKERTMEZ.
/// <para>
/// Bu, ozelligin en kolay yanlis yapilan yeridir: uzak cagrilar <c>Task.WhenAll</c> ile paralel
/// atilir ve TEK bir istisna yukari ciktiginda dokuz saglam dugumun satiri da gider — yani panonun
/// var olma sebebi yok olur. Ayni sekilde, cagrilar sirayla yapilsaydi dugum basina zaman asimi
/// toplanir ve on dugumluk bir kurulumda pano dakikalarca gelmezdi.
/// </para>
/// <para>
/// Testler GERCEK <see cref="ClusterService"/> ve GERCEK <see cref="ClusterNodeStore"/> ile kosar;
/// yalnizca aga cikan halka (<see cref="IClusterNodeClient"/>) sahtedir — cunku olculmek istenen
/// sey tam olarak "ag basarisiz oldugunda ne oluyor".
/// </para>
/// </summary>
public sealed class ClusterOverviewResilienceTests : IDisposable
{
    private readonly string veriKoku = Path.Combine(
        Path.GetTempPath(),
        "adapanel-cluster-" + Guid.NewGuid().ToString("N"));

    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Olu_dugum_SATIRI_DUSURMEZ_ulasilamiyor_der()
    {
        var duzenek = await DuzenekAsync(("saglam", true), ("olu", true));
        duzenek.Istemci.Basarisiz("olu", ClusterMessages.NodeUnreachable, "timeout");

        var pano = await duzenek.Servis.GetOverviewAsync();

        // Iki satir da DURUYOR: olu dugumu listeden dusurmek "her sey yolunda" demek olurdu.
        Assert.Equal(2, pano.Nodes.Count);

        var olu = pano.Nodes.Single(satir => satir.Node.Label == "olu");
        Assert.False(olu.Reachable);
        Assert.Equal(ClusterMessages.NodeUnreachable, olu.StatusKey);
        Assert.Null(olu.Info);

        var saglam = pano.Nodes.Single(satir => satir.Node.Label == "saglam");
        Assert.True(saglam.Reachable);
        Assert.NotNull(saglam.Info);
    }

    [Fact]
    public async Task Olu_dugum_saglam_dugumun_verisini_GOTURMEZ()
    {
        var duzenek = await DuzenekAsync(("saglam", true), ("olu", true));
        duzenek.Istemci.SiteSayisi["saglam"] = 7;
        duzenek.Istemci.Basarisiz("olu", ClusterMessages.NodeUnreachable, "timeout");

        var pano = await duzenek.Servis.GetOverviewAsync();

        Assert.Equal(1, pano.ReachableCount);
        Assert.Equal(1, pano.UnreachableCount);
        // Ulasilamayan dugumun siteleri sayilmaz: bilinmeyeni sifir saymak, toplami "olculdu" gibi
        // gosterirdi. Eksiklik UnreachableCount ile SOYLENIR.
        Assert.Equal(7, pano.TotalSites);
    }

    /// <summary>
    /// ISTEMCI PATLASA BILE pano ayakta kalir. Istemci sozlesmesi "istisna atma" der; bu test
    /// sozlesmenin ihlal edildigi gunu de kapsar (son savunma).
    /// </summary>
    [Fact]
    public async Task Istemci_ISTISNA_atsa_bile_pano_ayakta_kalir()
    {
        var duzenek = await DuzenekAsync(("saglam", true), ("patlayan", true));
        duzenek.Istemci.Patlat.Add("patlayan");

        var pano = await duzenek.Servis.GetOverviewAsync();

        Assert.Equal(2, pano.Nodes.Count);
        Assert.True(pano.Nodes.Single(satir => satir.Node.Label == "saglam").Reachable);
        Assert.False(pano.Nodes.Single(satir => satir.Node.Label == "patlayan").Reachable);
    }

    /// <summary>
    /// YAVAS dugum digerlerini BEKLETMEZ. Uc dugumun her biri 400 ms gecikiyorsa toplam sure
    /// sirali calismada 1200 ms'e yaklasir; paralelde tek gecikme kadardir.
    /// </summary>
    [Fact]
    public async Task Yavas_dugumler_PARALEL_yoklanir_sirayla_degil()
    {
        var duzenek = await DuzenekAsync(("bir", true), ("iki", true), ("uc", true));
        // Kisa bir gecikme, cagrilarin gercekten UST USTE BINEBILMESI icin yeter; olcum saate
        // degil, ayni anda acik cagri sayisina bakiyor (bkz. SahteIstemci.EszamanliZirve).
        duzenek.Istemci.Gecikme = TimeSpan.FromMilliseconds(50);

        var pano = await duzenek.Servis.GetOverviewAsync();

        Assert.Equal(3, pano.ReachableCount);
        Assert.True(
            duzenek.Istemci.EszamanliZirve > 1,
            $"Yoklama sirayla yapiliyor: ayni anda en fazla {duzenek.Istemci.EszamanliZirve} cagri acikti.");
    }

    /// <summary>
    /// KAPALI dugum HIC yoklanmaz. Yoklanmasi, ayakta olmadigi bilinen bir sunucu icin her turda
    /// zaman asimi beklemek demek olurdu.
    /// </summary>
    [Fact]
    public async Task Kapali_dugum_YOKLANMAZ()
    {
        var duzenek = await DuzenekAsync(("acik", true), ("kapali", false));

        var pano = await duzenek.Servis.GetOverviewAsync();

        var kapali = pano.Nodes.Single(satir => satir.Node.Label == "kapali");
        Assert.False(kapali.Reachable);
        Assert.Equal(ClusterMessages.NodeDisabled, kapali.StatusKey);
        Assert.DoesNotContain("kapali", duzenek.Istemci.Yoklananlar);
        Assert.Contains("acik", duzenek.Istemci.Yoklananlar);
    }

    /// <summary>
    /// Cagiran iptal ettiginde (kullanici sayfadan ayrildi / panel kapaniyor) iptal YUKARI cikar.
    /// Yutulsaydi, kapanan panel "butun dugumler olu" diye bir tur daha kaydeder ve son hata
    /// alanlarini yalanla doldururdu.
    /// </summary>
    [Fact]
    public async Task Cagiranin_iptali_YUKARI_cikar()
    {
        var duzenek = await DuzenekAsync(("bir", true));
        using var kaynak = new CancellationTokenSource();
        await kaynak.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => duzenek.Servis.GetOverviewAsync(kaynak.Token));
    }

    [Fact]
    public async Task Basarili_yoklama_SON_GORULME_anini_yazar()
    {
        var duzenek = await DuzenekAsync(("saglam", true));

        await duzenek.Servis.GetOverviewAsync();
        var dugumler = await duzenek.Servis.GetNodesAsync();

        Assert.NotNull(dugumler[0].LastSeenAtUtc);
        Assert.Equal(string.Empty, dugumler[0].LastError);
    }

    [Fact]
    public async Task Basarisiz_yoklama_SON_HATAYI_yazar()
    {
        var duzenek = await DuzenekAsync(("olu", true));
        duzenek.Istemci.Basarisiz("olu", ClusterMessages.NodeUnreachable, "timeout");

        await duzenek.Servis.GetOverviewAsync();
        var dugumler = await duzenek.Servis.GetNodesAsync();

        Assert.Null(dugumler[0].LastSeenAtUtc);
        Assert.Contains("timeout", dugumler[0].LastError, StringComparison.Ordinal);
    }

    /// <summary>
    /// Ulasilamayan dugume SITE KURULMAZ. "Kuruldu" deyip kurmamak, panelin yalan soylemesinin en
    /// pahali bicimidir (kullanici siteyi var sanip DNS'i ona yonlendirir).
    /// </summary>
    [Fact]
    public async Task Ulasilamayan_dugume_site_KURULMAZ()
    {
        var duzenek = await DuzenekAsync(("olu", true));
        duzenek.Istemci.Basarisiz("olu", ClusterMessages.NodeUnreachable, "timeout");
        var dugum = (await duzenek.Servis.GetNodesAsync())[0];

        var hata = await Assert.ThrowsAsync<AdaPanel.Core.Common.AppException>(
            () => duzenek.Servis.CreateSiteAsync(new CreateClusterSiteRequest { NodeId = dugum.Id, Name = "ornek.test" }));

        Assert.Equal(ClusterMessages.NodeUnreachable, hata.MessageKey);
        Assert.Empty(duzenek.Istemci.Gonderilenler);
    }

    [Fact]
    public async Task Hicbir_dugum_yokken_pano_BOS_ama_calisir()
    {
        var duzenek = await DuzenekAsync();

        var pano = await duzenek.Servis.GetOverviewAsync();

        Assert.Empty(pano.Nodes);
        Assert.Equal(0, pano.TotalSites);
        Assert.Equal(0, pano.ReachableCount);
    }

    // ---------------------------------------------------------------------------------------
    // duzenek
    // ---------------------------------------------------------------------------------------

    private async Task<(ClusterService Servis, SahteIstemci Istemci)> DuzenekAsync(params (string Etiket, bool Acik)[] dugumler)
    {
        var dosyaDeposu = new JsonFileStore(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = veriKoku }));
        var depo = new ClusterNodeStore(dosyaDeposu, new SahteKoruyucu(), NullLogger<ClusterNodeStore>.Instance);
        var istemci = new SahteIstemci();
        var servis = new ClusterService(depo, istemci, NullLogger<ClusterService>.Instance)
        {
            // Testte gercek DNS'e cikilmaz: adres denetimi kendi dosyasinda olculuyor.
            Resolver = (_, _) => Task.FromResult(new[] { System.Net.IPAddress.Parse("93.184.216.34") })
        };

        foreach (var (etiket, acik) in dugumler)
        {
            await servis.RegisterAsync(new RegisterClusterNodeRequest
            {
                Label = etiket,
                Address = $"https://{etiket}.ornek.test",
                ApiToken = "adap_" + etiket,
                Enabled = acik
            });
        }

        return (servis, istemci);
    }

    /// <summary>DPAPI yerine gecen, tersine cevrilebilir ama DUZ OLMAYAN koruyucu.</summary>
    private sealed class SahteKoruyucu : ISecretProtector
    {
        public string Protect(string plaintext) =>
            Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("korunmus:" + plaintext));

        public string Unprotect(string protectedValue) =>
            System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(protectedValue))["korunmus:".Length..];
    }

    /// <summary>
    /// Aga cikan tek halkanin sahtesi. Gercek istemcinin SOZLESMESINI taklit eder: basarisizlik
    /// bir DEGERDIR, istisna degil — <see cref="Patlat"/> ise sozlesmenin ihlal edildigi gunu
    /// canlandirir.
    /// </summary>
    private sealed class SahteIstemci : IClusterNodeClient
    {
        private readonly Dictionary<string, (string Kod, string Ayrinti)> hatalar = new(StringComparer.Ordinal);

        public Dictionary<string, int> SiteSayisi { get; } = new(StringComparer.Ordinal);

        public HashSet<string> Patlat { get; } = new(StringComparer.Ordinal);

        public List<string> Yoklananlar { get; } = [];

        public List<string> Gonderilenler { get; } = [];

        public TimeSpan Gecikme { get; set; } = TimeSpan.Zero;

        private int anlikCagri;

        /// <summary>
        /// Aynı anda kaç yoklamanın açık olduğunun ZİRVESİ. Paralellik testi eskiden duvar saatiyle
        /// ölçülüyordu ("3 x 400 ms toplam 1000 ms'nin altında kalmalı") ve tam takım yükü altında
        /// rastgele kırmızı veriyordu: paralel ideal 400 ms, sıralı 1200 ms, eşik 1000 ms — yani
        /// "yavaş paralel" ile "sıralı" arasında yalnız 200 ms vardı. Burada ölçülen şey vekil bir
        /// gösterge değil, sorunun kendisi: sıralı çalışsaydı zirve 1'de kalırdı.
        /// </summary>
        public int EszamanliZirve { get; private set; }

        public void Basarisiz(string etiket, string kod, string ayrinti) => hatalar[etiket] = (kod, ayrinti);

        public async Task<ClusterCallResult<ClusterNodeInfoDto>> FetchInfoAsync(
            ClusterNodeRecord node,
            string token,
            CancellationToken cancellationToken)
        {
            lock (Yoklananlar)
            {
                Yoklananlar.Add(node.Label);
                anlikCagri++;
                EszamanliZirve = Math.Max(EszamanliZirve, anlikCagri);
            }

            try
            {
                return await YoklaAsync(node, cancellationToken);
            }
            finally
            {
                lock (Yoklananlar)
                {
                    anlikCagri--;
                }
            }
        }

        private async Task<ClusterCallResult<ClusterNodeInfoDto>> YoklaAsync(
            ClusterNodeRecord node,
            CancellationToken cancellationToken)
        {
            if (Patlat.Contains(node.Label))
            {
                throw new InvalidOperationException("sozlesme ihlali");
            }

            if (Gecikme > TimeSpan.Zero)
            {
                await Task.Delay(Gecikme, cancellationToken);
            }

            if (hatalar.TryGetValue(node.Label, out var hata))
            {
                return new ClusterCallResult<ClusterNodeInfoDto>(null, hata.Kod, hata.Ayrinti, 5);
            }

            return new ClusterCallResult<ClusterNodeInfoDto>(
                new ClusterNodeInfoDto
                {
                    PanelVersion = "0.6.1",
                    MachineName = node.Label.ToUpperInvariant(),
                    SecureEntryPath = "gizli-" + node.Label,
                    SiteCount = SiteSayisi.TryGetValue(node.Label, out var sayi) ? sayi : 1,
                    DefaultSiteRoot = @"C:\inetpub\wwwroot",
                    Overview = new SystemOverviewDto { DisksMeasured = true,
                        Disks = [new DiskUsageDto { Name = "C:", Measured = true, TotalBytes = 1000, FreeBytes = 600, UsedBytes = 400, UsagePercent = 40 }] },
                    Services = [new ServiceHealthDto { Name = "W3SVC", Status = "Running" }]
                },
                string.Empty,
                string.Empty,
                5);
        }

        public Task<ClusterCallResult<JsonElement>> PostAsync(
            ClusterNodeRecord node,
            string token,
            string relativePath,
            object payload,
            CancellationToken cancellationToken)
        {
            lock (Gonderilenler)
            {
                Gonderilenler.Add($"{node.Label}:{relativePath}");
            }

            if (hatalar.TryGetValue(node.Label, out var hata))
            {
                return Task.FromResult(new ClusterCallResult<JsonElement>(default, hata.Kod, hata.Ayrinti, 5));
            }

            return Task.FromResult(new ClusterCallResult<JsonElement>(default, string.Empty, string.Empty, 5));
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(veriKoku))
        {
            Directory.Delete(veriKoku, recursive: true);
        }
    }
}
