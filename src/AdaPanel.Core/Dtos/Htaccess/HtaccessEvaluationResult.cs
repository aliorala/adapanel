namespace AdaPanel.Core.Dtos.Htaccess;

/// <summary>Motorun istek hakkındaki KARARI.</summary>
public enum HtaccessOutcome
{
    /// <summary>Hiçbir kural eşleşmedi; istek olağan akışına devam etmeli.</summary>
    None,

    /// <summary>İç yeniden yazma: <c>RewrittenPath</c>/<c>RewrittenQuery</c> kullanılmalı.</summary>
    Rewritten,

    /// <summary>Dış yönlendirme: <c>StatusCode</c> + <c>RedirectLocation</c>.</summary>
    Redirect,

    /// <summary><c>[F]</c> bayrağı — 403.</summary>
    Forbidden,

    /// <summary><c>[G]</c> bayrağı — 410.</summary>
    Gone,

    /// <summary>Erişim denetimi (<c>Deny</c> / <c>Require</c>) reddetti — 403.</summary>
    AccessDenied,

    /// <summary>Kimlik doğrulama gerekiyor — 401 + <c>WWW-Authenticate</c>.</summary>
    AuthenticationRequired,

    /// <summary>
    /// Motor kararını VEREMEDİ. Bütçe aşıldı, desen zaman aşımına uğradı ya da yapılandırma
    /// ölümcül biçimde bozuk. Barındırma katmanı isteği BOZMADAN devam ettirmelidir.
    /// </summary>
    Failed
}

/// <summary><c>Header</c> direktifinin istediği işlem.</summary>
public enum HtaccessHeaderOperation
{
    Set,
    Add,
    Append,
    Unset
}

/// <summary>Tek bir <c>Header</c> talimatı.</summary>
public sealed class HtaccessHeaderAction
{
    public HtaccessHeaderOperation Operation { get; init; }

    public string Name { get; init; } = string.Empty;

    public string Value { get; init; } = string.Empty;

    /// <summary><c>Header always set ...</c> — hata yanıtlarında da uygulanır.</summary>
    public bool Always { get; init; }
}

/// <summary>Bir isteğe uygulanacak 401 sorgusu.</summary>
public sealed class HtaccessAuthChallenge
{
    /// <summary><c>AuthName</c> — tarayıcının gösterdiği alan adı.</summary>
    public string Realm { get; init; } = string.Empty;

    /// <summary><c>AuthUserFile</c>'ın DOĞRULANMIŞ (site kökü içinde) tam yolu.</summary>
    public string UserFilePath { get; init; } = string.Empty;

    /// <summary><c>Require valid-user</c> — herhangi bir geçerli kullanıcı yeterli.</summary>
    public bool AnyValidUser { get; init; }

    /// <summary><c>Require user ali veli</c> — yalnızca bu kullanıcılar.</summary>
    public IReadOnlyList<string> AllowedUsers { get; init; } = [];
}

/// <summary>Kimlik doğrulama denemesinin sonucu. İstisna FIRLATILMAZ.</summary>
public sealed class HtaccessAuthResult
{
    public bool Succeeded { get; init; }

    /// <summary>Başarısızlığın makine-okur sebebi (<see cref="HtaccessDiagnosticCodes"/>).</summary>
    public string FailureCode { get; init; } = string.Empty;

    public string FailureReason { get; init; } = string.Empty;

    public IReadOnlyList<HtaccessDiagnostic> Diagnostics { get; init; } = [];
}

/// <summary>Bütçenin ne kadarının harcandığı. Panel yavaş bir siteyi buradan teşhis eder.</summary>
public sealed class HtaccessBudgetReport
{
    public int RuleEvaluations { get; init; }

    public int InternalRedirects { get; init; }

    public int RegexEvaluations { get; init; }

    public TimeSpan Elapsed { get; init; }

    /// <summary>Sınırlardan herhangi biri aşıldı mı?</summary>
    public bool Exceeded { get; init; }
}

/// <summary><c>Options</c> direktifinin ÇÖZÜLMÜŞ hâli (basamaklandırma birleştirildikten sonra).</summary>
public sealed class HtaccessOptionsState
{
    /// <summary><c>+Indexes</c>/<c>-Indexes</c>. Hiç belirtilmediyse <c>null</c> (miras/varsayılan).</summary>
    public bool? Indexes { get; init; }

    public bool? FollowSymLinks { get; init; }

    public bool? SymLinksIfOwnerMatch { get; init; }

    public bool? MultiViews { get; init; }

    public bool? ExecCgi { get; init; }

    public bool? Includes { get; init; }
}

/// <summary><c>ExpiresByType</c>/<c>ExpiresDefault</c> ile hesaplanacak önbellek ömrü.</summary>
public sealed class HtaccessExpiresPolicy
{
    /// <summary><c>ExpiresActive Off</c> ise hiçbir şey uygulanmaz.</summary>
    public bool Active { get; init; }

    /// <summary>MIME tipine göre saniye cinsinden ömür ve temel (erişim/değişiklik).</summary>
    public IReadOnlyDictionary<string, HtaccessExpiresEntry> ByType { get; init; } =
        new Dictionary<string, HtaccessExpiresEntry>(StringComparer.OrdinalIgnoreCase);

    public HtaccessExpiresEntry? Default { get; init; }

    /// <summary>
    /// Verilen içerik tipi için geçerli kaydı bulur. MIME tipi <c>text/css; charset=utf-8</c>
    /// biçiminde gelebileceği için parametre kısmı atılır.
    /// </summary>
    public HtaccessExpiresEntry? Resolve(string? contentType)
    {
        if (!Active)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(contentType))
        {
            var temiz = contentType.Split(';')[0].Trim();
            if (ByType.TryGetValue(temiz, out var kayit))
            {
                return kayit;
            }
        }

        return Default;
    }
}

/// <summary>Tek bir <c>Expires*</c> kaydı.</summary>
public sealed class HtaccessExpiresEntry
{
    /// <summary><c>access</c>/<c>now</c> ise <c>true</c>; <c>modification</c> ise <c>false</c>.</summary>
    public bool FromAccessTime { get; init; } = true;

    public long Seconds { get; init; }
}

/// <summary>
/// Bir istek için <c>.htaccess</c> basamaklandırmasının ÇÖZÜLMÜŞ sonucu.
/// </summary>
/// <remarks>
/// <para>
/// Sonuç yalnızca "ne yapılacağını" değil, "neyin yapılamadığını" da taşır. Kural gereği motor
/// istisna fırlatmaz: her başarısızlık <see cref="EngineFailed"/> + <see cref="Diagnostics"/>
/// üzerinden bildirilir ve barındırma katmanı isteği bozmadan devam edebilir.
/// </para>
/// <para>
/// Yeniden yazma sonucundan bağımsız olarak <see cref="DirectoryIndex"/>, <see cref="Headers"/>,
/// <see cref="ErrorDocuments"/>, <see cref="Options"/>, <see cref="MimeTypes"/> ve
/// <see cref="Expires"/> her zaman doldurulur — bunlar isteğin devamında da gereklidir.
/// </para>
/// </remarks>
public sealed record HtaccessEvaluationResult
{
    public HtaccessOutcome Outcome { get; init; } = HtaccessOutcome.None;

    /// <summary>İç yeniden yazmadan sonraki URL yolu (başında <c>/</c>).</summary>
    public string RewrittenPath { get; init; } = string.Empty;

    /// <summary>İç yeniden yazmadan sonraki sorgu dizesi (baştaki <c>?</c> olmadan).</summary>
    public string RewrittenQuery { get; init; } = string.Empty;

    /// <summary>Yönlendirme/hata durum kodu. <see cref="HtaccessOutcome.None"/> için 0.</summary>
    public int StatusCode { get; init; }

    /// <summary>Dış yönlendirmenin hedefi (mutlak URL).</summary>
    public string RedirectLocation { get; init; } = string.Empty;

    public IReadOnlyList<HtaccessHeaderAction> Headers { get; init; } = [];

    /// <summary><c>DirectoryIndex</c> listesi. <c>disabled</c> yazıldıysa boş liste.</summary>
    public IReadOnlyList<string> DirectoryIndex { get; init; } = [];

    /// <summary>Durum kodu → <c>ErrorDocument</c> hedefi (yol, URL ya da tırnaklı metin).</summary>
    public IReadOnlyDictionary<int, string> ErrorDocuments { get; init; } =
        new Dictionary<int, string>();

    public HtaccessOptionsState Options { get; init; } = new();

    /// <summary><c>AddType</c> ile eşlenen uzantı → MIME tipi (uzantılar baştaki nokta ile).</summary>
    public IReadOnlyDictionary<string, string> MimeTypes { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary><c>AddDefaultCharset</c>/<c>DefaultCharset</c>. Kapalıysa boş.</summary>
    public string DefaultCharset { get; init; } = string.Empty;

    public HtaccessExpiresPolicy Expires { get; init; } = new();

    /// <summary>
    /// 401 üretilecekse sorgu bilgisi. <see cref="HtaccessOutcome.AuthenticationRequired"/>
    /// dışındaki sonuçlarda da dolu olabilir (kullanıcı zaten doğrulanmışsa).
    /// </summary>
    public HtaccessAuthChallenge? Auth { get; init; }

    /// <summary>
    /// Motor kararını veremedi. <b>Bu bir istisna DEĞİL, bir DEĞERDİR</b> — barındırma katmanı
    /// isteği bozmadan olağan akışa devam etmelidir.
    /// </summary>
    public bool EngineFailed { get; init; }

    /// <summary>Başarısızlığın makine-okur kodu (<see cref="HtaccessDiagnosticCodes"/>).</summary>
    public string FailureCode { get; init; } = string.Empty;

    public IReadOnlyList<HtaccessDiagnostic> Diagnostics { get; init; } = [];

    public HtaccessBudgetReport Budget { get; init; } = new();

    /// <summary>Basamaklandırmada gerçekten OKUNAN dosyalar, kökten içeriye doğru.</summary>
    public IReadOnlyList<string> AppliedFiles { get; init; } = [];
}
