using System.Globalization;
using AdaPanel.Core.Dtos.Seo;
using AdaPanel.Infrastructure.Statistics;

namespace AdaPanel.Infrastructure.Seo;

/// <summary>
/// W3C günlük satırlarından ARAMA MOTORU DAVRANIŞINI çıkarır.
/// </summary>
/// <remarks>
/// <para>
/// <b>Ayrıştırma burada YOKTUR</b> — satırları <see cref="W3cLogReader"/> üretir (panelin tek W3C
/// ayrıştırıcısı, <c>WebStatisticsService</c> ile ortak). Bu sınıfın işi yalnız TOPLAMAKTIR.
/// </para>
/// <para>
/// <b>Neden ziyaretçi istatistiğinden ayrı bir toplayıcı:</b> ölçülen şey farklı. İstatistik
/// ekranı "kaç kişi geldi"yi sorar ve botları AYIRMAZ; burada sorulan "arama motoru neyi, ne
/// sıklıkla ve hangi cevapla gezdi"dir. Aynı toplayıcıya iki soruyu birden yüklemek, iki ekranın
/// da sayısını bulanıklaştırırdı.
/// </para>
/// <para>
/// <b>Bellek:</b> hiçbir yerde satır listesi biriktirilmez; yalnız sözlükler tutulur ve onlar da
/// <see cref="SeoLogLimits.MaxDistinctPaths"/> ile sınırlıdır.
/// </para>
/// </remarks>
public sealed class SeoLogAnalyzer(DateTime cutoffUtc)
{
    private readonly Dictionary<string, EngineStats> engines = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PathStats> botPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> dailyBot = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> wasteRedirect = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> wasteNotFound = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> wasteServerError = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> wasteBlocked = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, QueryStats> queryVariants = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, BotIpStats> botIps = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Pencereye giren TÜM istekler (bot + insan).</summary>
    public long TotalRequests { get; private set; }

    /// <summary>Bunların bot olanları.</summary>
    public long BotRequests { get; private set; }

    /// <summary>Sözlüklerden biri sınıra dayandı mı — rapor "tam değil" demek zorunda.</summary>
    public bool Saturated { get; private set; }

    /// <summary>
    /// Tek bir günlük satırını işler.
    /// </summary>
    public void Add(W3cLogRecord record)
    {
        // TARİH SÜZGECİ ÖNCE: dosya son yazma zamanına göre seçildi, ama tek bir dosya birden çok
        // günün satırını taşıyabilir (yönetici döndürmeyi haftalık/aylık yapmış olabilir).
        // Süzgeç olmasaydı "7 günlük analiz" sessizce bir ayı kapsardı.
        var date = record.Get("date");
        if (!TryParseDate(date, out var gun) || gun < cutoffUtc.Date)
        {
            return;
        }

        TotalRequests++;

        var engine = SeoBotCatalog.Match(record.Get("cs(User-Agent)"));
        if (engine is null)
        {
            return;
        }

        BotRequests++;

        var path = NormalizePath(record.Get("cs-uri-stem"));
        var query = record.Get("cs-uri-query");
        var status = ParseStatus(record.Get("sc-status"));
        var an = BuildTimestamp(gun, record.Get("time"));

        Increment(dailyBot, date);

        var motor = Get(engines, engine.Key, () => new EngineStats(engine));
        motor.Requests++;
        motor.AddStatus(status);
        motor.Days.Add(date);
        if (an > motor.LastSeenUtc)
        {
            motor.LastSeenUtc = an;
        }

        if (path.Length > 0 && botPaths.Count < SeoLogLimits.MaxDistinctPaths)
        {
            var yol = Get(botPaths, path, () => new PathStats(path));
            yol.Hits++;
            yol.LastStatus = status;
            yol.LastSeenUtc = an > yol.LastSeenUtc ? an : yol.LastSeenUtc;
            yol.Engines.TryGetValue(engine.Key, out var mevcut);
            yol.Engines[engine.Key] = mevcut + 1;
            motor.Paths.Add(path);
        }
        else if (path.Length > 0)
        {
            Saturated = true;
        }

        RecordWaste(path, query, status);
        RecordIdentity(record.Get("c-ip"), engine);
    }

    /// <summary>
    /// TARAMA BÜTÇESİ İSRAFININ TEK KARAR YERİ. Kural burada bir kez yazılır; rapor kurma
    /// aşamasında ikinci kez sınıflandırma yapılmaz.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>3xx (yönlendirme):</b> bot iki istek harcayıp bir sayfa görür. Site içi bağlantılar ya da
    /// site haritası eski adresi gösteriyorsa bu her turda tekrarlanır.
    /// </para>
    /// <para>
    /// <b>404/410:</b> bota "bu sayfa yok" demek doğrudan kayıptır — hem bütçe gider hem de o
    /// adrese verilmiş dış bağlantıların değeri boşa çıkar.
    /// </para>
    /// <para>
    /// <b>401/403:</b> bot engelleniyor. <c>robots.txt</c> ile kapatmak yerine sunucudan reddetmek,
    /// motora "hata" gibi görünür.
    /// </para>
    /// <para>
    /// <b>5xx:</b> en pahalısı. Motorlar sürekli 5xx alan siteyi daha SEYREK gezmeye başlar.
    /// </para>
    /// <para>
    /// <b>Parametreli kopya:</b> aynı yolun hem sorgusuz hem çok sayıda farklı sorgulu hâli
    /// geziliyorsa (<c>?sort=</c>, <c>?utm_source=</c>) bot aynı içeriği tekrar tekrar indiriyor
    /// demektir. Kural İKİ koşula birden bağlandı — yolun sorgusuz hâlinin de gezilmiş olması VE
    /// en az iki farklı sorgu görülmesi — çünkü tek bir parametreli adres (bir arama sayfası)
    /// israf değildir.
    /// </para>
    /// </remarks>
    private void RecordWaste(string path, string query, int status)
    {
        if (path.Length == 0)
        {
            return;
        }

        switch (status)
        {
            case >= 300 and < 400:
                Increment(wasteRedirect, path);
                break;
            case 404 or 410:
                Increment(wasteNotFound, path);
                break;
            case 401 or 403:
                Increment(wasteBlocked, path);
                break;
            case >= 500:
                Increment(wasteServerError, path);
                break;
        }

        if (queryVariants.Count >= SeoLogLimits.MaxDistinctPaths)
        {
            Saturated = true;
            return;
        }

        var varyant = Get(queryVariants, path, () => new QueryStats());
        if (string.IsNullOrEmpty(query) || query == "-")
        {
            varyant.PlainHits++;
            return;
        }

        varyant.QueryHits++;
        if (varyant.Queries.Count < 50)
        {
            varyant.Queries.Add(query);
        }
    }

    /// <summary>
    /// Bot İDDİASINDA bulunan IP'leri sayar. <b>Burada hüküm YOKTUR</b> — doğrulama ağ işidir ve
    /// <see cref="SeoBotVerifier"/> tarafından, ayrı bir bütçeyle yapılır. İkisini birleştirmek,
    /// ayrıştırma döngüsünün içine DNS beklemesi koymak olurdu.
    /// </summary>
    private void RecordIdentity(string ip, SeoBotEngine engine)
    {
        if (string.IsNullOrEmpty(ip) || ip == "-" || botIps.Count >= SeoLogLimits.MaxDistinctPaths)
        {
            return;
        }

        var anahtar = ip + "|" + engine.Key;
        var kayit = Get(botIps, anahtar, () => new BotIpStats(ip, engine));
        kayit.Requests++;
    }

    /// <summary>Doğrulayıcıya verilecek adaylar: en çok istek atandan başlayarak.</summary>
    public IReadOnlyList<SeoBotClaim> BuildClaims() =>
        botIps.Values
            .OrderByDescending(item => item.Requests)
            .Select(item => new SeoBotClaim(item.Ip, item.Engine, item.Requests))
            .ToArray();

    /// <summary>
    /// Ölçümleri rapora dönüştürür.
    /// </summary>
    /// <param name="knownPaths">
    /// Panelin KENDİ SEO taramasından bilinen sayfa yolları. Boşsa "hiç gezilmemiş sayfa" listesi
    /// de boş kalır — çünkü karşılaştırılacak bir gerçek yoktur, ve boş listeyi "her sayfa
    /// geziliyor" diye okutmamak için rapor kaynağı ayrıca bildirir.
    /// </param>
    public SeoLogReportDto Build(IReadOnlyCollection<string> knownPaths)
    {
        var israf = BuildWaste();

        return new SeoLogReportDto
        {
            HasData = TotalRequests > 0,
            TotalRequests = TotalRequests,
            BotRequests = BotRequests,
            Engines = engines.Values
                .OrderByDescending(item => item.Requests)
                .Select(item => item.ToDto())
                .ToArray(),
            DailyBotRequests = dailyBot
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new SeoLogDayDto { Date = pair.Key, Requests = pair.Value })
                .ToArray(),
            TopBotPaths = botPaths.Values
                .OrderByDescending(item => item.Hits)
                .Take(SeoLogLimits.TopCount)
                .Select(item => item.ToDto())
                .ToArray(),
            // BOTA DÖNEN HATALAR ayrı liste: "en çok gezilen" listesinde 404'ler popüler
            // olmadıkları için hiç görünmezdi, oysa SEO açısından en acil kalem onlar.
            ErrorPaths = botPaths.Values
                .Where(item => item.LastStatus >= 400)
                .OrderByDescending(item => item.LastStatus >= 500)
                .ThenByDescending(item => item.Hits)
                .Take(SeoLogLimits.TopCount)
                .Select(item => item.ToDto())
                .ToArray(),
            Waste = israf,
            WastedRequests = israf.Sum(item => item.Hits),
            UncrawledPaths = BuildUncrawled(knownPaths),
            KnownPageCount = knownPaths.Count
        };
    }

    private IReadOnlyCollection<SeoLogWasteDto> BuildWaste()
    {
        var kalemler = new List<SeoLogWasteDto>();

        Ekle(wasteServerError, "server_error");
        Ekle(wasteNotFound, "not_found");
        Ekle(wasteBlocked, "blocked");
        Ekle(wasteRedirect, "redirect");

        foreach (var pair in queryVariants)
        {
            var stats = pair.Value;
            if (stats.PlainHits == 0 || stats.Queries.Count < 2)
            {
                continue;
            }

            kalemler.Add(new SeoLogWasteDto
            {
                Reason = "query_duplicate",
                Path = pair.Key,
                Hits = stats.QueryHits,
                // ÖRNEK sorgular ham veridir ve çevrilmez: kullanıcı hangi parametrenin kopya
                // ürettiğini ancak kendi adresini görerek anlar.
                Detail = string.Join(", ", stats.Queries.Take(3))
            });
        }

        // SIRALAMA ÖNCE ACİLİYETE, SONRA SAYIYA. Yalnız sayıya göre sıralamak, tek isabetli bir
        // 5xx'i binlerce yönlendirmenin altına gömerdi — oysa sürekli 5xx alan siteyi motorlar
        // daha SEYREK gezmeye başlar ve o kalem en pahalısıdır.
        return kalemler
            .OrderBy(item => Aciliyet(item.Reason))
            .ThenByDescending(item => item.Hits)
            .Take(SeoLogLimits.TopCount)
            .ToArray();

        /// <summary>İsraf sebeplerinin ACİLİYET SIRASI — tek karar yeri.</summary>
        static int Aciliyet(string sebep) => sebep switch
        {
            "server_error" => 0,
            "not_found" => 1,
            "blocked" => 2,
            "redirect" => 3,
            _ => 4
        };

        void Ekle(Dictionary<string, long> kaynak, string sebep)
        {
            foreach (var pair in kaynak)
            {
                kalemler.Add(new SeoLogWasteDto { Reason = sebep, Path = pair.Key, Hits = pair.Value });
            }
        }
    }

    /// <summary>
    /// Bilinen ama HİÇ GEZİLMEMİŞ sayfalar. Karşılaştırma normalleştirilmiş yol üzerinden yapılır;
    /// aksi hâlde <c>/Hakkimizda</c> ile <c>/hakkimizda/</c> iki ayrı sayfa sanılır ve liste
    /// gerçekte gezilmiş sayfalarla dolardı.
    /// </summary>
    private IReadOnlyCollection<string> BuildUncrawled(IReadOnlyCollection<string> knownPaths)
    {
        if (knownPaths.Count == 0)
        {
            return [];
        }

        var gezilen = new HashSet<string>(botPaths.Keys, StringComparer.OrdinalIgnoreCase);

        return knownPaths
            .Select(NormalizePath)
            .Where(path => path.Length > 0 && !gezilen.Contains(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Take(SeoLogLimits.TopCount * 4)
            .ToArray();
    }

    /// <summary>
    /// Yol normalleştirmesi — <b>tek yerde</b>. Hem günlük satırı hem SEO taraması buradan geçer,
    /// yoksa iki taraf aynı sayfayı farklı yazıp karşılaştırma yalan söylerdi.
    /// </summary>
    /// <remarks>
    /// Sorgu dizesi AYRILIR (IIS zaten ayrı sütuna yazar ama tarama raporundaki yol taşıyabilir),
    /// sondaki eğik çizgi kök dışında atılır. Büyük/küçük harf DÖNÜŞTÜRÜLMEZ: sözlükler zaten
    /// harf duyarsız karşılaştırıyor ve kullanıcıya adresini kendi yazdığı gibi göstermek gerekir.
    /// </remarks>
    internal static string NormalizePath(string raw)
    {
        if (string.IsNullOrEmpty(raw) || raw == "-")
        {
            return string.Empty;
        }

        var yol = raw;
        var soru = yol.IndexOf('?', StringComparison.Ordinal);
        if (soru >= 0)
        {
            yol = yol[..soru];
        }

        yol = yol.TrimEnd();
        if (yol.Length > 1 && yol.EndsWith('/'))
        {
            yol = yol.TrimEnd('/');
            if (yol.Length == 0)
            {
                return "/";
            }
        }

        return yol;
    }

    /// <summary>
    /// IIS <c>date</c> alanı ISO <c>yyyy-MM-dd</c>'dir ve <b>InvariantCulture</b> ile ayrıştırılır:
    /// Türkçe locale'de de aynı sonucu vermesi gerekir (kalıp: <c>WebStatisticsService</c>).
    /// </summary>
    private static bool TryParseDate(string value, out DateTime date)
    {
        date = default;
        return !string.IsNullOrEmpty(value) &&
               DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }

    /// <summary>
    /// Gün + <c>HH:mm:ss</c> saatinden UTC an kurar. Saat okunamazsa günün başı kullanılır — bir
    /// satır yüzünden analizi düşürmek yerine, o satırın "ne zaman"ını kabaca almak yeterlidir.
    /// </summary>
    private static DateTime BuildTimestamp(DateTime day, string time)
    {
        if (TimeSpan.TryParseExact(time, @"hh\:mm\:ss", CultureInfo.InvariantCulture, out var saat))
        {
            return DateTime.SpecifyKind(day.Add(saat), DateTimeKind.Utc);
        }

        return DateTime.SpecifyKind(day, DateTimeKind.Utc);
    }

    private static int ParseStatus(string value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var status) ? status : 0;

    private static void Increment(Dictionary<string, long> map, string key)
    {
        if (map.TryGetValue(key, out var mevcut))
        {
            map[key] = mevcut + 1;
        }
        else if (map.Count < SeoLogLimits.MaxDistinctPaths)
        {
            map[key] = 1;
        }
    }

    private static TValue Get<TValue>(Dictionary<string, TValue> map, string key, Func<TValue> factory)
    {
        if (map.TryGetValue(key, out var mevcut))
        {
            return mevcut;
        }

        var yeni = factory();
        map[key] = yeni;
        return yeni;
    }

    private sealed class EngineStats(SeoBotEngine engine)
    {
        public long Requests;
        public DateTime LastSeenUtc;
        private long status2xx;
        private long status3xx;
        private long status4xx;
        private long status5xx;

        public HashSet<string> Days { get; } = new(StringComparer.Ordinal);

        public HashSet<string> Paths { get; } = new(StringComparer.OrdinalIgnoreCase);

        public void AddStatus(int status)
        {
            switch (status)
            {
                case >= 200 and < 300: status2xx++; break;
                case >= 300 and < 400: status3xx++; break;
                case >= 400 and < 500: status4xx++; break;
                case >= 500: status5xx++; break;
            }
        }

        public SeoLogEngineDto ToDto() => new()
        {
            Key = engine.Key,
            Category = engine.Category.ToString().ToLowerInvariant(),
            Requests = Requests,
            UniquePaths = Paths.Count,
            Status2xx = status2xx,
            Status3xx = status3xx,
            Status4xx = status4xx,
            Status5xx = status5xx,
            LastSeenUtc = LastSeenUtc,
            ActiveDays = Days.Count
        };
    }

    private sealed class PathStats(string path)
    {
        public long Hits;
        public int LastStatus;
        public DateTime LastSeenUtc;

        public Dictionary<string, long> Engines { get; } = new(StringComparer.Ordinal);

        public SeoLogPathDto ToDto() => new()
        {
            Path = path,
            Hits = Hits,
            LastStatus = LastStatus,
            LastSeenUtc = LastSeenUtc,
            TopEngine = Engines.Count == 0
                ? string.Empty
                : Engines.OrderByDescending(pair => pair.Value).First().Key
        };
    }

    private sealed class QueryStats
    {
        public long PlainHits;
        public long QueryHits;

        public HashSet<string> Queries { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class BotIpStats(string ip, SeoBotEngine engine)
    {
        public long Requests;

        public string Ip => ip;

        public SeoBotEngine Engine => engine;
    }
}
