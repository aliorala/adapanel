using System.Text;
using AdaPanel.Core.Dtos.Htaccess;

namespace AdaPanel.Infrastructure.Htaccess;

/// <summary>
/// Bir isteğin mod_rewrite değerlendirmesi boyunca DEĞİŞEN durumu.
/// </summary>
/// <remarks>
/// mod_rewrite'ta URL yolu ve sorgu dizesi kural kural değişir; <c>%{REQUEST_URI}</c> ve
/// <c>%{REQUEST_FILENAME}</c> her turda YENİDEN hesaplanır. Bu yüzden değişkenler istekten değil,
/// bu durumdan okunur.
/// </remarks>
internal sealed class HtaccessRewriteState
{
    public required HtaccessRequestContext Request { get; init; }

    /// <summary>Belge köküne göre, başında <c>/</c> olan güncel URL yolu.</summary>
    public string CurrentPath { get; set; } = "/";

    public string CurrentQuery { get; set; } = string.Empty;

    /// <summary><c>[E=...]</c> ve <c>SetEnv</c> ile yazılan değişkenler.</summary>
    public Dictionary<string, string> Env { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary><c>$0..$9</c> — son eşleşen <c>RewriteRule</c> deseninin yakalamaları.</summary>
    public string[] RuleBackrefs { get; set; } = new string[10];

    /// <summary><c>%0..%9</c> — son eşleşen <c>RewriteCond</c> deseninin yakalamaları.</summary>
    public string[] CondBackrefs { get; set; } = new string[10];
}

/// <summary>
/// <c>%{...}</c> sunucu değişkenlerinin çözücüsü.
/// </summary>
/// <remarks>
/// <para>
/// <b>Bilinmeyen değişken BOŞ DÖNER ve BİLDİRİLİR.</b> Apache de bilinmeyen değişken için boş
/// dizge verir, ama sessizce; burada bulgu üretilir çünkü <c>%{HTTP_X_FORWARDED_PROTO}</c> gibi
/// bir yazımın neden hiç eşleşmediğini kullanıcının görmesi gerekir.
/// </para>
/// <para>
/// <b>APACHE'DEN SAPMALAR (bilinçli):</b>
/// <list type="bullet">
/// <item><c>REQUEST_URI</c> GÜNCEL yolu verir; Apache'de iç yönlendirmeden önce özgün URI'dir.
/// Uygulamada fark yalnızca aynı turda yeniden yazılmış bir yolu okuyan kurallarda görülür.</item>
/// <item><c>PATH_INFO</c> her zaman boştur: IIS yol bilgisini ayrı çözer.</item>
/// <item><c>REMOTE_HOST</c>, <c>SERVER_ADDR</c>, <c>REMOTE_PORT</c>, <c>REMOTE_IDENT</c>
/// desteklenmez (ters DNS / soket bilgisi motorun elinde yok).</item>
/// </list>
/// </para>
/// </remarks>
internal static class HtaccessServerVariables
{
    private static readonly HashSet<string> Desteklenmeyen = new(StringComparer.OrdinalIgnoreCase)
    {
        "REMOTE_HOST", "REMOTE_PORT", "REMOTE_IDENT", "SERVER_ADDR", "SERVER_ADMIN",
        "API_VERSION", "CONN_REMOTE_ADDR", "SSL_CLIENT_S_DN", "SSL_CIPHER", "SSL_PROTOCOL"
    };

    public static string Resolve(string name, HtaccessRewriteState state, List<HtaccessDiagnostic> bulgular,
        string file, int line)
    {
        var istek = state.Request;

        if (name.StartsWith("HTTP:", StringComparison.OrdinalIgnoreCase))
        {
            return Baslik(istek, name[5..]);
        }

        if (name.StartsWith("ENV:", StringComparison.OrdinalIgnoreCase))
        {
            return state.Env.TryGetValue(name[4..], out var deger) ? deger : string.Empty;
        }

        if (name.StartsWith("LA-U:", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("LA-F:", StringComparison.OrdinalIgnoreCase))
        {
            // Alt-istek (subrequest) ile ileriye bakma. Motor alt-istek çalıştırmaz.
            Bildir(bulgular, file, line, name,
                "ileriye bakma (LA-U/LA-F) desteklenmiyor: alt-istek çalıştırılmıyor. Boş dizge kullanıldı.");
            return string.Empty;
        }

        if (Desteklenmeyen.Contains(name))
        {
            Bildir(bulgular, file, line, name,
                $"%{{{name}}} desteklenmiyor; boş dizge kullanıldı. Bu değişkene bağlı koşul BEKLENDİĞİ GİBİ çalışmaz.");
            return string.Empty;
        }

        return name.ToUpperInvariant() switch
        {
            "REQUEST_URI" => state.CurrentPath,
            "REQUEST_FILENAME" or "SCRIPT_FILENAME" => FizikselYol(istek.SiteRootPath, state.CurrentPath),
            "DOCUMENT_ROOT" => istek.SiteRootPath,
            "QUERY_STRING" => state.CurrentQuery,
            "REQUEST_METHOD" => istek.Method,
            "REQUEST_SCHEME" => istek.IsHttps ? "https" : "http",
            "HTTPS" => istek.IsHttps ? "on" : "off",
            "HTTP_HOST" => istek.HttpHost,
            "SERVER_NAME" => string.IsNullOrEmpty(istek.ServerName) ? HostAdi(istek.HttpHost) : istek.ServerName,
            "SERVER_PORT" => istek.ServerPort.ToString(),
            "SERVER_PROTOCOL" => "HTTP/1.1",
            "SERVER_SOFTWARE" => "AdaPanel",
            "REMOTE_ADDR" => istek.RemoteAddress,
            "REMOTE_USER" => istek.RemoteUser,
            "AUTH_TYPE" => string.IsNullOrEmpty(istek.RemoteUser) ? string.Empty : "Basic",
            "HTTP_USER_AGENT" => Baslik(istek, "User-Agent"),
            "HTTP_REFERER" => Baslik(istek, "Referer"),
            "HTTP_COOKIE" => Baslik(istek, "Cookie"),
            "HTTP_ACCEPT" => Baslik(istek, "Accept"),
            "HTTP_FORWARDED" => Baslik(istek, "Forwarded"),
            "HTTP_PROXY_CONNECTION" => Baslik(istek, "Proxy-Connection"),
            "CONTENT_TYPE" => Baslik(istek, "Content-Type"),
            "CONTENT_LENGTH" => Baslik(istek, "Content-Length"),
            "THE_REQUEST" => TamIstek(istek),
            "PATH_INFO" => string.Empty,
            "IS_SUBREQ" => "false",
            "TIME_YEAR" => DateTime.Now.ToString("yyyy"),
            "TIME_MON" => DateTime.Now.ToString("MM"),
            "TIME_DAY" => DateTime.Now.ToString("dd"),
            "TIME_HOUR" => DateTime.Now.ToString("HH"),
            "TIME_MIN" => DateTime.Now.ToString("mm"),
            "TIME_SEC" => DateTime.Now.ToString("ss"),
            "TIME_WDAY" => ((int)DateTime.Now.DayOfWeek).ToString(),
            "TIME" => DateTime.Now.ToString("yyyyMMddHHmmss"),
            _ => Bilinmeyen(name, bulgular, file, line)
        };
    }

    /// <summary>URL yolunu site kökü altındaki fiziksel yola çevirir.</summary>
    public static string FizikselYol(string siteRoot, string urlPath)
    {
        var goreli = urlPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        if (goreli.Length == 0)
        {
            return siteRoot;
        }

        try
        {
            return Path.Combine(siteRoot, goreli);
        }
        catch (ArgumentException)
        {
            // Geçersiz karakter içeren yol: birleştirme yapılamaz. Boş dönmek, -f/-d testlerinin
            // "yok" demesini sağlar; istisna sızmaz.
            return string.Empty;
        }
    }

    /// <summary>
    /// <c>%{THE_REQUEST}</c> — istek satırının kendisi. ÖZGÜN yoldan kurulur, yeniden yazılmış
    /// yoldan değil; Apache'de de bu değişken hiç değişmez.
    /// </summary>
    private static string TamIstek(HtaccessRequestContext istek)
    {
        var yol = new StringBuilder(istek.RequestPath);
        if (!string.IsNullOrEmpty(istek.QueryString))
        {
            yol.Append('?').Append(istek.QueryString);
        }

        return $"{istek.Method} {yol} HTTP/1.1";
    }

    private static string Baslik(HtaccessRequestContext istek, string ad) =>
        istek.Headers.TryGetValue(ad, out var deger) ? deger : string.Empty;

    private static string HostAdi(string host)
    {
        if (string.IsNullOrEmpty(host))
        {
            return string.Empty;
        }

        var ikiNokta = host.LastIndexOf(':');
        return ikiNokta > 0 && !host.Contains(']') ? host[..ikiNokta] : host;
    }

    private static string Bilinmeyen(string name, List<HtaccessDiagnostic> bulgular, string file, int line)
    {
        Bildir(bulgular, file, line, name,
            $"%{{{name}}} tanınmayan bir sunucu değişkeni; boş dizge kullanıldı. Bu değişkene bağlı koşul BEKLENDİĞİ GİBİ çalışmaz.");
        return string.Empty;
    }

    private static void Bildir(List<HtaccessDiagnostic> bulgular, string file, int line, string name, string mesaj)
    {
        // Aynı değişken bir dosyada onlarca kez geçebilir; paneli gürültüye boğmamak için
        // değişken başına TEK bulgu tutulur.
        if (bulgular.Any(t => t.Code == HtaccessDiagnosticCodes.UnsupportedVariable && t.Directive == name))
        {
            return;
        }

        bulgular.Add(HtaccessParser.Bulgu(HtaccessDiagnosticLevel.Unsupported,
            HtaccessDiagnosticCodes.UnsupportedVariable, file, line, name, mesaj));
    }
}
