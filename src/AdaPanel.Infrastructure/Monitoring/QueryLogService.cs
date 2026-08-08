using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AdaPanel.Core.Dtos.Monitoring;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AdaPanel.Infrastructure.Monitoring;

/// <inheritdoc />
public sealed partial class QueryLogService(
    IOptions<PanelRuntimeOptions> runtimeOptions,
    ILogger<QueryLogService> logger) : IQueryLogService
{
    /// <summary>Kayıtlar kaç gün saklanır. Kullanıcı isteği: en fazla bir ay.</summary>
    public const int RetentionDays = 30;

    /// <summary>Örnekleme aralığı — <see cref="QueryLogWorker"/> ile AYNI olmalı, ekranda gösterilir.</summary>
    public const int SampleIntervalSeconds = 5;

    /// <summary>Bir satırda saklanan en fazla sorgu metni. Uzun metin dosyayı şişirir, okunmaz da.</summary>
    private const int MaxStatementLength = 600;

    /// <summary>Tek bir gün dosyasından okunacak en fazla satır — bozuk/şişmiş dosyada bellek koruması.</summary>
    private const int MaxLinesPerDay = 200_000;

    private readonly string logRoot = Path.Combine(
        Path.GetFullPath(Environment.ExpandEnvironmentVariables(runtimeOptions.Value.DataRoot)),
        "logs",
        "db-queries");

    /// <summary>
    /// Bir önceki turda AÇIK olan sorgular: anahtar → son görülen hâli.
    /// <para>
    /// Süreç içi tutulur; panel yeniden başlarsa o an açık olan sorgular kaydedilmeden düşer. Bunu
    /// diske yazmak, her örnekleme turunda ek G/Ç demekti ve kaybedilen şey (yeniden başlatma anında
    /// süren sorgular) bu maliyeti hak etmiyor.
    /// </para>
    /// </summary>
    private static readonly ConcurrentDictionary<string, AcikSorgu> Acik = new(StringComparer.Ordinal);

    /// <summary>Aynı dosyaya iki tur birden yazmasın.</summary>
    private static readonly SemaphoreSlim YazmaKilidi = new(1, 1);

    private static readonly JsonSerializerOptions JsonAyar = new() { WriteIndented = false };

    public async Task<int> RecordSampleAsync(ResourceConsumerSnapshotDto snapshot, CancellationToken cancellationToken = default)
    {
        // Liste OKUNAMADIYSA hiçbir şey yapma. Aksi hâlde okunamayan tur "hepsi bitti" sayılır ve
        // hâlâ çalışan sorgular günlüğe bitmiş gibi yazılırdı — ölçememeyi ölçüm saymak.
        if (snapshot is null || !snapshot.Available)
        {
            return 0;
        }

        var kaynak = (snapshot.Source ?? string.Empty).Trim().ToLowerInvariant();
        var simdi = DateTime.UtcNow;

        var buTur = new Dictionary<string, ResourceConsumerDto>(StringComparer.Ordinal);
        foreach (var tuketici in snapshot.Consumers.Where(item =>
                     string.Equals(item.ResourceType, "database", StringComparison.OrdinalIgnoreCase)))
        {
            buTur[Anahtar(kaynak, tuketici)] = tuketici;
        }

        // 1) Açık kalanları güncelle / yenileri ekle.
        foreach (var (anahtar, tuketici) in buTur)
        {
            Acik.AddOrUpdate(
                anahtar,
                _ => new AcikSorgu(simdi, tuketici),
                (_, onceki) => onceki with { Son = tuketici });
        }

        // 2) Bu kaynağa ait olup ARTIK LİSTEDE OLMAYANLAR bitmiştir.
        var bitenler = new List<QueryLogEntryDto>();
        foreach (var anahtar in Acik.Keys.Where(item => item.StartsWith(kaynak + "", StringComparison.Ordinal)).ToArray())
        {
            if (buTur.ContainsKey(anahtar) || !Acik.TryRemove(anahtar, out var acik))
            {
                continue;
            }

            bitenler.Add(Kayda(acik, kaynak, simdi));
        }

        if (bitenler.Count == 0)
        {
            return 0;
        }

        await YazAsync(bitenler, cancellationToken);
        return bitenler.Count;
    }

    /// <summary>
    /// Bir sorgunun kimliği. Bağlantı kimliği TEK BAŞINA yetmez: motor onu kapanan oturumlardan
    /// geri kullanır, o zaman iki ayrı sorgu tek kayıt sanılır. Metin de katılınca aynı bağlantıda
    /// arka arkaya çalışan farklı sorgular ayrı ayrı kaydedilir.
    /// </summary>
    private static string Anahtar(string kaynak, ResourceConsumerDto tuketici)
        => string.Join('', kaynak, tuketici.ConnectionId ?? string.Empty, Kirp(tuketici.Statement));

    private static QueryLogEntryDto Kayda(AcikSorgu acik, string kaynak, DateTime simdi) => new()
    {
        FirstSeenUtc = acik.IlkGorulme,
        EndedAtUtc = simdi,
        // Motorun bildirdiği süreyi kullanıyoruz, örnekleme farkını DEĞİL: sorgu panel onu ilk
        // görmeden önce başlamış olabilir ve o durumda kendi hesabımız gerçeğin altında kalırdı.
        DurationSeconds = (int)Math.Max(0, acik.Son.DurationSeconds),
        Source = kaynak,
        SiteName = acik.Son.SiteName ?? string.Empty,
        Target = acik.Son.Target ?? string.Empty,
        User = acik.Son.User ?? string.Empty,
        ClientHost = acik.Son.ClientHost ?? string.Empty,
        Statement = Kirp(acik.Son.Statement),
        Redacted = acik.Son.Redacted,
        RemoteClient = acik.Son.RemoteClient,
        OwnerName = acik.Son.OwnerName ?? string.Empty
    };

    private static string Kirp(string? value)
    {
        var kirpilmis = (value ?? string.Empty).Trim();
        return kirpilmis.Length <= MaxStatementLength ? kirpilmis : kirpilmis[..MaxStatementLength] + "…";
    }

    private async Task YazAsync(IReadOnlyCollection<QueryLogEntryDto> kayitlar, CancellationToken cancellationToken)
    {
        await YazmaKilidi.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(logRoot);

            // Günlere göre grupla: bir tur gece yarısını geçmiş olabilir.
            foreach (var gun in kayitlar.GroupBy(item => item.EndedAtUtc.Date))
            {
                var yol = DosyaYolu(gun.Key);
                var govde = new StringBuilder();
                foreach (var kayit in gun)
                {
                    govde.Append(JsonSerializer.Serialize(kayit, JsonAyar)).Append('\n');
                }

                await File.AppendAllTextAsync(yol, govde.ToString(), Encoding.UTF8, cancellationToken);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Günlük yazılamazsa panelin izleme akışı DURMAZ; sessiz de geçmez.
            logger.LogWarning(exception, "Sorgu geçmişi yazılamadı ({Adet} kayıt).", kayitlar.Count);
        }
        finally
        {
            YazmaKilidi.Release();
        }
    }

    private string DosyaYolu(DateTime gun)
        => Path.Combine(logRoot, $"{gun:yyyy-MM-dd}.ndjson");

    public Task<QueryLogPageDto> QueryAsync(QueryLogFilterDto filter, CancellationToken cancellationToken = default)
    {
        filter ??= new QueryLogFilterDto();
        var sayfa = new QueryLogPageDto
        {
            RetentionDays = RetentionDays,
            SampleIntervalSeconds = SampleIntervalSeconds,
            AvailableDays = MevcutGunler()
        };

        var gunler = OkunacakGunler(filter);
        var eslesenler = new List<QueryLogEntryDto>();

        foreach (var gun in gunler)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var kayit in GunuOku(gun))
            {
                if (Uyuyor(kayit, filter))
                {
                    eslesenler.Add(kayit);
                }
            }
        }

        // En yeni önce.
        eslesenler.Sort((a, b) => b.EndedAtUtc.CompareTo(a.EndedAtUtc));

        sayfa.TotalCount = eslesenler.Count;
        sayfa.TopShapes = Ozetle(eslesenler);
        sayfa.Entries = eslesenler
            .Skip(Math.Max(0, filter.Skip))
            .Take(Math.Clamp(filter.Take, 1, 500))
            .ToArray();

        return Task.FromResult(sayfa);
    }

    private IReadOnlyCollection<string> MevcutGunler()
    {
        try
        {
            if (!Directory.Exists(logRoot))
            {
                return [];
            }

            return new DirectoryInfo(logRoot)
                .GetFiles("*.ndjson")
                .Select(file => Path.GetFileNameWithoutExtension(file.Name))
                .Where(ad => DateTime.TryParseExact(ad, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                .OrderByDescending(ad => ad, StringComparer.Ordinal)
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private IEnumerable<DateTime> OkunacakGunler(QueryLogFilterDto filter)
    {
        var bas = (filter.FromUtc ?? DateTime.UtcNow.Date.AddDays(-(RetentionDays - 1))).Date;
        var son = (filter.ToUtc ?? DateTime.UtcNow.Date).Date;
        if (son < bas)
        {
            (bas, son) = (son, bas);
        }

        for (var gun = bas; gun <= son; gun = gun.AddDays(1))
        {
            yield return gun;
        }
    }

    private IEnumerable<QueryLogEntryDto> GunuOku(DateTime gun)
    {
        var yol = DosyaYolu(gun);
        if (!File.Exists(yol))
        {
            yield break;
        }

        var okunan = 0;
        IEnumerator<string>? satirlar = null;
        try
        {
            satirlar = File.ReadLines(yol).GetEnumerator();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Sorgu geçmişi okunamadı: {Yol}", yol);
            yield break;
        }

        using (satirlar)
        {
            while (okunan < MaxLinesPerDay)
            {
                string satir;
                try
                {
                    if (!satirlar.MoveNext())
                    {
                        break;
                    }

                    satir = satirlar.Current;
                }
                catch (IOException)
                {
                    break;
                }

                okunan++;
                if (string.IsNullOrWhiteSpace(satir))
                {
                    continue;
                }

                QueryLogEntryDto? kayit = null;
                try
                {
                    kayit = JsonSerializer.Deserialize<QueryLogEntryDto>(satir);
                }
                catch (JsonException)
                {
                    // Yarım yazılmış son satır (süreç kapanmışsa olabilir) tüm günü çöpe atmasın.
                }

                if (kayit is not null)
                {
                    yield return kayit;
                }
            }
        }
    }

    private static bool Uyuyor(QueryLogEntryDto kayit, QueryLogFilterDto filter)
    {
        if (filter.FromUtc is { } bas && kayit.EndedAtUtc < bas.Date)
        {
            return false;
        }

        if (filter.ToUtc is { } son && kayit.EndedAtUtc > son.Date.AddDays(1).AddTicks(-1))
        {
            return false;
        }

        if (kayit.DurationSeconds < Math.Max(0, filter.MinDurationSeconds))
        {
            return false;
        }

        if (filter.RemoteOnly && !kayit.RemoteClient)
        {
            return false;
        }

        return Esit(filter.Source, kayit.Source)
               && Esit(filter.SiteName, kayit.SiteName)
               && Esit(filter.Target, kayit.Target)
               && Esit(filter.User, kayit.User)
               && Iceriyor(filter.Search, kayit.Statement);

        static bool Esit(string? filtre, string? deger)
            => string.IsNullOrWhiteSpace(filtre) || string.Equals(filtre.Trim(), deger ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        static bool Iceriyor(string? filtre, string? deger)
            => string.IsNullOrWhiteSpace(filtre) || (deger ?? string.Empty).Contains(filtre.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyCollection<QueryLogSummaryRowDto> Ozetle(IReadOnlyCollection<QueryLogEntryDto> kayitlar)
        => kayitlar
            .GroupBy(kayit => Sekil(kayit.Statement), StringComparer.Ordinal)
            .Select(grup => new QueryLogSummaryRowDto
            {
                Shape = grup.Key,
                Count = grup.Count(),
                TotalSeconds = grup.Sum(item => (long)item.DurationSeconds),
                MaxSeconds = grup.Max(item => item.DurationSeconds),
                SiteName = grup
                    .Where(item => !string.IsNullOrWhiteSpace(item.SiteName))
                    .GroupBy(item => item.SiteName, StringComparer.OrdinalIgnoreCase)
                    .OrderByDescending(site => site.Count())
                    .Select(site => site.Key)
                    .FirstOrDefault() ?? string.Empty,
                Sample = grup.OrderByDescending(item => item.DurationSeconds).First().Statement
            })
            // Sunucuyu en çok meşgul eden önce: toplam süre, sonra adet.
            .OrderByDescending(row => row.TotalSeconds)
            .ThenByDescending(row => row.Count)
            .Take(25)
            .ToArray();

    /// <summary>
    /// Sorgunun DEĞİŞMEZLERİNİ temizleyip "şekil"e indirger.
    /// <para>
    /// Bu olmadan <c>WHERE id=1</c> ile <c>WHERE id=2</c> ayrı sorgu sayılır, hiçbir şey öne çıkmaz
    /// ve "hangi sorgu çok çalışmış" sorusu cevapsız kalırdı.
    /// </para>
    /// </summary>
    internal static string Sekil(string? statement)
    {
        var metin = (statement ?? string.Empty).Trim();
        if (metin.Length == 0)
        {
            return string.Empty;
        }

        metin = TekTirnakli().Replace(metin, "?");
        metin = CiftTirnakli().Replace(metin, "?");
        metin = Sayilar().Replace(metin, "?");
        metin = FazlaBosluk().Replace(metin, " ");
        return metin.Trim();
    }

    public Task<int> PurgeExpiredAsync(CancellationToken cancellationToken = default)
    {
        var silinen = 0;
        try
        {
            if (!Directory.Exists(logRoot))
            {
                return Task.FromResult(0);
            }

            var sinir = DateTime.UtcNow.Date.AddDays(-RetentionDays);
            foreach (var dosya in new DirectoryInfo(logRoot).GetFiles("*.ndjson"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var ad = Path.GetFileNameWithoutExtension(dosya.Name);
                if (!DateTime.TryParseExact(ad, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var gun))
                {
                    continue;
                }

                if (gun < sinir)
                {
                    dosya.Delete();
                    silinen++;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Sorgu geçmişi temizliği tamamlanamadı.");
        }

        return Task.FromResult(silinen);
    }

    [GeneratedRegex(@"'(?:[^']|'')*'", RegexOptions.None, matchTimeoutMilliseconds: 250)]
    private static partial Regex TekTirnakli();

    [GeneratedRegex(@"""(?:[^""]|"""")*""", RegexOptions.None, matchTimeoutMilliseconds: 250)]
    private static partial Regex CiftTirnakli();

    [GeneratedRegex(@"\b\d+(\.\d+)?\b", RegexOptions.None, matchTimeoutMilliseconds: 250)]
    private static partial Regex Sayilar();

    [GeneratedRegex(@"\s+", RegexOptions.None, matchTimeoutMilliseconds: 250)]
    private static partial Regex FazlaBosluk();

    private readonly record struct AcikSorgu(DateTime IlkGorulme, ResourceConsumerDto Son);
}
