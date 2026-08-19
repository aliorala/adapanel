using AdaPanel.Core.Dtos.Seo;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Seo;
using AdaPanel.Infrastructure.Support;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// TARAMA KUYRUĞU ve RAPOR SAKLAMA: arka planda koşan işin görünürlüğü ve geçmişin sınırı.
/// </summary>
/// <remarks>
/// Kuyruk, panelin isteğini bloklamamak için var. Ama arka plana alınan iş GÖRÜNÜR olmazsa
/// kullanıcı "başladı mı" sorusunu cevaplayamaz; bu testler durumun her adımda doğru
/// raporlandığını sabitler.
/// </remarks>
public sealed class SeoScanQueueTests
{
    private static SeoScanJob Job(string site) => new(Guid.NewGuid().ToString("N"), site, "/", 50, null);

    [Fact]
    public void Kuyruga_alinan_tarama_QUEUED_gorunur()
    {
        var queue = new SeoScanQueue();

        Assert.Equal("idle", queue.GetState("ornek.test").State);
        Assert.Equal(SeoEnqueueResult.Accepted, queue.TryEnqueue(Job("ornek.test")));

        var durum = queue.GetState("ornek.test");
        Assert.Equal("queued", durum.State);
        Assert.Equal(50, durum.PageLimit);
        Assert.Equal(1, durum.QueueLength);
    }

    /// <summary>
    /// AYNI SİTE İÇİN İKİNCİ TARAMA REDDEDİLİR. Aksi hâlde düğmeye üst üste basmak, aynı siteye
    /// paralel taramalar başlatıp sunucuyu kendi kendine yorardı.
    /// </summary>
    [Fact]
    public void Ayni_sitenin_IKINCI_taramasi_reddedilir()
    {
        var queue = new SeoScanQueue();
        queue.TryEnqueue(Job("ornek.test"));

        Assert.Equal(SeoEnqueueResult.AlreadyRunning, queue.TryEnqueue(Job("ornek.test")));
    }

    /// <summary>Kuyruk kapasitesi doluyken yeni iş kabul edilmez (sınırsız kuyruk = sınırsız bellek).</summary>
    [Fact]
    public void Kuyruk_DOLUNCA_yeni_is_kabul_edilmez()
    {
        var queue = new SeoScanQueue();
        for (var i = 0; i < SeoScanQueue.Capacity; i++)
        {
            Assert.Equal(SeoEnqueueResult.Accepted, queue.TryEnqueue(Job($"site{i}.test")));
        }

        Assert.Equal(SeoEnqueueResult.QueueFull, queue.TryEnqueue(Job("tasan.test")));
    }

    /// <summary>Sırada beklerken iptal edilen iş HİÇ başlatılmaz.</summary>
    [Fact]
    public void SIRADA_iptal_edilen_is_baslatilmaz()
    {
        var queue = new SeoScanQueue();
        var job = Job("ornek.test");
        queue.TryEnqueue(job);

        Assert.True(queue.Cancel("ornek.test"));
        Assert.Null(queue.TryBeginRun(job));
        Assert.Equal("idle", queue.GetState("ornek.test").State);
    }

    /// <summary>SÜREN tarama iptal edilince belirteç tetiklenir — tarayıcı bunu görüp durur.</summary>
    [Fact]
    public void SUREN_tarama_iptal_edilince_belirtec_tetiklenir()
    {
        var queue = new SeoScanQueue();
        var job = Job("ornek.test");
        queue.TryEnqueue(job);

        using var cancellation = queue.TryBeginRun(job);
        Assert.NotNull(cancellation);
        Assert.Equal("running", queue.GetState("ornek.test").State);

        queue.ReportProgress("ornek.test", 7);
        Assert.Equal(7, queue.GetState("ornek.test").PagesCrawled);

        Assert.True(queue.Cancel("ornek.test"));
        Assert.True(cancellation!.Token.IsCancellationRequested);

        queue.Finish("ornek.test");
        Assert.Equal("idle", queue.GetState("ornek.test").State);
    }

    [Fact]
    public void Durdurulacak_is_yoksa_iptal_FALSE_doner()
    {
        Assert.False(new SeoScanQueue().Cancel("ornek.test"));
    }

    /// <summary>
    /// GEÇMİŞ SINIRLI: silen kimse olmadığında dosyaların sonsuza kadar büyüdüğü ders
    /// (AuditRetentionWorker) burada baştan uygulanır.
    /// </summary>
    [Fact]
    public async Task Rapor_gecmisi_EN_YENIDEN_eskiye_saklanir_ve_sinirlanir()
    {
        var kok = Path.Combine(Path.GetTempPath(), "adapanel-seo-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SeoReportStore(new JsonFileStore(
                Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = kok })));

            for (var i = 0; i < SeoReportStore.MaxReports + 3; i++)
            {
                await store.AppendAsync(
                    "ornek.test",
                    new SeoScanReportDto { Id = $"r{i}", SiteName = "ornek.test", Status = "completed" },
                    CancellationToken.None);
            }

            var gecmis = await store.ReadAsync("ornek.test", CancellationToken.None);

            Assert.Equal(SeoReportStore.MaxReports, gecmis.Reports.Count);
            Assert.Equal("r7", gecmis.Reports[0].Id);   // en yeni başta
            Assert.Equal("r3", gecmis.Reports[^1].Id);  // en eskiler düşmüş
        }
        finally
        {
            if (Directory.Exists(kok))
            {
                Directory.Delete(kok, recursive: true);
            }
        }
    }

    /// <summary>Site adı dosya adına çevrilirken ayraç bırakılmaz (veri kökü dışına yazım olamaz).</summary>
    [Fact]
    public async Task Site_adi_dosya_adina_GUVENLE_cevrilir()
    {
        var kok = Path.Combine(Path.GetTempPath(), "adapanel-seo-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SeoReportStore(new JsonFileStore(
                Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = kok })));

            await store.AppendAsync(
                @"..\..\kacis",
                new SeoScanReportDto { Id = "r1", Status = "completed" },
                CancellationToken.None);

            var yazilanlar = Directory.GetFiles(Path.Combine(kok, "config", "seo"));

            Assert.Single(yazilanlar);
            Assert.DoesNotContain("..\\..", Path.GetFileName(yazilanlar[0]), StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(kok))
            {
                Directory.Delete(kok, recursive: true);
            }
        }
    }
}
