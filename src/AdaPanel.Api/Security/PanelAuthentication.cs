using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace AdaPanel.Api.Security;

/// <summary>
/// Panelin İKİ kimlik yolunun (oturum çerezi / kişisel API anahtarı) TEK karar noktası.
/// </summary>
/// <remarks>
/// <para>
/// <b>Bulunan hata:</b> <c>PanelToken</c> şeması YALNIZCA <c>/mcp</c> ucuna bağlıydı. <c>/api/v1/**</c>
/// controller'larının hiçbiri <c>AuthenticationSchemes</c> belirtmediği için varsayılan ÇEREZ şeması
/// kullanılıyor, <c>Authorization: Bearer adap_…</c> başlığı HİÇ OKUNMUYORDU. Sonuç: panelin
/// "kişisel API anahtarı" özelliği fiilen yalnızca yapay zeka istemcisi içindi; hiçbir dış entegrasyon
/// (WHMCS modülü, müşterinin kendi betikleri, izleme aracı) REST API'yi anahtarla kullanamıyordu —
/// WHMCS modülü bu yüzden çerez oturumu açmak zorunda kalmış ve dakikada 5 istekle sınırlı
/// <c>login</c> politikasına takılmıştı.
/// </para>
/// <para>
/// <b>Neden politika şeması (policy scheme), neden her controller'a <c>AuthenticationSchemes</c>
/// yazmak DEĞİL:</b> panelde 50'nin üzerinde controller var. Her birine iki şema adı yazmak,
/// yarın eklenecek controller'da unutulduğunda sessizce eski davranışa (yalnız çerez) dönerdi —
/// yani düzeltme kendi kendini bozardı. Politika şeması varsayılan şemadır ve isteğin TAŞIDIĞI
/// kimliğe bakarak doğru şemaya yönlendirir; tek kayıt bütün uçları kapsar.
/// </para>
/// <para>
/// <b>Neden çerez varsayılan taraf:</b> <c>Authorization</c> başlığı tarayıcı tarafından
/// KENDİLİĞİNDEN gönderilmez. Yani arayüzden gelen hiçbir istek bu yola düşmez ve panel arayüzünün
/// davranışı değişmez. Başlık varsa istemci kimliğini AÇIKÇA seçmiş demektir; o zaman anahtar yolu
/// kullanılır (çerez de olsa).
/// </para>
/// </remarks>
public static class PanelAuthentication
{
    /// <summary>
    /// Varsayılan şema: yönlendirici. Gerçek doğrulamayı çerez ya da anahtar şeması yapar.
    /// </summary>
    public const string SchemeName = "AdaPanel";

    private const string BearerPrefix = "Bearer ";

    /// <summary>
    /// Anahtarların ortak öneki (<c>ApiTokenService.TokenPrefix</c> ile aynı). Yalnız "Bearer"
    /// aranmıyor: ileride başka bir bearer türü (OAuth vb.) eklenirse bu şema onu sahiplenmesin.
    /// </summary>
    private const string TokenPrefix = "adap_";

    /// <summary>
    /// Durum DEĞİŞTİRMEYEN HTTP fiilleri. Beyaz liste: bilinmeyen bir fiil "değiştiriyor" sayılır.
    /// <para>
    /// Tek yerde tutuluyor çünkü panelde bu ayrımı ÜÇ ayrı kural soruyor: abonelik askısı
    /// (<see cref="SubscriptionSuspension"/>), CSRF doğrulaması (<see cref="PanelAntiforgeryFilter"/>)
    /// ve anahtar yetenekleri (<see cref="PanelTokenCapabilities"/>). Üç kopya olsaydı biri
    /// güncellenip diğerleri unutulurdu.
    /// </para>
    /// </summary>
    private static readonly string[] SafeMethods = ["GET", "HEAD", "OPTIONS", "TRACE"];

    /// <summary>İstek durum değiştiriyor mu?</summary>
    public static bool IsStateChanging(string? httpMethod) =>
        !SafeMethods.Contains((httpMethod ?? string.Empty).Trim(), StringComparer.OrdinalIgnoreCase);

    /// <summary>İstekteki düz metin panel anahtarı; yoksa null.</summary>
    public static string? ExtractToken(HttpRequest request)
    {
        if (!request.Headers.TryGetValue("Authorization", out var header))
        {
            return null;
        }

        var raw = header.ToString();
        if (!raw.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var presented = raw[BearerPrefix.Length..].Trim();
        return presented.StartsWith(TokenPrefix, StringComparison.Ordinal) ? presented : null;
    }

    /// <summary>İstek kişisel API anahtarı taşıyor mu?</summary>
    public static bool CarriesToken(HttpRequest request) => ExtractToken(request) is not null;

    /// <summary>
    /// Bu isteği hangi şema doğrulayacak. <c>PolicySchemeOptions.ForwardDefaultSelector</c>'a bağlanır.
    /// </summary>
    public static string SelectScheme(HttpContext context) =>
        CarriesToken(context.Request)
            ? PanelTokenAuthenticationHandler.SchemeName
            : CookieAuthenticationDefaults.AuthenticationScheme;

    /// <summary>
    /// Kimlik ANAHTARLA mı doğrulandı? Başlığa değil DOĞRULANMIŞ kimliğe bakılır: "başlık var"
    /// ile "anahtar geçerli" aynı şey değildir ve CSRF muafiyeti gibi kararlar ikincisine
    /// dayanmalıdır.
    /// </summary>
    public static bool IsTokenAuthenticated(ClaimsPrincipal? user) =>
        user?.Identity is { IsAuthenticated: true, AuthenticationType: PanelTokenAuthenticationHandler.SchemeName };

    /// <summary>
    /// Hız sınırı bölümleme anahtarı.
    /// <para>
    /// Anahtarlı istekler ANAHTAR başına bölümlenir, IP başına değil: sağlayıcılar paylaşımlı çıkış
    /// adresleri kullanır ve bir müşterinin trafiği diğerini kilitlememelidir. Anahtarın kendisi
    /// değil ÖZETİ kullanılır — bölümleme anahtarları tanılama/günlük yüzeylerinde görünebilir ve
    /// oraya kimlik bilgisi taşımak gereksiz bir risktir.
    /// </para>
    /// </summary>
    public static string RateLimitPartitionKey(HttpContext context)
    {
        var token = ExtractToken(context.Request);
        if (token is null)
        {
            return "ip:" + (context.Connection.RemoteIpAddress?.ToString() ?? "unknown");
        }

        return "token:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)))[..16];
    }
}
