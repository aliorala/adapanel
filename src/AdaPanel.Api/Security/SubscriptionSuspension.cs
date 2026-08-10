using System.Security.Claims;
using AdaPanel.Api.Extensions;
using AdaPanel.Core.Common;
using AdaPanel.Core.Interfaces;

namespace AdaPanel.Api.Security;

/// <summary>
/// Askıya alınmış aboneliğin FİİLEN bağlayıcı olmasını sağlayan tek karar noktası.
/// </summary>
/// <remarks>
/// <para>
/// <b>Bulunan hata:</b> askı yalnızca <c>customer-scopes.json</c> içindeki
/// <c>SuspendedAtUtc</c> alanına yazılıyordu ve API'de HİÇBİR YERDE sorulmuyordu. Askıya alınan
/// müşteri panele giriyor, durdurulmuş sitesini "Başlat" düğmesiyle yeniden ayağa kaldırıyordu —
/// yani abonelik iş modelinin en temel yaptırımı yoktu. Kota dolunca site duruyor, müşteri geri
/// başlatıyor, bir sonraki denetim turu tekrar durduruyordu.
/// </para>
/// <para>
/// <b>Neden çerezi düşürmüyoruz:</b> giriş akışı askıyı denetlemiyor. Çerez düşürülseydi kullanıcı
/// giriş yapar, ilk istekte atılır, tekrar giriş yapardı — sonsuz döngüye benzeyen ve sebebi hiç
/// görünmeyen bir deneyim. Ayrıca askıdaki müşterinin panele girip <i>neden</i> askıda olduğunu,
/// kotasını ve faturasını görmesi ŞART; çözümün kendisi orada.
/// </para>
/// <para>
/// <b>Kural:</b> okuma (GET/HEAD/OPTIONS/TRACE) serbest, DURUM DEĞİŞTİREN istek (POST/PUT/PATCH/
/// DELETE) 403. Yöntem listesi beyaz liste olarak yazıldı: yeni bir HTTP fiili çıkarsa varsayılan
/// "engelle" tarafına düşer.
/// </para>
/// <para>
/// <b>Kimi bağlar:</b> yalnızca askıdaki aboneliğin SAHİBİNİ (ve alt-kullanıcılarını). Yönetici ve
/// operatör hiç bağlanmaz — askıyı çözecek olan onlar. Bayi kendi müşterisini yönetmeye devam eder;
/// bayi ancak KENDİ aboneliği askıdaysa bağlanır (o zaman da bütün işi askıdadır).
/// </para>
/// </remarks>
public static class SubscriptionSuspension
{
    /// <summary>
    /// Reddin sebebi. Kullanıcı "neden çalışmıyor" diye tahmin etmemeli.
    /// <para>
    /// Anahtar, fırlatma yerinde METİN OLARAK da yazılır (sabit üzerinden geçilmez): panelin
    /// çeviri kapsamı testi (<c>MessageKeyCoverageTests</c>) kaynağı düz metin arayarak tarar,
    /// sabitten okunan bir anahtar taramada GÖRÜNMEZ ve çevirisi unutulduğunda kullanıcı ekranda
    /// ham anahtarı görür. Depodaki bütün fırlatma yerleri de aynı biçimde yazılmış.
    /// </para>
    /// </summary>
    public const string MessageKey = "subscription.suspended_readonly";

    /// <summary>
    /// Askı DIŞINDA tutulan yollar.
    /// <para>
    /// <c>/api/v1/auth</c>: oturum açma/kapatma, parola değiştirme, iki adımlı doğrulama açma/kapatma
    /// ve kurtarma kodları. Askı bir ödeme/kota durumudur, güvenlik olayı değil: müşterinin hesabını
    /// güvenceye alma ve oturumundan çıkma yeteneğini elinden almak, askının amacıyla ilgisiz bir
    /// zarardır. Bu yolların tamamı zaten kendi hız sınırı ve mevcut-parola doğrulamasıyla korunuyor.
    /// </para>
    /// <para>
    /// Panelde MÜŞTERİNİN kendi aboneliğini yeniden etkinleştirebileceği (self-servis ödeme/plan
    /// yükseltme) bir uç YOK — ölçüldü: <c>SubscriptionsController</c>'daki bütün yazma uçları
    /// <c>Administrator,Reseller</c>. Bu yüzden buraya "yeniden etkinleştirme" muafiyeti
    /// yazılmadı; olmayan bir yolu muaf tutmak, ileride o yol eklendiğinde kimsenin fark etmeyeceği
    /// sessiz bir delik bırakırdı. Böyle bir uç eklendiğinde muafiyet BURAYA eklenmeli.
    /// </para>
    /// </summary>
    private static readonly string[] ExemptPathPrefixes = ["/api/v1/auth"];

    /// <summary>
    /// İstek durum değiştiriyor mu (yani askının konusu mu)?
    /// <para>
    /// Fiil listesi burada DEĞİL <see cref="PanelAuthentication.IsStateChanging"/> içinde: aynı ayrımı
    /// CSRF doğrulaması ve anahtar yetenekleri de soruyor, üç kopya olsaydı biri güncellenip
    /// diğerleri unutulurdu.
    /// </para>
    /// </summary>
    public static bool IsStateChanging(string? httpMethod) => PanelAuthentication.IsStateChanging(httpMethod);

    /// <summary>Yol askı denetiminden muaf mı?</summary>
    public static bool IsExemptPath(string? path)
    {
        var normalized = (path ?? string.Empty).TrimEnd('/');
        return ExemptPathPrefixes.Any(prefix =>
            normalized.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Bu kullanıcı KENDİ aboneliğinin askısıyla bağlanır mı?
    /// <para>
    /// Yönetici/operatör açıkça dışarıda: askıyı kaldıracak olan onlar ve bir yönetici hesabına
    /// yanlışlıkla kapsam kaydı düşmüşse panel kilitlenmemeli.
    /// </para>
    /// </summary>
    public static bool IsBoundBySuspension(ClaimsPrincipal user) =>
        user.Identity?.IsAuthenticated == true
        && !user.IsInRole("Administrator")
        && !user.IsInRole("Operator")
        && CustomerAccess.IsScoped(user);

    /// <summary>
    /// İsteğe izin verilip verilmediğine karar verir; verilmiyorsa <see cref="MessageKey"/> ile 403 fırlatır.
    /// </summary>
    /// <remarks>
    /// HttpContext'ten bağımsız yazıldı: karar (yöntem + yol + kimlik + abonelik durumu) saf bir
    /// fonksiyon olduğu için filtre olmadan da doğrulanabiliyor.
    /// </remarks>
    public static async Task EnsureAllowedAsync(
        ICustomerScopeService scopes,
        ClaimsPrincipal user,
        string? httpMethod,
        string? path,
        CancellationToken cancellationToken)
    {
        if (!IsStateChanging(httpMethod) || IsExemptPath(path) || !IsBoundBySuspension(user))
        {
            return;
        }

        // GetScopeAsync alt-kullanıcıyı ZATEN parent'a çözer (ResolveEffective) — yani abonelik
        // altındaki ek kullanıcılar da askıya takılır. Ayrıca burada ikinci bir çözümleme yapmak,
        // aynı kuralın iki kopyası olurdu.
        var scope = await scopes.GetScopeAsync(user.GetRequiredUserId(), cancellationToken);
        if (!scope.IsSuspended)
        {
            return;
        }

        // Sebep (traffic/expired/manual) Details'e konur: arayüz zaten abonelik kartında bu
        // anahtarların çevirisini gösteriyor, kullanıcı "hangi sebeple" sorusunu tek istekte
        // yanıtlayabilsin.
        throw new AppException("subscription.suspended_readonly", 403, 403, scope.SuspendReason);
    }
}
