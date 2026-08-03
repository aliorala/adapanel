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
    /// <summary>
    /// Sunucu GENELİ bilgi döndüren araçların başında çağrılır.
    /// <para>
    /// Neden gerekli: aynı bilgiyi veren REST uçları <c>[Authorize(Roles = "Administrator")]</c> ile
    /// korunuyor. MCP tarafında bu denetim yapılmazsa bir müşteri anahtarı, panelin REST üzerinden
    /// vermediği sunucu geneli bilgiyi (SSL bitiş tarihleri, güvenlik ayarları, bekleyen
    /// güncellemeler, disk doluluğu) okuyabilir — yani saldırı yüzeyi haritası. Aynı yeteneğin iki
    /// kapısı varsa ikisi de aynı kilidi taşımalı.
    /// </para>
    /// </summary>
    public static void RequireAdministrator(ClaimsPrincipal user)
    {
        if (!user.IsInRole("Administrator"))
        {
            throw new McpProtocolException(
                "Bu araç sunucu geneli bilgi döndürür ve yalnızca yönetici hesabına ait anahtarlarla kullanılabilir.",
                McpErrorCode.InvalidRequest);
        }
    }

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
