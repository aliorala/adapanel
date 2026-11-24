using AdaPanel.Core.Dtos.Monitoring;
using AdaPanel.Infrastructure.Monitoring;
using AdaPanel.Infrastructure.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Veritabanı sorgu geçmişi: canlı listeden düşen sorguların kaydı.
/// <para>
/// Bu bir ÖRNEKLEME kaydıdır ve iki şeyin doğru olması gerekir: (1) bir sorgu ancak GERÇEKTEN
/// listeden düştüğünde "bitti" sayılmalı, (2) aynı şekildeki sorgular gruplanabilmeli — yoksa
/// "hangi sorgu çok çalışmış" sorusu cevapsız kalır.
/// </para>
/// </summary>
public sealed class QueryLogServiceTests : IDisposable
{
    private readonly string dataRoot = Path.Combine(Path.GetTempPath(), $"ada-qlog-{Guid.NewGuid():N}");
    private readonly QueryLogService servis;
    private readonly string benzersiz = Guid.NewGuid().ToString("N");

    public QueryLogServiceTests()
    {
        Directory.CreateDirectory(dataRoot);
        servis = new QueryLogService(
            Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = dataRoot }),
            NullLogger<QueryLogService>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(dataRoot, recursive: true); } catch { /* en iyi çaba */ }
    }

    [Fact]
    public async Task Listeden_dusen_sorgu_kaydedilir_duran_sorgu_kaydedilmez()
    {
        // 1. tur: iki sorgu açık
        Assert.Equal(0, await servis.RecordSampleAsync(Anlik(Sorgu("a", "SELECT 1", 3), Sorgu("b", "SELECT 2", 7))));

        // 2. tur: "a" bitti, "b" hâlâ çalışıyor → yalnız "a" kaydedilmeli
        Assert.Equal(1, await servis.RecordSampleAsync(Anlik(Sorgu("b", "SELECT 2", 9))));

        var sayfa = await servis.QueryAsync(new QueryLogFilterDto());
        var kayit = Assert.Single(sayfa.Entries);
        Assert.Equal("SELECT 1", kayit.Statement);
        // Motorun bildirdiği süre kullanılır, örnekleme farkı değil.
        Assert.Equal(3, kayit.DurationSeconds);
    }

    [Fact]
    public async Task Liste_OKUNAMADIYSA_hicbir_sorgu_bitti_sayilmaz()
    {
        // Ölçememeyi "hepsi bitti" saymak, hâlâ çalışan sorguları günlüğe bitmiş gibi yazardı.
        await servis.RecordSampleAsync(Anlik(Sorgu("a", "SELECT 1", 3)));

        Assert.Equal(0, await servis.RecordSampleAsync(Anlik(okunabildi: false)));

        // Sorgu hâlâ "açık" sayılmalı: sonraki geçerli turda düşerse o zaman kaydedilir.
        Assert.Empty((await servis.QueryAsync(new QueryLogFilterDto())).Entries);
        Assert.Equal(1, await servis.RecordSampleAsync(Anlik()));
    }

    [Fact]
    public async Task Ayni_baglantida_arka_arkaya_calisan_farkli_sorgular_ayri_kaydedilir()
    {
        // Bağlantı kimliği tek başına kimlik olamaz: motor onu geri kullanır ve iki ayrı sorgu
        // tek kayıt sanılırdı.
        await servis.RecordSampleAsync(Anlik(Sorgu("a", "SELECT ilk", 2)));
        await servis.RecordSampleAsync(Anlik(Sorgu("a", "SELECT ikinci", 4)));
        await servis.RecordSampleAsync(Anlik());

        var kayitlar = (await servis.QueryAsync(new QueryLogFilterDto())).Entries;
        Assert.Equal(2, kayitlar.Count);
        Assert.Contains(kayitlar, item => item.Statement == "SELECT ilk");
        Assert.Contains(kayitlar, item => item.Statement == "SELECT ikinci");
    }

    [Theory]
    // Değişmezler temizlenmeli, yoksa WHERE id=1 ile WHERE id=2 ayrı sorgu sayılır ve hiçbir şey
    // öne çıkmaz — "hangi sorgu çok çalışmış" sorusu cevapsız kalır.
    [InlineData("SELECT * FROM t WHERE id = 1", "SELECT * FROM t WHERE id = ?")]
    [InlineData("SELECT * FROM t WHERE id = 42", "SELECT * FROM t WHERE id = ?")]
    [InlineData("SELECT * FROM t WHERE ad = 'ali'", "SELECT * FROM t WHERE ad = ?")]
    [InlineData("SELECT * FROM t WHERE ad = \"veli\"", "SELECT * FROM t WHERE ad = ?")]
    [InlineData("SELECT  *   FROM\n t", "SELECT * FROM t")]
    [InlineData("", "")]
    public void Sorgu_sekli_degismezleri_temizler(string girdi, string beklenen)
    {
        Assert.Equal(beklenen, QueryLogService.Sekil(girdi));
    }

    [Fact]
    public async Task Ozet_toplam_sureye_gore_siralar()
    {
        // Sunucuyu asıl yoran, çok ÇALIŞAN değil çok SÜREN olabilir; sıralama bunu yansıtmalı.
        await servis.RecordSampleAsync(Anlik(
            Sorgu("a", "SELECT * FROM t WHERE id = 1", 1),
            Sorgu("b", "SELECT * FROM t WHERE id = 2", 1),
            Sorgu("c", "SELECT * FROM t WHERE id = 3", 1),
            Sorgu("d", "UPDATE agir SET x = 5", 90)));
        await servis.RecordSampleAsync(Anlik());

        var ozet = (await servis.QueryAsync(new QueryLogFilterDto())).TopShapes.ToArray();

        Assert.Equal("UPDATE agir SET x = ?", ozet[0].Shape);
        Assert.Equal(90, ozet[0].TotalSeconds);

        var select = ozet.Single(row => row.Shape.StartsWith("SELECT", StringComparison.Ordinal));
        Assert.Equal(3, select.Count); // üç farklı id TEK şekilde toplandı
    }

    [Fact]
    public async Task Filtreler_uygulanir()
    {
        await servis.RecordSampleAsync(Anlik(
            Sorgu("a", "SELECT hizli", 1, site: "site-a", db: "db1"),
            Sorgu("b", "SELECT yavas", 45, site: "site-b", db: "db2", uzak: true)));
        await servis.RecordSampleAsync(Anlik());

        Assert.Single((await servis.QueryAsync(new QueryLogFilterDto { MinDurationSeconds = 30 })).Entries);
        Assert.Single((await servis.QueryAsync(new QueryLogFilterDto { SiteName = "site-a" })).Entries);
        Assert.Single((await servis.QueryAsync(new QueryLogFilterDto { Target = "db2" })).Entries);
        Assert.Single((await servis.QueryAsync(new QueryLogFilterDto { RemoteOnly = true })).Entries);
        Assert.Single((await servis.QueryAsync(new QueryLogFilterDto { Search = "yavas" })).Entries);
        Assert.Equal(2, (await servis.QueryAsync(new QueryLogFilterDto())).Entries.Count);

        // Büyük/küçük harf duyarsız olmalı: kullanıcı "SITE-A" yazınca boş sonuç almamalı.
        Assert.Single((await servis.QueryAsync(new QueryLogFilterDto { SiteName = "SITE-A" })).Entries);
        Assert.Single((await servis.QueryAsync(new QueryLogFilterDto { Search = "YAVAS" })).Entries);
    }

    [Fact]
    public async Task Saklama_suresini_asan_gunler_silinir()
    {
        await servis.RecordSampleAsync(Anlik(Sorgu("a", "SELECT 1", 1)));
        await servis.RecordSampleAsync(Anlik());

        var klasor = Path.Combine(dataRoot, "logs", "db-queries");
        var eski = Path.Combine(klasor, $"{DateTime.UtcNow.Date.AddDays(-(QueryLogService.RetentionDays + 2)):yyyy-MM-dd}.ndjson");
        await File.WriteAllTextAsync(eski, "{}\n");

        Assert.Equal(1, await servis.PurgeExpiredAsync());
        Assert.False(File.Exists(eski));
        // Bugünün dosyası DURMALI.
        Assert.True(File.Exists(Path.Combine(klasor, $"{DateTime.UtcNow.Date:yyyy-MM-dd}.ndjson")));
    }

    // --- düzenek ---

    [Fact]
    public async Task Truncated_snapshot_does_not_mark_unseen_queries_as_finished()
    {
        await servis.RecordSampleAsync(Anlik(Sorgu("a", "SELECT active", 3), Sorgu("b", "SELECT other", 7)));
        var truncated = new ResourceConsumerSnapshotDto
        {
            Source = benzersiz, Installed = true, Available = true, TotalCount = 2,
            Consumers = [Sorgu("b", "SELECT other", 9)]
        };
        Assert.Equal(0, await servis.RecordSampleAsync(truncated));
        Assert.Empty((await servis.QueryAsync(new QueryLogFilterDto())).Entries);
        Assert.Equal(2, await servis.RecordSampleAsync(Anlik()));
    }

    [Fact]
    public async Task Cancelled_sample_preserves_active_queries_for_next_valid_sample()
    {
        await servis.RecordSampleAsync(Anlik(Sorgu("a", "SELECT retained", 3)));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            servis.RecordSampleAsync(Anlik(), cancelled.Token));
        Assert.Equal(1, await servis.RecordSampleAsync(Anlik()));
        Assert.Equal("SELECT retained", Assert.Single((await servis.QueryAsync(new QueryLogFilterDto())).Entries).Statement);
    }

    [Fact]
    public async Task Repeated_partial_samples_do_not_accumulate_unverifiable_queries()
    {
        for (var i = 0; i < 30; i++)
        {
            Assert.Equal(0, await servis.RecordSampleAsync(new ResourceConsumerSnapshotDto
            {
                Source = benzersiz, Installed = true, Available = true, TotalCount = 201,
                Consumers = [Sorgu(i.ToString(), "SELECT omitted_by_limit", 3)]
            }));
        }
        Assert.Equal(0, await servis.RecordSampleAsync(Anlik()));
        Assert.Empty((await servis.QueryAsync(new QueryLogFilterDto())).Entries);
    }

    [Fact]
    public async Task Separate_data_roots_do_not_consume_each_others_active_queries()
    {
        var other = new QueryLogService(
            Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = Path.Combine(dataRoot, "other") }),
            NullLogger<QueryLogService>.Instance);
        await servis.RecordSampleAsync(Anlik(Sorgu("a", "SELECT private", 3)));
        Assert.Equal(0, await other.RecordSampleAsync(Anlik()));
        Assert.Empty((await other.QueryAsync(new QueryLogFilterDto())).Entries);
        Assert.Equal(1, await servis.RecordSampleAsync(Anlik()));
    }

    [Fact]
    public async Task New_service_scope_for_same_root_retains_active_queries()
    {
        await servis.RecordSampleAsync(Anlik(Sorgu("a", "SELECT shared", 3)));
        var nextScope = new QueryLogService(
            Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = dataRoot }),
            NullLogger<QueryLogService>.Instance);
        Assert.Equal(1, await nextScope.RecordSampleAsync(Anlik()));
        Assert.Single((await servis.QueryAsync(new QueryLogFilterDto())).Entries);
    }

    private ResourceConsumerSnapshotDto Anlik(params ResourceConsumerDto[] tuketiciler) => Anlik(true, tuketiciler);

    private ResourceConsumerSnapshotDto Anlik(bool okunabildi, params ResourceConsumerDto[] tuketiciler) => new()
    {
        // Kaynak adı testler arasında benzersiz: servis açık sorguları SÜREÇ GENELİNDE tutar
        // (kasıtlı — kapsamlar arasında paylaşılır), o yüzden testler birbirine karışmamalı.
        Source = benzersiz,
        Installed = true,
        Available = okunabildi,
        CapturedAtUtc = DateTime.UtcNow,
        TotalCount = tuketiciler.Length,
        Consumers = tuketiciler
    };

    private static ResourceConsumerDto Sorgu(
        string baglantiId,
        string metin,
        double sure,
        string site = "ornek-site",
        string db = "ornek-db",
        bool uzak = false) => new()
    {
        ResourceType = "database",
        SiteName = site,
        Target = db,
        User = "kullanici",
        ClientHost = uzak ? "203.0.113.5" : "localhost",
        DurationSeconds = sure,
        Statement = metin,
        ConnectionId = baglantiId,
        RemoteClient = uzak
    };
}
