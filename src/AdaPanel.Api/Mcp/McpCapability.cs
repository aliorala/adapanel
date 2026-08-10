using System.Security.Claims;
using AdaPanel.Api.Security;
using AdaPanel.Core.Common;
using AdaPanel.Core.Interfaces;
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

    /// <summary>
    /// DNS araçlarının kapısı: yönetici ya da bayi.
    /// <para>
    /// Neden: aynı yeteneğin REST kapısı olan <c>DnsController</c>
    /// <c>[Authorize(Roles = "Administrator,Reseller")]</c> ile korunuyor — DNS bölgesi bir sunucu
    /// yeteneğidir ve müşteriye açılmamış. MCP tarafında bu denetim yoktu: bir MÜŞTERİ anahtarı,
    /// panelin REST üzerinden hiç vermediği bölge okuma/yazma yeteneğine erişebiliyordu (MX'i
    /// değiştirmek postayı kesmek, A'yı değiştirmek trafiği kaçırmak demektir). Aynı yeteneğin iki
    /// kapısı varsa ikisi de aynı kilidi taşımalı.
    /// </para>
    /// </summary>
    public static void RequireDnsRole(ClaimsPrincipal user)
    {
        if (!user.IsInRole("Administrator") && !user.IsInRole("Reseller"))
        {
            throw new McpProtocolException(
                "DNS bölgesi yönetimi yalnızca yönetici ve bayi hesaplarına açıktır; panelin REST " +
                "arayüzünde de aynı kısıt geçerli.",
                McpErrorCode.InvalidRequest);
        }
    }

    /// <summary>
    /// OKUYAN araçların ortak kapısı: alt-kullanıcının BÖLÜM İZNİ.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Bulunan hata:</b> bölüm izni <c>RequireSubUserSectionAttribute</c> ile uygulanıyordu ve o
    /// bir MVC filtresidir — <c>/mcp</c> MVC boru hattından GEÇMEZ. Ölçüldü: bölümleri kısıtlanmış
    /// bir alt-kullanıcı, kendi API anahtarıyla <c>sites_list</c>/<c>site_health</c> gibi araçlara
    /// erişebiliyordu; panelde kapatılan kapı MCP'de açıktı.
    /// </para>
    /// <para>
    /// Karar <see cref="CustomerAccess.EnsureSectionAsync"/> içinde TEK yerde; burada yalnızca
    /// MCP'nin hata biçimine çevriliyor.
    /// </para>
    /// </remarks>
    public static async Task EnsureReadAllowedAsync(
        ICustomerScopeService scopes,
        ClaimsPrincipal user,
        string section,
        CancellationToken cancellationToken)
    {
        try
        {
            await CustomerAccess.EnsureSectionAsync(scopes, user, section, cancellationToken);
        }
        catch (AppException)
        {
            // Neden özel durum çevrilir: araçtan dönen normal bir "hata" nesnesi modele metin olarak
            // verilir ve model devam eder; burada istenen, işlemin YAPILMAMASI ve açıkça bildirilmesi.
            throw new McpProtocolException(
                $"Bu kullanıcının '{section}' bölümüne erişim izni yok. İzni abonelik sahibi verir.",
                McpErrorCode.InvalidRequest);
        }
    }

    /// <summary>
    /// YAZAN araçların ortak kapısı: abonelik askısı + alt-kullanıcının bölüm izni.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Bulunan hata:</b> <c>SubscriptionSuspensionFilter</c> bir MVC filtresidir, <c>/mcp</c> ise
    /// MVC boru hattından geçmez. Yani askıya alınmış bir abonelik, panelde "Başlat" düğmesine
    /// basamazken <c>AdaPanelWriteTools</c> üzerinden AYNI işi yapmaya devam edebiliyordu — abonelik
    /// iş modelinin yaptırımı yapay zeka kapısından delinebiliyordu.
    /// </para>
    /// <para>
    /// <b>Neden uç seviyesinde toptan engel değil:</b> <c>/mcp</c> tek bir JSON-RPC ucudur; gövde
    /// okunmadan okuma ile yazma ayrılamaz. Bu yüzden kapı her YAZAN aracın ilk satırında.
    /// </para>
    /// <para>
    /// Karar <see cref="SubscriptionSuspension"/> içinde tek yerde. Yöntem olarak "POST", yol olarak
    /// gerçek istek yolu "/mcp" verilir: kural durum değiştiren isteklere bakar ve MCP yazma aracı
    /// tanımı gereği durum değiştirir.
    /// </para>
    /// </remarks>
    public static async Task EnsureWriteAllowedAsync(
        ICustomerScopeService scopes,
        ClaimsPrincipal user,
        string section,
        CancellationToken cancellationToken)
    {
        try
        {
            await SubscriptionSuspension.EnsureAllowedAsync(scopes, user, "POST", "/mcp", cancellationToken);
        }
        catch (AppException exception)
        {
            throw new McpProtocolException(
                "Bu abonelik askıya alınmış; yalnızca okuma yapılabilir. Sebep: " +
                (string.IsNullOrWhiteSpace(exception.Details) ? "bilinmiyor" : exception.Details) +
                ". Askıyı panel yöneticisi kaldırır.",
                McpErrorCode.InvalidRequest);
        }

        await EnsureReadAllowedAsync(scopes, user, section, cancellationToken);
    }
}
