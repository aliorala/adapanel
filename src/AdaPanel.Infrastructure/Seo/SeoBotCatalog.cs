namespace AdaPanel.Infrastructure.Seo;

/// <summary>Botun ne işe yaradığı — ekranda gruplama ve önem sırası buradan gelir.</summary>
public enum SeoBotCategory
{
    /// <summary>Klasik arama motoru tarayıcısı; sitenin arama sonuçlarındaki varlığını O belirler.</summary>
    Search,

    /// <summary>Yapay zekâ arama/eğitim tarayıcısı. Tarama bütçesini yer ama klasik sıralamayı etkilemez.</summary>
    Ai,

    /// <summary>Üçüncü taraf SEO/analiz tarayıcısı (Ahrefs, Semrush). Site sahibine faydası yoktur, bütçesini yer.</summary>
    Tool
}

/// <summary>
/// Bir arama motoru botunun KİMLİĞİ: hangi kullanıcı aracısı dizgeleri ona ait, ve kimliği
/// DOĞRULANABİLİYORSA hangi ters DNS son ekleriyle.
/// </summary>
/// <param name="Key">
/// Dile bağımsız anahtar (<c>google</c>, <c>bing</c>). Arayüz çeviriyi
/// <c>panel.seo.log.engine.{key}</c> ile kurar; burada Türkçe/İngilizce cümle ÜRETİLMEZ.
/// </param>
/// <param name="UserAgentTokens">
/// Kullanıcı aracısında aranan KÜÇÜK HARFLİ parçalar. IIS boşlukları <c>+</c> ile değiştirdiği
/// için (bkz. <see cref="Statistics.W3cLogReader"/>) parçalar boşluk İÇERMEZ — <c>googlebot</c>
/// gibi tek kelimeler seçildi ki <c>+</c> kodlaması eşleşmeyi bozmasın.
/// </param>
/// <param name="ReverseDnsSuffixes">
/// Motorun RESMEN yayımladığı ters DNS son ekleri. <b>Boşsa o motor bu yöntemle doğrulanamaz</b>
/// ve panel onun için asla "sahte" demez (bkz. <see cref="SeoBotVerdict"/>).
/// </param>
public sealed record SeoBotEngine(
    string Key,
    SeoBotCategory Category,
    IReadOnlyList<string> UserAgentTokens,
    IReadOnlyList<string> ReverseDnsSuffixes);

/// <summary>
/// Arama motoru botlarının <b>TEK KAYNAĞI</b>: ad, kullanıcı aracısı imzası ve doğrulama son eki.
/// </summary>
/// <remarks>
/// <para>
/// <b>NEDEN TEK DOSYA:</b> bu depoda "aynı listenin ikinci kopyası" hatası beş kez ısırdı. Bot
/// listesi tam da o türden: ayrıştırıcı, i18n, doğrulayıcı ve ekran aynı adları bilmek zorunda.
/// Liste ikiye bölünseydi yeni bir motor eklendiğinde biri güncellenip diğeri unutulur ve panel o
/// motoru "bilinmeyen bot" diye gösterirdi — üstelik sessizce.
/// </para>
/// <para>
/// <b>DOĞRULAMA YÖNTEMİ (FCrDNS):</b> Google, Bing, Yandex, Baidu ve Apple kendi botlarının
/// doğrulanmasını AÇIKÇA ters DNS + ileri DNS teyidi olarak tarif eder: IP'nin ters kaydı motorun
/// alan adıyla bitmeli, o ana bilgisayar adının ileri çözümü de AYNI IP'yi vermeli. Yalnız ters
/// kayda bakmak yetmez; ters DNS'i saldırgan kendi ağında istediği gibi yazabilir.
/// </para>
/// <para>
/// <b>DOĞRULANAMAYAN MOTORLAR "SAHTE" DEĞİLDİR.</b> DuckDuckGo ve yapay zekâ tarayıcıları kimliği
/// ters DNS ile değil, yayımlanmış IP listeleriyle doğrulatır; panel o listeleri indirmediği için
/// onlar hakkında hüküm VERMEZ. Boş <see cref="SeoBotEngine.ReverseDnsSuffixes"/> "doğrulama
/// desteklenmiyor" demektir ve sonuç <see cref="SeoBotVerdict.Unsupported"/> olur.
/// </para>
/// </remarks>
public static class SeoBotCatalog
{
    /// <summary>Tanınan ama hangi motora ait olduğu bilinmeyen tarayıcılar için anahtar.</summary>
    public const string OtherKey = "other";

    /// <summary>
    /// Bilinen motorlar. Sıra ÖNEMLİ: eşleşme ilk bulunanda durur, bu yüzden daha dar imzalar
    /// (<c>googlebot-image</c>) daha genişlerinden (<c>googlebot</c>) önce yazılmaz — ikisi de
    /// aynı motora gittiği için gerek yok; ayrım gerekseydi sıra ters olurdu.
    /// </summary>
    public static IReadOnlyList<SeoBotEngine> Engines { get; } =
    [
        new("google", SeoBotCategory.Search,
            ["googlebot", "google-inspectiontool", "storebot-google", "googleother", "google-safety"],
            [".googlebot.com", ".google.com", ".googleusercontent.com"]),

        new("bing", SeoBotCategory.Search,
            ["bingbot", "adidxbot", "bingpreview", "msnbot"],
            [".search.msn.com"]),

        new("yandex", SeoBotCategory.Search,
            ["yandexbot", "yandeximages", "yandexmobilebot", "yandexaccessibilitybot"],
            [".yandex.ru", ".yandex.net", ".yandex.com"]),

        new("baidu", SeoBotCategory.Search,
            ["baiduspider"],
            [".baidu.com", ".baidu.jp"]),

        new("apple", SeoBotCategory.Search,
            ["applebot"],
            [".applebot.apple.com", ".apple.com"]),

        // Ters DNS ile doğrulanmıyor: DuckDuckGo kimliğini yayımlanmış IP listesiyle doğrulatır.
        new("duckduckgo", SeoBotCategory.Search, ["duckduckbot", "duckassistbot"], []),

        new("openai", SeoBotCategory.Ai, ["gptbot", "oai-searchbot", "chatgpt-user"], []),
        new("anthropic", SeoBotCategory.Ai, ["claudebot", "claude-searchbot", "claude-user"], []),
        new("perplexity", SeoBotCategory.Ai, ["perplexitybot", "perplexity-user"], []),
        new("commoncrawl", SeoBotCategory.Ai, ["ccbot"], []),

        new("ahrefs", SeoBotCategory.Tool, ["ahrefsbot", "ahrefssiteaudit"], []),
        new("semrush", SeoBotCategory.Tool, ["semrushbot", "siteauditbot"], []),
        new("moz", SeoBotCategory.Tool, ["dotbot", "rogerbot"], []),
        new("majestic", SeoBotCategory.Tool, ["mj12bot"], [])
    ];

    /// <summary>
    /// Bilinen motorlara UYMAYAN ama yine de bot olan istemcilerin imzası.
    /// </summary>
    /// <remarks>
    /// Bunlar <see cref="OtherKey"/> altında toplanır. Amaç sayıyı DOĞRU tutmaktır: tanınmayan bir
    /// tarayıcıyı "insan ziyaretçi" saymak, bot trafiği oranını olduğundan küçük gösterirdi.
    /// <c>slurp</c> Yahoo'nun, <c>spider</c>/<c>crawler</c> ise genel kalıplardır.
    /// </remarks>
    private static readonly string[] GenericBotTokens =
        ["bot", "crawler", "spider", "slurp", "curl", "wget", "scrapy", "python-requests"];

    /// <summary>
    /// Kullanıcı aracısından motoru bulur.
    /// </summary>
    /// <param name="userAgent">
    /// IIS'in yazdığı HAM değer. Boşluklar <c>+</c> olabilir, değer <c>-</c> olabilir; ikisi de
    /// burada karşılanır — çağıranların her biri kendi temizliğini yapsaydı kural bölünürdü.
    /// </param>
    /// <returns>Motor; bot değilse <c>null</c>. Tanınan ama bilinmeyen bot için <c>Key</c> = <see cref="OtherKey"/>.</returns>
    public static SeoBotEngine? Match(string userAgent)
    {
        if (string.IsNullOrEmpty(userAgent) || userAgent == "-")
        {
            return null;
        }

        var kucuk = userAgent.ToLowerInvariant();

        foreach (var engine in Engines)
        {
            foreach (var token in engine.UserAgentTokens)
            {
                if (kucuk.Contains(token, StringComparison.Ordinal))
                {
                    return engine;
                }
            }
        }

        foreach (var token in GenericBotTokens)
        {
            if (kucuk.Contains(token, StringComparison.Ordinal))
            {
                return Other;
            }
        }

        return null;
    }

    /// <summary>Tanınan ama bilinmeyen bot. Doğrulaması desteklenmez (son ek listesi boş).</summary>
    public static SeoBotEngine Other { get; } = new(OtherKey, SeoBotCategory.Tool, [], []);

    /// <summary>Anahtardan motor (kayıtlı rapor okunurken kullanılır).</summary>
    public static SeoBotEngine? ByKey(string key) =>
        string.Equals(key, OtherKey, StringComparison.Ordinal)
            ? Other
            : Engines.FirstOrDefault(engine => string.Equals(engine.Key, key, StringComparison.Ordinal));

    /// <summary>
    /// Ters DNS adı motora AİT Mİ. Son ek karşılaştırması nokta ile başlar (<c>.googlebot.com</c>):
    /// aksi hâlde <c>evilgooglebot.com</c> gibi bir ad "Google" sayılırdı.
    /// </summary>
    public static bool MatchesReverseDns(SeoBotEngine engine, string hostName)
    {
        if (engine.ReverseDnsSuffixes.Count == 0 || string.IsNullOrWhiteSpace(hostName))
        {
            return false;
        }

        var ad = hostName.Trim().TrimEnd('.').ToLowerInvariant();
        return engine.ReverseDnsSuffixes.Any(suffix => ad.EndsWith(suffix, StringComparison.Ordinal));
    }
}
