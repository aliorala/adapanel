using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace AdaPanel.Infrastructure.Diagnostics;

/// <summary>Maskeleme sonucu; doğrudan <c>ErrorReport</c> alanlarına yazılabilir.</summary>
public sealed record SanitizedError(
    string ExceptionType,
    string Message,
    string StackTrace,
    string RoutePattern,
    string Fingerprint);

/// <summary>
/// Hata kaydını göndermeye uygun hâle getirir. Onay hata başına değil KURULUMDA BİR KEZ
/// alındığı için, gönderilen verinin otomatik gönderimi savunulabilir kılacak kadar küçük
/// olması gerekir: burası o daraltmayı yapan tek yerdir.
///
/// SAF ve TEST EDİLEBİLİR: yalnız statik metotlar, dosya sistemine/ortama dokunmaz —
/// böylece "parola sızıyor mu" sorusu gerçek bir testle yanıtlanabilir.
/// </summary>
public static partial class ErrorReportSanitizer
{
    public const int MaxMessageLength = 2000;
    public const int MaxStackTraceLength = 8000;
    public const int MaxRoutePatternLength = 250;
    public const int FingerprintFrameCount = 3;

    /// <summary>
    /// Alan adı sayılması için son etiketin bu kümede olması ŞART. NEDEN: yığın izleri
    /// noktalı .NET tip adlarıyla doludur (System.IO.FileNotFoundException, x.php, config.json);
    /// "nokta içeriyorsa alan adıdır" varsayımı teşhis değerini yok ederdi.
    /// </summary>
    private static readonly HashSet<string> KnownTlds = new(StringComparer.Ordinal)
    {
        "com", "net", "org", "info", "biz", "name", "pro", "edu", "gov", "mil", "int",
        "tr", "de", "fr", "nl", "uk", "ru", "us", "ca", "au", "be", "at", "ch", "se", "no", "dk",
        "fi", "es", "it", "pl", "cz", "gr", "pt", "ro", "bg", "hu", "ua", "az", "kz", "ir", "sa",
        "ae", "eg", "il", "in", "cn", "jp", "kr", "br", "mx", "ar", "cl", "za", "nz", "ie", "sk",
        "si", "hr", "rs", "lt", "lv", "ee", "is", "lu", "mt", "cy",
        "xyz", "online", "site", "shop", "store", "web", "space", "website", "click", "link",
        "live", "life", "world", "today", "email", "agency", "digital", "media", "network",
        "systems", "solutions", "services", "support", "team", "tech", "cloud", "host", "hosting",
        "domains", "software", "studio", "design", "art", "blog", "news", "press", "wiki",
        "travel", "tours", "estate", "clinic", "dental", "legal", "finance", "bank", "insurance",
        "school", "academy", "education", "university", "institute", "church", "group", "company",
        "global", "center", "city", "land", "house", "market", "sale", "deals", "gold", "vip",
        "eu", "asia", "africa", "moe", "fun", "icu", "top", "pw", "cc", "tv", "fm", "am",
        "gg", "gs", "ms", "ly", "st", "to", "ws", "mobi", "coop", "aero", "museum", "jobs", "tel"
    };

    /// <summary>Alan adı sanılmasını engellemek için elenen uzantılar (TLD kümesiyle çakışanlar).</summary>
    private static readonly HashSet<string> FileExtensions = new(StringComparer.Ordinal)
    {
        "php", "dll", "exe", "cs", "json", "xml", "config", "js", "css", "html", "htm", "txt",
        "log", "ps1", "sql", "zip", "aspx", "py", "md", "pdb", "ini", "cmd", "bat", "sh", "yml",
        "yaml", "csproj", "sln", "resx", "cshtml", "ts", "vue", "png", "jpg", "gif", "svg", "ico",
        "woff", "woff2", "map", "lock", "bak", "tmp", "old", "cer", "pfx", "key", "crt", "pem"
    };

    // --- Sır kalıpları: anahtar=değer ve "Bearer <token>" biçimleri --------------------------
    [GeneratedRegex(
        @"(?i)\b(password|passwd|pwd|secret|token|apikey|api[_-]?key|access[_-]?key|access[_-]?token|refresh[_-]?token|client[_-]?secret|private[_-]?key|authorization|auth[_-]?token|connection[_-]?string|user\s?id|uid|sas|signature)\b\s*[:=]\s*(""[^""\r\n]*""|'[^'\r\n]*'|[^\s;,&)\]}""']+)",
        RegexOptions.CultureInvariant)]
    private static partial Regex SecretAssignmentPattern();

    [GeneratedRegex(@"(?i)\b(bearer|basic|negotiate)\s+[A-Za-z0-9._\-+/=]{8,}", RegexOptions.CultureInvariant)]
    private static partial Regex AuthSchemePattern();

    /// <summary>
    /// PANELIN KENDI erisim anahtari ("adap_" + onaltilik). En kritik kacak buydu: anahtar bir hata
    /// mesajina duserse toplayiciya DUZ METIN giderdi ve o anahtar paneli yonetmeye yeter. Anahtar=deger
    /// kalibina takilmaz, cunku metinde ciplak da gecebilir.
    /// </summary>
    [GeneratedRegex(@"adap_[0-9a-fA-F]{16,}", RegexOptions.CultureInvariant)]
    private static partial Regex PanelApiKeyPattern();

    /// <summary>Ciplak JWT: "eyJ" ile baslar ve nokta icerir; hicbir anahtar adina bagli degildir.</summary>
    [GeneratedRegex(@"eyJ[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,}(\.[A-Za-z0-9_\-]+)?", RegexOptions.CultureInvariant)]
    private static partial Regex BareJwtPattern();

    /// <summary>
    /// Veritabani/katalog/sunucu adi. Musteri veritabani adlari musteri adini tasir (musteri_wp_db
    /// gibi) ve baglanti dizesi hatalarinda oldugu gibi gorunur.
    /// </summary>
    [GeneratedRegex(@"(?i)\b(database|initial\s+catalog|data\s+source)\b\s*=\s*([^\s;,\)\]}""']+)", RegexOptions.CultureInvariant)]
    private static partial Regex DatabaseTargetPattern();

    /// <summary>
    /// Komut satiri parola bayragi: sqlcmd/mysql gibi araclar -P/-p/--password ile parola alir ve
    /// panel bu komutlari ProcessRunner ile calistiriyor; hata metninde komut satiri gorunebilir.
    /// </summary>
    [GeneratedRegex(@"(?i)(\s-{1,2}(?:p|password|pwd)[=\s]+)(\S+)", RegexOptions.CultureInvariant)]
    private static partial Regex CommandLinePasswordPattern();

    /// <summary>
    /// Uygulama havuzu kimliği: "IIS APPPOOL\musterisite". Havuz adı SİTE adını taşır, yani doğrudan
    /// müşteri kimliğidir. Tek ters bölülü olduğu için UNC kalıbına (iki ters bölü) takılmıyordu.
    /// </summary>
    [GeneratedRegex(@"(?i)\bIIS\s+APPPOOL\\[A-Za-z0-9_.$\-]{1,64}", RegexOptions.CultureInvariant)]
    private static partial Regex AppPoolAccountPattern();

    /// <summary>
    /// Adlandırılmış hesap: "Login failed for user 'WIN-SRV01\ahmet'", "identity: SUNUCU\ftpuser".
    /// <para>
    /// Neden anahtar kelime şart: "SUNUCU\ahmet" biçimi tek başına yol parçasından ayırt edilemez —
    /// genel bir <c>kelime\kelime</c> kalıbı <c>D:\musteriler\ahmet\rapor.txt</c> yolunu da yer ve
    /// teşhis için gereken dosya adını yok eder. Bu yüzden yalnız hesap BAĞLAMI olan yerde uygulanır.
    /// </para>
    /// </summary>
    [GeneratedRegex(
        @"(?i)\b(user|account|logon|identity|username)\b(?:\s*[:=]\s*|\s+)'?(?:[A-Za-z0-9_.\-]{1,32}\\)?[A-Za-z0-9_.$\-]{1,64}'?",
        RegexOptions.CultureInvariant)]
    private static partial Regex NamedAccountPattern();


    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9\-]+(\.[A-Za-z0-9\-]+)+", RegexOptions.CultureInvariant)]
    private static partial Regex EmailPattern();

    [GeneratedRegex(
        @"\b(25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)(\.(25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)){3}\b",
        RegexOptions.CultureInvariant)]
    private static partial Regex IPv4Pattern();

    [GeneratedRegex(
        @"(?<![\w:.])(([A-Fa-f0-9]{1,4}:){7}[A-Fa-f0-9]{1,4}|([A-Fa-f0-9]{1,4}:){1,7}:([A-Fa-f0-9]{1,4}(:[A-Fa-f0-9]{1,4}){0,6})?|::1)(?![\w:.])",
        RegexOptions.CultureInvariant)]
    private static partial Regex IPv6Pattern();

    // --- Yol kalıpları: en özelden en genele; sıra ÖNEMLİ ------------------------------------
    [GeneratedRegex(
        @"(?i)\b[a-z]:\\(?:[^\\\r\n""'<>|:*?]+\\)*wwwroot\\[^\\\r\n""'<>|:*?]+",
        RegexOptions.CultureInvariant)]
    private static partial Regex SiteRootPattern();

    [GeneratedRegex(@"(?i)\b[a-z]:\\(?:[^\\\r\n""'<>|:*?]+\\)*wwwroot\b", RegexOptions.CultureInvariant)]
    private static partial Regex WebRootPattern();

    [GeneratedRegex(@"(?i)\b[a-z]:\\Users\\[^\\\r\n""'<>|:*?]+", RegexOptions.CultureInvariant)]
    private static partial Regex UserHomePattern();

    [GeneratedRegex(@"(?i)\b[a-z]:\\ProgramData\\AdaPanel", RegexOptions.CultureInvariant)]
    private static partial Regex PanelDataPattern();

    [GeneratedRegex(@"(?i)\b[a-z]:\\ProgramData", RegexOptions.CultureInvariant)]
    private static partial Regex ProgramDataPattern();

    [GeneratedRegex(@"(?i)\b[a-z]:\\Program Files \(x86\)", RegexOptions.CultureInvariant)]
    private static partial Regex ProgramFilesX86Pattern();

    [GeneratedRegex(@"(?i)\b[a-z]:\\Program Files", RegexOptions.CultureInvariant)]
    private static partial Regex ProgramFilesPattern();

    [GeneratedRegex(@"(?i)\b[a-z]:\\Windows\b", RegexOptions.CultureInvariant)]
    private static partial Regex WindowsDirPattern();

    [GeneratedRegex(@"\\\\[^\\\r\n""'<>|:*?]+\\[^\\\r\n""'<>|:*?]+", RegexOptions.CultureInvariant)]
    private static partial Regex UncPathPattern();

    /// <summary>
    /// Yukarıdakilere uymayan mutlak yollar: yalnız son parça (dosya adı) korunur.
    /// Boşluk KAPSAM DIŞI: "C:\veri\x.txt bulunamadı" cümlesinde metnin geri kalanı yutulmasın.
    /// Boşluklu bilinen kökler (Program Files) zaten yukarıda ele alındı.
    /// </summary>
    [GeneratedRegex(@"(?i)\b[a-z]:\\[^\s\r\n""'<>|:*?]*", RegexOptions.CultureInvariant)]
    private static partial Regex AnyDrivePathPattern();

    /// <summary>
    /// Alan adı adayı. TAMAMEN KÜÇÜK HARF şartı bilerek konuldu: .NET tipleri PascalCase'dir
    /// (System.IO, AdaPanel.Core) ve böylece kendiliğinden elenir. Ayraç "/" ve "\" bilerek
    /// dışlanmadı: "https://musteri.com/x" ve yol içinde kalan alan adları da karmalanmalı.
    /// </summary>
    [GeneratedRegex(
        @"(?<![\w@.\-])([a-z0-9](?:[a-z0-9\-]*[a-z0-9])?)(\.[a-z0-9](?:[a-z0-9\-]*[a-z0-9])?)+(?![\w\-])",
        RegexOptions.CultureInvariant)]
    private static partial Regex DomainCandidatePattern();

    /// <summary>Sorgu dizesi: yalnız "=" içeren gerçek sorgular; düz cümledeki "?" bozulmasın.</summary>
    [GeneratedRegex(@"(?<=\S)\?[^\s""'<>]*=[^\s""'<>]*", RegexOptions.CultureInvariant)]
    private static partial Regex QueryStringPattern();

    [GeneratedRegex(
        @"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b",
        RegexOptions.CultureInvariant)]
    private static partial Regex GuidPattern();

    [GeneratedRegex(@"\d+", RegexOptions.CultureInvariant)]
    private static partial Regex DigitRunPattern();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespacePattern();

    /// <summary>Yol parçalarından değer gibi görünenleri şablonlar (yönlendirme şablonu yoksa).</summary>
    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9\-_]{0,31}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeRouteSegmentPattern();

    /// <summary>
    /// Bir hatayı gönderilebilir hâle getirir: tip + maskelenmiş mesaj/yığın izi + yol şablonu
    /// + tekilleştirme parmak izi.
    /// </summary>
    public static SanitizedError Sanitize(
        string? exceptionType,
        string? message,
        string? stackTrace,
        string? routePattern)
    {
        var type = Truncate(CollapseSpaces(exceptionType ?? string.Empty), 250);
        if (type.Length == 0)
        {
            type = "UnknownException";
        }

        var maskedMessage = Truncate(Mask(message), MaxMessageLength);
        var maskedStack = Truncate(MaskStackTrace(stackTrace), MaxStackTraceLength);
        var maskedRoute = Truncate(Mask(routePattern), MaxRoutePatternLength);
        var fingerprint = ComputeFingerprint(type, maskedMessage, maskedStack);

        return new SanitizedError(type, maskedMessage, maskedStack, maskedRoute, fingerprint);
    }

    /// <summary>
    /// Serbest metinden kimliklendirici her şeyi çıkarır. Sıra önemlidir: sırlar → e-posta →
    /// IP → yollar (alan adı içeren site kökü burada zaten yok olur) → kalan alan adları.
    /// </summary>
    public static string Mask(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var text = value;

        // 1) Sırlar: değeri asla taşımayalım. Anahtar adı kalır (teşhis için yeterli).
        //    "Bearer <token>" ÖNCE temizlenir: "Authorization: Bearer xyz" satırında anahtar=değer
        //    kuralı yalnız "Bearer" kelimesini yutar, asıl token dışarıda kalırdı.
        text = AuthSchemePattern().Replace(text, match => match.Groups[1].Value + " <redacted>");
        text = SecretAssignmentPattern().Replace(text, match => match.Groups[1].Value + "=<redacted>");
        // Anahtar adına BAĞLI OLMAYAN sırlar: metinde çıplak geçtikleri için yukarıdaki iki kalıba
        // takılmazlar. Panelin kendi anahtarı en önemlisi.
        text = PanelApiKeyPattern().Replace(text, "<redacted>");
        text = BareJwtPattern().Replace(text, "<redacted>");
        text = CommandLinePasswordPattern().Replace(text, match => match.Groups[1].Value + "<redacted>");
        text = DatabaseTargetPattern().Replace(text, match => match.Groups[1].Value + "=<redacted>");

        // 2) Sorgu dizesi DEĞERLERİ hiç gönderilmez.
        text = QueryStringPattern().Replace(text, "?<query>");

        // 3) E-posta ve IP: hiçbir biçimde gönderilmez (alan adından ÖNCE, aksi hâlde
        //    e-postanın alan kısmı ayrı yakalanıp karma olarak sızardı).
        text = EmailPattern().Replace(text, "<email>");
        text = IPv6Pattern().Replace(text, "<ip>");
        text = IPv4Pattern().Replace(text, "<ip>");

        // 4) Yollar: sembolik köklere indirgenir.
        text = MaskPaths(text);

        // 5) Kalan alan adları: karma (aynı alan adı hep aynı takma ada gider → gruplanabilir).
        text = DomainCandidatePattern().Replace(text, HashDomainMatch);

        return text.Trim();
    }

    /// <summary>Yığın izi: satır yapısı korunur (kareler teşhis için gerekli), içerik maskelenir.</summary>
    public static string MaskStackTrace(string? stackTrace)
    {
        if (string.IsNullOrWhiteSpace(stackTrace))
        {
            return string.Empty;
        }

        var lines = stackTrace.Replace("\r\n", "\n").Split('\n');
        var builder = new StringBuilder();
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.Append('\n');
            }

            builder.Append(Mask(trimmed));
        }

        return builder.ToString();
    }

    /// <summary>Dosya yollarını sembolik köklere çevirir; son parça (dosya adı) korunur.</summary>
    public static string MaskPaths(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var text = value;
        text = SiteRootPattern().Replace(text, "<site-root>");
        text = WebRootPattern().Replace(text, "<web-root>");
        text = UserHomePattern().Replace(text, "<user-home>");
        text = PanelDataPattern().Replace(text, "<panel-data>");
        text = ProgramDataPattern().Replace(text, "<program-data>");
        text = ProgramFilesX86Pattern().Replace(text, "<program-files-x86>");
        text = ProgramFilesPattern().Replace(text, "<program-files>");
        text = WindowsDirPattern().Replace(text, "<windows>");
        text = UncPathPattern().Replace(text, "<unc>");
        // Windows hesapları UNC'den SONRA: UNC iki ters bölü ister, bunlar tek ters bölülü olanı
        // yakalar. Sıra ters olsaydı "\\sunucu\pay" yolunun ikinci yarısı hesap sanılırdı.
        text = AppPoolAccountPattern().Replace(text, "<account>");
        text = NamedAccountPattern().Replace(text, match => match.Groups[1].Value + " <account>");

        // Kalan mutlak yollar: dizin yapısı müşteriye özgü olabilir (D:\musteriler\ahmet\...),
        // ama dosya adı hatayı bulmak için gerekli → yalnız son parça bırakılır.
        text = AnyDrivePathPattern().Replace(text, match =>
        {
            var raw = match.Value.TrimEnd();
            var lastSeparator = raw.LastIndexOf('\\');
            var leaf = lastSeparator >= 0 && lastSeparator < raw.Length - 1
                ? raw[(lastSeparator + 1)..]
                : string.Empty;
            var suffix = match.Value.Length > raw.Length ? match.Value[raw.Length..] : string.Empty;
            return leaf.Length == 0 ? "<path>" + suffix : "<path>\\" + leaf + suffix;
        });

        return text;
    }

    /// <summary>
    /// Alan adını geri döndürülemez ama TUTARLI bir takma ada çevirir: aynı alan adı hep
    /// "site-a1b2c3" olur, böylece hatalar site bazında gruplanabilir ama site kimliği gitmez.
    /// </summary>
    public static string HashDomain(string domain)
    {
        var normalized = domain.Trim().TrimEnd('.').ToLowerInvariant();
        if (normalized.Length == 0)
        {
            return "site-000000";
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return "site-" + Convert.ToHexString(hash, 0, 3).ToLowerInvariant();
    }

    /// <summary>
    /// Tekilleştirme anahtarı: tip + normalleştirilmiş mesaj + ilk birkaç kare.
    /// Sayılar/GUID'ler normalleştirilir; aksi hâlde "id 41 bulunamadı" ile "id 42 bulunamadı"
    /// ayrı kayıtlar olur ve tekilleştirme hiç işe yaramazdı.
    /// </summary>
    public static string ComputeFingerprint(string? exceptionType, string? message, string? stackTrace)
    {
        var frames = string.IsNullOrWhiteSpace(stackTrace)
            ? string.Empty
            : string.Join(
                '\n',
                stackTrace.Replace("\r\n", "\n")
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Take(FingerprintFrameCount));

        var seed = string.Join(
            '\u001f',
            Normalize(exceptionType),
            Normalize(message),
            Normalize(frames));

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }

    /// <summary>
    /// İstek yolunu ŞABLON hâline getirir. Yönlendirme şablonu varsa (ör. "api/v1/sites/{site}")
    /// doğrudan kullanılır; yoksa (hata yönlendirmeden ÖNCE oluştuysa) değer gibi görünen
    /// parçalar "{value}" ile değiştirilir — site adı/kimlik gövdeye sızmasın.
    /// </summary>
    public static string BuildRoutePattern(string? httpMethod, string? routeTemplate, string? rawPath)
    {
        var method = string.IsNullOrWhiteSpace(httpMethod) ? string.Empty : httpMethod.Trim().ToUpperInvariant();
        string path;

        if (!string.IsNullOrWhiteSpace(routeTemplate))
        {
            path = "/" + routeTemplate.Trim().TrimStart('/');
        }
        else if (!string.IsNullOrWhiteSpace(rawPath))
        {
            var queryIndex = rawPath.IndexOf('?');
            var pathOnly = queryIndex >= 0 ? rawPath[..queryIndex] : rawPath;
            var segments = pathOnly.Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(segment => SafeRouteSegmentPattern().IsMatch(segment) ? segment : "{value}");
            path = "/" + string.Join('/', segments);
        }
        else
        {
            path = "/<unknown>";
        }

        var pattern = method.Length == 0 ? path : method + " " + path;
        return Truncate(pattern, MaxRoutePatternLength);
    }

    private static string HashDomainMatch(Match match)
    {
        var candidate = match.Value;
        var labels = candidate.Split('.');
        var lastLabel = labels[^1];

        // Dosya adı (x.php, config.json) alan adı DEĞİLDİR; ayrıca bilinmeyen uzantılar da
        // TLD listesinde olmadıkları için kendiliğinden elenir.
        if (FileExtensions.Contains(lastLabel) || !KnownTlds.Contains(lastLabel))
        {
            return candidate;
        }

        // Tek etiket + TLD'den kısa adaylar (ör. "a.tr") yine alan adıdır; karma uygula.
        return HashDomain(candidate);
    }

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var text = GuidPattern().Replace(value, "#");
        text = DigitRunPattern().Replace(text, "#");
        text = WhitespacePattern().Replace(text, " ");
        return text.Trim().ToLowerInvariant();
    }

    private static string CollapseSpaces(string value) => WhitespacePattern().Replace(value, " ").Trim();

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength] + "…";
}
