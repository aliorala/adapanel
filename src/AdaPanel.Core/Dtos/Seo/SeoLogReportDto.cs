namespace AdaPanel.Core.Dtos.Seo;

/// <summary>Tek bir arama motorunun ölçülen tarama davranışı.</summary>
public sealed class SeoLogEngineDto
{
    /// <summary>Dile bağımsız motor anahtarı (<c>google</c>, <c>bing</c>, <c>other</c>).</summary>
    public string Key { get; init; } = string.Empty;

    /// <summary><c>search</c> | <c>ai</c> | <c>tool</c> — ekranda gruplama için.</summary>
    public string Category { get; init; } = string.Empty;

    /// <summary>Pencere içinde bu motordan gelen toplam istek.</summary>
    public long Requests { get; init; }

    /// <summary>Bu motorun DOKUNDUĞU ayrı sayfa sayısı — "ne kadar geziyor"un asıl ölçüsü.</summary>
    public int UniquePaths { get; init; }

    public long Status2xx { get; init; }

    public long Status3xx { get; init; }

    public long Status4xx { get; init; }

    public long Status5xx { get; init; }

    /// <summary>Bu motorun son görüldüğü an (UTC). Hiç görülmediyse alan zaten oluşmaz.</summary>
    public DateTime LastSeenUtc { get; init; }

    /// <summary>Bu motorun görüldüğü ayrı gün sayısı — "her gün mü, bir kez mi" sorusunun cevabı.</summary>
    public int ActiveDays { get; init; }
}

/// <summary>Gün bazlı bot isabet sayacı (grafik için).</summary>
public sealed class SeoLogDayDto
{
    /// <summary><c>yyyy-MM-dd</c> — günlükteki HAM tarih alanı, UTC.</summary>
    public string Date { get; init; } = string.Empty;

    public long Requests { get; init; }
}

/// <summary>Botun gezdiği tek bir adres ve ölçülen sonucu.</summary>
public sealed class SeoLogPathDto
{
    /// <summary>Yol (<c>cs-uri-stem</c>). Sorgu dizesi AYRI alandadır.</summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>Bu yola bot tarafından yapılan istek sayısı.</summary>
    public long Hits { get; init; }

    /// <summary>Bu yolda EN SON görülen HTTP durum kodu.</summary>
    public int LastStatus { get; init; }

    /// <summary>Bu yolu en çok gezen motorun anahtarı.</summary>
    public string TopEngine { get; init; } = string.Empty;

    public DateTime LastSeenUtc { get; init; }
}

/// <summary>
/// TARAMA BÜTÇESİ İSRAFI: botun harcadığı ama site sahibine hiçbir şey kazandırmayan istekler.
/// </summary>
/// <remarks>
/// Her kalem dile bağımsız bir <see cref="Reason"/> taşır; cümleyi arayüz kurar
/// (<c>panel.seo.log.waste.{reason}</c>). Sunucunun Türkçe cümle üretmesi, İngilizce oturumdaki
/// kullanıcıya Türkçe rapor göstermek olurdu.
/// </remarks>
public sealed class SeoLogWasteDto
{
    /// <summary>
    /// <c>redirect</c> (3xx), <c>not_found</c> (404/410), <c>server_error</c> (5xx),
    /// <c>blocked</c> (401/403) ya da <c>query_duplicate</c> (aynı sayfanın parametreli kopyası).
    /// </summary>
    public string Reason { get; init; } = string.Empty;

    public string Path { get; init; } = string.Empty;

    public long Hits { get; init; }

    /// <summary>Ölçülen ham değer (HTTP kodu, örnek sorgu dizesi). Çevrilmez.</summary>
    public string Detail { get; init; } = string.Empty;
}

/// <summary>
/// Kullanıcı aracısı bir arama motoru adı taşıyan TEK bir IP hakkındaki hüküm.
/// </summary>
public sealed class SeoLogBotIdentityDto
{
    public string Ip { get; init; } = string.Empty;

    public string EngineKey { get; init; } = string.Empty;

    public long Requests { get; init; }

    /// <summary>
    /// <c>verified</c> — ters DNS motorun alan adıyla bitiyor VE ileri çözüm aynı IP'yi veriyor.
    /// <c>spoofed</c> — ters DNS çözüldü ama motora ait DEĞİL (ya da ileri teyit tutmadı).
    /// <c>unresolved</c> — ters DNS cevap vermedi; hüküm YOK.
    /// <c>unsupported</c> — bu motor kimliğini ters DNS ile doğrulatmıyor; hüküm YOK.
    /// <c>not_checked</c> — doğrulama İSTENMEDİ ya da bütçe dolduğu için bu IP'ye sıra gelmedi.
    /// </summary>
    public string Verdict { get; init; } = string.Empty;

    /// <summary>Ters DNS'ten dönen ad (çözülemediyse boş) — hükmün KANITI.</summary>
    public string ReverseDns { get; init; } = string.Empty;
}

/// <summary>
/// SAHTE BOT DOĞRULAMASININ ÖZETİ ve <b>neyin ölçülmediğinin</b> açık kaydı.
/// </summary>
/// <remarks>
/// <para>
/// <b>Bu blok neden hüküm vermemeyi de raporluyor:</b> "Googlebot" yazan bir kullanıcı aracısı tek
/// başına HİÇBİR ŞEY kanıtlamaz — o başlığı herkes yazabilir. Doğrulamanın tek yolu ters DNS +
/// ileri DNS teyididir ve o da ağ erişimi ister. Panel bunu yapamadığında "doğrulanmadı" der;
/// <b>"sahte" demez.</b> Ölçülmemiş bir şeyi iddia etmek, kullanıcıyı gerçek olmayan bir saldırı
/// için engelleme kuralı yazmaya iterdi.
/// </para>
/// </remarks>
public sealed class SeoLogVerificationDto
{
    /// <summary>Doğrulama çalıştırıldı mı (istenmediyse ya da çözücü yoksa false).</summary>
    public bool Performed { get; init; }

    /// <summary>Ters DNS sorgusu YAPILAN ayrı IP sayısı.</summary>
    public int CheckedIps { get; init; }

    /// <summary>Motora ait olduğu KANITLANAN IP sayısı.</summary>
    public int VerifiedIps { get; init; }

    /// <summary>Ters DNS çözüldüğü hâlde motora ait ÇIKMAYAN IP sayısı — gerçek sahte bot ölçümü.</summary>
    public int SpoofedIps { get; init; }

    /// <summary>Hüküm verilemeyen IP sayısı (çözülemedi, desteklenmiyor ya da sıra gelmedi).</summary>
    public int UndeterminedIps { get; init; }

    /// <summary>Bot iddiasında bulunan ayrı IP sayısı — <see cref="CheckedIps"/> bunun bir alt kümesidir.</summary>
    public int CandidateIps { get; init; }

    /// <summary>Tek tek hükümler (yalnız ilgi çekenler: sahte + doğrulanmış örnekler).</summary>
    public IReadOnlyCollection<SeoLogBotIdentityDto> Identities { get; init; } = [];
}

/// <summary>
/// SEO GÜNLÜK ANALİZİ RAPORU — arama motorlarının siteyi gerçekte nasıl gezdiği.
/// </summary>
/// <remarks>
/// <para>
/// <b>Bu, ziyaretçi istatistiği DEĞİLDİR</b> (o <c>WebStatisticsService</c> / istatistik ekranında).
/// Buradaki tek soru şudur: arama motoru botu siteye ne sıklıkla geliyor, hangi sayfaları geziyor,
/// ne cevap alıyor ve bu emeğin ne kadarı boşa gidiyor.
/// </para>
/// <para>
/// <b>BÜTÇE ALANLARI GİZLENMEZ</b> (<see cref="LinesScanned"/>, <see cref="LimitReached"/>,
/// <see cref="FilesSkipped"/>): kırpılmış bir okumadan çıkan sayıyı "sitenin tamamı" gibi
/// göstermek, panelin en pahalı yalanı olurdu — kullanıcı "Googlebot hiç gelmemiş" diye SEO
/// kararı alırdı.
/// </para>
/// </remarks>
public sealed class SeoLogReportDto
{
    public string SiteName { get; init; } = string.Empty;

    public DateTime GeneratedAtUtc { get; init; }

    public long DurationMs { get; init; }

    /// <summary>Analiz penceresi (gün).</summary>
    public int PeriodDays { get; init; }

    /// <summary>Okunan IIS günlük klasörü — "sayı neden sıfır" sorusunun ilk cevabı.</summary>
    public string LogDirectory { get; init; } = string.Empty;

    /// <summary>Klasör hiç yoksa/okunamadıysa dile bağımsız sebep; doluysa ölçüm YOKTUR.</summary>
    public string Error { get; init; } = string.Empty;

    /// <summary>Pencerede en az bir istek okunabildi mi.</summary>
    public bool HasData { get; init; }

    public int FilesScanned { get; init; }

    /// <summary>Bunların kaçı ZSTD arşiviydi (<c>.log.zst</c>) — log döndürme sonrası önemlidir.</summary>
    public int FilesCompressed { get; init; }

    /// <summary>Bütçe dolduğu için AÇILMAYAN dosya sayısı.</summary>
    public int FilesSkipped { get; init; }

    public long LinesScanned { get; init; }

    /// <summary>Satır/dosya bütçesine dayanıldı mı — TRUE ise rapor pencerenin TAMAMI değildir.</summary>
    public bool LimitReached { get; init; }

    /// <summary>Pencere içindeki TÜM istekler (bot + insan) — bot oranının paydası.</summary>
    public long TotalRequests { get; init; }

    /// <summary>Bunların kaçı bot.</summary>
    public long BotRequests { get; init; }

    public IReadOnlyCollection<SeoLogEngineDto> Engines { get; init; } = [];

    public IReadOnlyCollection<SeoLogDayDto> DailyBotRequests { get; init; } = [];

    /// <summary>Botun EN ÇOK gezdiği sayfalar.</summary>
    public IReadOnlyCollection<SeoLogPathDto> TopBotPaths { get; init; } = [];

    /// <summary>Bota 4xx/5xx dönen sayfalar — doğrudan SEO kaybı.</summary>
    public IReadOnlyCollection<SeoLogPathDto> ErrorPaths { get; init; } = [];

    public IReadOnlyCollection<SeoLogWasteDto> Waste { get; init; } = [];

    /// <summary>Bütçe israfının toplam istek sayısı.</summary>
    public long WastedRequests { get; init; }

    /// <summary>
    /// Sitede VAR OLDUĞU BİLİNEN ama pencerede hiçbir botun uğramadığı adresler.
    /// </summary>
    public IReadOnlyCollection<string> UncrawledPaths { get; init; } = [];

    /// <summary>
    /// "Var olduğu bilinen" bilgisinin KAYNAĞI: panelin kendi SEO taramasının rapor kimliği.
    /// Boşsa hiç tarama yapılmamıştır ve <see cref="UncrawledPaths"/> de boştur — boş listeyi
    /// "her sayfa geziliyor" diye okumak yanlış olurdu, bu yüzden kaynak birlikte gönderilir.
    /// </summary>
    public string UncrawledSourceReportId { get; init; } = string.Empty;

    /// <summary>SEO taramasından bilinen toplam sayfa sayısı (payda).</summary>
    public int KnownPageCount { get; init; }

    public SeoLogVerificationDto Verification { get; init; } = new();
}
