using AdaPanel.Core.Dtos.Htaccess;

namespace AdaPanel.Infrastructure.Htaccess;

/// <summary>
/// Motorun "yüklü" saydığı Apache modülleri. <c>&lt;IfModule&gt;</c> bunlara bakar.
/// </summary>
/// <remarks>
/// <para>
/// Liste GERÇEĞE göre kurulmuştur: yalnızca motorun taklit ettiği modüller yüklü sayılır.
/// <c>&lt;IfModule mod_php7.c&gt;</c> bloğu bu yüzden ATLANIR — IIS'te mod_php yoktur ve
/// içindeki <c>php_value</c> satırlarını çalıştırıyormuş gibi yapmak yalan olurdu.
/// </para>
/// <para>
/// <b><c>mod_negotiation.c</c> bilerek YÜKLÜ sayılıyor</b> ve tek sebebi vardır: Laravel ve
/// Symfony gibi çatılar <c>Options -MultiViews -Indexes</c> satırını bu bloğun içine yazar.
/// Modülü yüklü saymasaydık <c>-Indexes</c> de atlanır, dizin listeleme AÇIK kalırdı. İçerik
/// pazarlığı zaten yapılmadığı için <c>-MultiViews</c> ile davranışımız Apache ile AYNIDIR;
/// yalnızca <c>+MultiViews</c> farklıdır ve o bulgu olarak bildirilir.
/// </para>
/// </remarks>
internal static class HtaccessModules
{
    // TIP `IReadOnlySet<>` DEGIL `HashSet<>`: IReadOnlySet<T> net5+ ile geldi, net48'de YOK ve
    // polyfill edilemez (HashSet<T> orada o arayuzu UYGULAMIYOR -- arayuzu kendimiz tanimlasak da
    // atama derlenmezdi). Motorun kaynaklari IIS modulunde de derlendigi icin tek yazim gerekiyordu.
    // Kaybedilen sey yalniz DERLEME ZAMANI salt-okunurlugu; tek tuketici
    // HtaccessConfigurationBuilder'daki `Contains` cagrisidir ve O(1) davranis AYNI kaldi.
    public static HashSet<string> Default { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "core.c",
        "mod_rewrite.c",
        "mod_alias.c",
        "mod_headers.c",
        "mod_expires.c",
        "mod_mime.c",
        "mod_dir.c",
        "mod_env.c",
        "mod_autoindex.c",
        "mod_negotiation.c",
        "mod_auth_basic.c",
        "mod_authn_file.c",
        "mod_authz_core.c",
        "mod_authz_host.c",
        "mod_authz_user.c",
        "mod_access_compat.c"
    };
}

/// <summary>
/// Blok süzgeçlerinin (<c>&lt;Files&gt;</c>, <c>&lt;Limit&gt;</c>, ...) değerlendirileceği bağlam.
/// </summary>
/// <remarks>
/// Yapılandırma İSTEK BAŞINA kurulur; ayrıştırma sonucu önbelleklenir ama birleştirme ucuzdur ve
/// blok süzgeçleri isteğe bağlıdır. Alternatif (bloğu çalışma zamanında taşımak) her kural
/// değerlendirmesinde yeniden süzme demek olurdu.
/// </remarks>
internal sealed class HtaccessScope
{
    /// <summary>İstenen kaynağın dosya adı (<c>resim.jpg</c>). Dizin isteğinde boş olabilir.</summary>
    public string FileName { get; init; } = string.Empty;

    /// <summary>İsteğin URL yolu (<c>/gizli/rapor.pdf</c>).</summary>
    public string RequestPath { get; init; } = "/";

    /// <summary>İsteğin fiziksel dizini — <c>&lt;Directory&gt;</c> süzgeci buna bakar.</summary>
    public string PhysicalDirectory { get; init; } = string.Empty;

    public string Method { get; init; } = "GET";

    // Bkz. HtaccessModules.Default -- IReadOnlySet<> net48'de yok.
    public HashSet<string> LoadedModules { get; init; } = HtaccessModules.Default;
}

/// <summary><c>AuthType</c>/<c>AuthName</c>/<c>AuthUserFile</c> üçlüsü.</summary>
internal sealed class HtaccessAuthSettings
{
    public string Type { get; set; } = string.Empty;
    public string Realm { get; set; } = string.Empty;
    public string UserFile { get; set; } = string.Empty;
    public int Line { get; set; }
    public string File { get; set; } = string.Empty;

    public bool IsConfigured =>
        Type.Equals("Basic", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(UserFile);
}

/// <summary>
/// Basamaklandırma birleştirildikten sonraki ÇÖZÜLMÜŞ yapılandırma.
/// </summary>
internal sealed class HtaccessConfiguration
{
    /// <summary><c>RewriteEngine</c>. Hiç yazılmadıysa <c>null</c> (Apache varsayılanı: kapalı).</summary>
    public bool? RewriteEngine { get; set; }

    public List<HtaccessRewriteRule> Rules { get; } = [];

    public List<HtaccessRedirectRule> Redirects { get; } = [];

    /// <summary><c>DirectoryIndex</c>. Hiç yazılmadıysa <c>null</c>.</summary>
    public List<string>? DirectoryIndex { get; set; }

    public Dictionary<int, string> ErrorDocuments { get; } = [];

    /// <summary>Seçenek adı → açık/kapalı. Hiç değinilmemiş seçenek listede YOKTUR.</summary>
    public Dictionary<string, bool> Options { get; } = new(StringComparer.OrdinalIgnoreCase);

    public HtaccessAccessRules Access { get; set; } = new();

    public List<HtaccessHeaderAction> Headers { get; } = [];

    public bool? ExpiresActive { get; set; }

    public Dictionary<string, HtaccessExpiresEntry> ExpiresByType { get; } = new(StringComparer.OrdinalIgnoreCase);

    public HtaccessExpiresEntry? ExpiresDefault { get; set; }

    public HtaccessAuthSettings Auth { get; } = new();

    public Dictionary<string, string> MimeTypes { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string DefaultCharset { get; set; } = string.Empty;

    public Dictionary<string, string> Env { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<HtaccessDiagnostic> Diagnostics { get; } = [];

    /// <summary>Basamaklandırmada gerçekten uygulanan dosyalar, kökten içeriye.</summary>
    public List<string> AppliedFiles { get; } = [];

    /// <summary>Ölümcül hatası olan bir dosya var mı? (Apache 500 dönerdi.)</summary>
    public bool HasFatal { get; set; }

    public HtaccessOptionsState ToOptionsState() => new()
    {
        Indexes = Ucdurum("Indexes"),
        FollowSymLinks = Ucdurum("FollowSymLinks"),
        SymLinksIfOwnerMatch = Ucdurum("SymLinksIfOwnerMatch"),
        MultiViews = Ucdurum("MultiViews"),
        ExecCgi = Ucdurum("ExecCGI"),
        Includes = Ucdurum("Includes")
    };

    public HtaccessExpiresPolicy ToExpiresPolicy() => new()
    {
        Active = ExpiresActive ?? false,
        ByType = new Dictionary<string, HtaccessExpiresEntry>(ExpiresByType, StringComparer.OrdinalIgnoreCase),
        Default = ExpiresDefault
    };

    private bool? Ucdurum(string ad) => Options.TryGetValue(ad, out var deger) ? deger : null;
}
