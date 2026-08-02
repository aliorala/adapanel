using System.Security.Claims;
using AdaPanel.Api.Security;
using ModelContextProtocol;

namespace AdaPanel.Api.Mcp;

/// <summary>
/// Yıkıcı araçların yetki kapısı.
/// <para>
/// Kural: bir aracın yıkıcı olup olmadığı MODELİN kararına bırakılamaz. Yetki, isteği yapan
/// ANAHTARIN yeteneğinden okunur ve araç gövdesinin İLK satırında zorlanır. Anahtar bu yetkiyi
/// taşımıyorsa araç hiç çalışmaz.
/// </para>
/// <para>
/// Neden özel durum fırlatılıyor: MCP'de araçtan dönen normal bir "hata" nesnesi modele metin
/// olarak verilir ve model devam eder. Burada istenen tam tersi — işlem YAPILMAMALI ve bunun
/// açıkça bildirilmesi gerekir.
/// </para>
/// </summary>
internal static class McpCapability
{
    /// <summary>Silme/üzerine yazma/servis durdurma gerektiren araçların başında çağrılır.</summary>
    public static void RequireDestructive(ClaimsPrincipal user)
    {
        if (user.FindFirst(PanelTokenAuthenticationHandler.AllowDestructiveClaim)?.Value != "true")
        {
            throw new McpProtocolException(
                "Bu anahtar yıkıcı işlemlere izin vermiyor. Panelde Yapay Zeka Bağlantısı ekranından " +
                "yeni bir anahtar oluşturup 'yıkıcı işlemlere izin ver' seçeneğini açın.",
                McpErrorCode.InvalidRequest);
        }
    }

    /// <summary>
    /// Sunucuda komut çalıştıran araçların başında çağrılır. AYRI bir yetki: bu, paneli değil
    /// SUNUCUYU vermektir ve yalnız yönetici anahtarında etkinleştirilebilir.
    /// </summary>
    public static void RequireTerminal(ClaimsPrincipal user)
    {
        if (user.FindFirst(PanelTokenAuthenticationHandler.AllowTerminalClaim)?.Value != "true")
        {
            throw new McpProtocolException(
                "Bu anahtar sunucuda komut çalıştırmaya izin vermiyor. Bu yetki yalnızca yönetici " +
                "anahtarlarında ve açıkça etkinleştirildiğinde kullanılabilir.",
                McpErrorCode.InvalidRequest);
        }
    }
}
