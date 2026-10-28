using AdaPanel.Api.Security;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Interfaces;

namespace AdaPanel.Api.Middleware;

public sealed class SecureEntryPathMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, ISetupService setupService, IPanelSecurityService securityService)
    {
        var path = context.Request.Path;
        if (IsAlwaysPublicPath(path) || IsDirectWebmailBrokerRequest(context))
        {
            await next(context);
            return;
        }

        var setupStatus = await setupService.GetStatusAsync(context.RequestAborted);
        if (!setupStatus.IsInitialized)
        {
            await next(context);
            return;
        }

        var isAllowedIp = await securityService.IsIpAllowedAsync(context.Connection.RemoteIpAddress?.ToString(), context.RequestAborted);
        if (!isAllowedIp)
        {
            await RejectApiRequestAsync(context, StatusCodes.Status403Forbidden, "security.ip_not_allowed");
            return;
        }

        // MCP ucu: kimlik doğrulaması bearer anahtarıyla yapılır. Gizli giriş yolu başlığını burada
        // zorunlu kılmak MCP istemcisini gereksiz kırılganlaştırır — anahtar zaten yüksek entropili
        // bir sırdır ve tek tek iptal edilebilir. IP kısıtlaması ise YUKARIDA uygulanmış durumda,
        // yani kaldırılmıyor.
        //
        // NOT: IP kısıtlaması kullanılıyorsa yapay zeka sağlayıcısının çıkış adresleri listeye
        // eklenmelidir; aksi hâlde bağlantı 403 alır.
        if (path.StartsWithSegments("/mcp", out _))
        {
            await next(context);
            return;
        }

        if (path.StartsWithSegments("/api", out _))
        {
            if (RequiresSecureEntryHeader(path) && !CarriesApiToken(context.Request))
            {
                var expectedPath = await securityService.GetSecureEntryPathAsync(context.RequestAborted);
                var providedPath = context.Request.Headers["X-AdaPanel-Entry-Path"].ToString().Trim().Trim('/').ToLowerInvariant();

                if (!string.Equals(providedPath, expectedPath, StringComparison.OrdinalIgnoreCase))
                {
                    await RejectApiRequestAsync(context, StatusCodes.Status404NotFound, "security.invalid_entry_path");
                    return;
                }
            }

            await next(context);
            return;
        }

        var secureEntryPath = await securityService.GetSecureEntryPathAsync(context.RequestAborted);
        if (!path.HasValue || path == "/")
        {
            context.Response.Redirect($"/{secureEntryPath}");
            return;
        }

        if (path.StartsWithSegments($"/{secureEntryPath}", out var remainder))
        {
            context.Request.Path = remainder.HasValue ? remainder : "/";
            await next(context);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status404NotFound;
    }

    // The private webmail broker uses a dedicated pool-only key instead of the browser's
    // entry header. Do not turn its entire route prefix, forwarded requests or remote
    // localhost Host headers into a public exemption. The controller checks the key again.
    internal static bool IsDirectWebmailBrokerRequest(HttpContext context) =>
        HttpMethods.IsPost(context.Request.Method)
        && (string.Equals(context.Request.Path.Value, "/api/internal/webmail-sso/consume", StringComparison.OrdinalIgnoreCase)
            || string.Equals(context.Request.Path.Value, "/api/internal/webmail-sso/revoke", StringComparison.OrdinalIgnoreCase))
        && context.Connection.RemoteIpAddress is { } remote && System.Net.IPAddress.IsLoopback(remote)
        && context.Connection.LocalIpAddress is { } local && System.Net.IPAddress.IsLoopback(local)
        && context.Request.Host.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        && !context.Request.Headers.Keys.Any(name => name.StartsWith("X-Forwarded-", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Forwarded", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Gizli giriş yolu (ve IP) denetiminden tamamen muaf yollar.
    /// <para>
    /// <c>internal</c>: muafiyet listesinin İÇERİĞİ test edilebilmeli. Bir yolun listede olduğunu
    /// varsaymak, o yolun sessizce 404 döndüğünü fark etmemek demektir.
    /// </para>
    /// </summary>
    internal static bool IsAlwaysPublicPath(PathString path)
    {
        return path.StartsWithSegments("/assets", out _)
            || path.StartsWithSegments("/swagger", out _)
            || path.StartsWithSegments("/favicon", out _)
            || path.StartsWithSegments("/robots.txt", out _)
            || path.StartsWithSegments("/index.html", out _)
            || path.StartsWithSegments("/api/v1/setup", out _)
            || path.StartsWithSegments("/api/v1/health", out _)
            || IsMailAutoconfigPath(path)
            || IsMailListUnsubscribePath(path)
            || IsSiteBuilderContactFormPath(path);
    }

    /// <summary>
    /// Site kurucunun ürettiği iletişim formunun gönderim ucu.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Neden muaf:</b> bu ucu çağıran kişi panelin kullanıcısı DEĞİLDİR — müşterinin sitesini
    /// ziyaret eden biridir ve isteği, o sitedeki bir <c>&lt;form&gt;</c>'un düz POST'uyla yapar.
    /// Gizli giriş yolu başlığını gönderemez (form gönderimine başlık eklenemez); muafiyet
    /// olmasaydı uç eklenmiş ama HER gönderimde 404 dönüyor olurdu — yani form var görünüp hiç
    /// çalışmazdı ve bunu yalnız ziyaretçi görürdü. Abonelikten çıkma ucuyla aynı gerekçe.
    /// </para>
    /// <para>
    /// IP kısıtlamasını da atlaması KASITLIDIR ve aynı sebebe dayanır: ziyaretçi, panel
    /// yöneticisinin izin listesindeki bir IP'den değil kendi rastgele adresinden bağlanır.
    /// </para>
    /// <para>
    /// <b>Muafiyetin kapsamı DAR:</b> yalnız <c>/api/v1/public/site-forms</c> segmenti.
    /// Arkasındaki denetleyici TEK bir şey yapar (site SAHİBİNİN kaydettiği adrese bir posta
    /// gönderir); site listelemez, tasarım okutmaz, "bu site var mı" sorusunu cevaplamaz ve
    /// IP/site/gönderen başına hız sınırlıdır. <c>StartsWithSegments</c> segment sınırına baktığı
    /// için başka bir <c>/api/v1/public…</c> ucu bu muafiyete SIZAMAZ.
    /// </para>
    /// </remarks>
    internal static bool IsSiteBuilderContactFormPath(PathString path) =>
        path.StartsWithSegments(AdaPanel.Infrastructure.SiteBuilder.SiteBuilderContactEndpoint.RoutePrefix, out _);

    /// <summary>
    /// Dağıtım listesinden self-servis çıkma ucu.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Neden muaf:</b> bu ucu çağıran kişi panelin kullanıcısı DEĞİLDİR — listeden çıkmak
    /// isteyen bir abonedir ve isteği ya e-postadaki bağlantıya tıklayarak ya da genel bir formdan
    /// yapar. Gizli giriş yolu başlığını gönderemez; muafiyet olmasaydı uç eklenmiş ama HER istekte
    /// 404 dönüyor olurdu — yani özellik var görünüp hiç çalışmazdı. Posta istemcisi otomatik
    /// yapılandırma yollarıyla aynı gerekçe.
    /// </para>
    /// <para>
    /// IP kısıtlamasını da atlaması KASITLIDIR ve aynı sebebe dayanır: abone, panel yöneticisinin
    /// izin listesindeki bir IP'den değil, kendi rastgele adresinden bağlanır.
    /// </para>
    /// <para>
    /// <b>Muafiyetin kapsamı DAR:</b> yalnız <c>/api/v1/public/mail-lists</c> segmenti. Arkasındaki
    /// denetleyici tek bir şey yapar (bir adresi bir listeden çıkarır), liste/abone LİSTELEMEZ ve
    /// "bu adres abone mi" sorusunu cevaplamaz; çıkarma ise yalnız o adrese gönderilen, tek
    /// kullanımlık ve süreli bir belirteçle mümkündür. <c>StartsWithSegments</c> segment sınırına
    /// baktığı için başka bir <c>/api/v1/public…</c> ucu bu muafiyete SIZAMAZ.
    /// </para>
    /// </remarks>
    internal static bool IsMailListUnsubscribePath(PathString path) =>
        path.StartsWithSegments("/api/v1/public/mail-lists", out _);

    /// <summary>
    /// Posta istemcisi otomatik yapılandırma yolları (Outlook autodiscover / Thunderbird autoconfig).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Bu yollar SABİTTİR: istemciler tam olarak bu adresleri çağırır, gizli giriş yolunun
    /// arkasına alınamaz ve özel bir başlık gönderemezler. Muafiyet yazılmasaydı uçlar eklenmiş
    /// ama HER istekte 404 dönüyor olurdu — yani özellik var görünüp hiç çalışmazdı.
    /// </para>
    /// <para>
    /// Muafiyet IP kısıtlamasını da atlar ve bu KASITLIDIR: posta istemcileri son kullanıcının
    /// rastgele IP'sinden bağlanır, panel yöneticisinin izin listesinden değil. Yeni bir yüzey
    /// açmıyor — arkasındaki denetleyici salt-okunurdur, durum değiştirmez, kimlik/sır taşımaz ve
    /// yalnızca panelde TANIMLI alan adları için ad/port/şifreleme döner; tanımsız alan 404 alır.
    /// </para>
    /// </remarks>
    internal static bool IsMailAutoconfigPath(PathString path) =>
        path.StartsWithSegments("/autodiscover", out _)
        || path.StartsWithSegments("/.well-known/autoconfig", out _)
        || string.Equals(path.Value, "/mail/config-v1.1.xml", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Gizli giriş yolu başlığı ZORUNLU MU?
    /// <para>
    /// Kural TERSİNE çevrildi: <c>/api</c> altındaki HER ŞEY başlık ister, yalnız aşağıdaki kısa
    /// muafiyet listesi istemez. Önceki hâli bunun tersiydi — korunan uçlar tek tek sayılıyordu ve
    /// listeye eklenmeyen her YENİ denetleyici sessizce korumasız kalıyordu. Denetimde tam olarak bu
    /// oldu: advisor, antivirus, attention, dns/server, dns/template, monitoring/*, server-health,
    /// sub-users, wordpress, migration, service-plans, dbtools, api-tokens, error-reports ve
    /// panel-access uçları listeye hiç girmemişti. Sayıma dayalı koruma, unutmayı "açık" olarak
    /// cezalandırır; kurala dayalı koruma ise en fazla gereksiz sıkılık üretir.
    /// </para>
    /// </summary>
    internal static bool RequiresSecureEntryHeader(PathString path)
    {
        // A native browser form cannot add the entry header. This exact authenticated
        // endpoint instead requires the current origin plus an actor-bound 60-second grant.
        // The ordinary panel IP policy above remains in force.
        if (path.Equals(new PathString("/api/v1/mail/webmail/handoff"))) return false;
        // TARAYICININ KENDİSİNİN GEZDİĞİ yollar başlık isteyemez.
        //
        // /api/v1/dbtools, phpMyAdmin'in TAMAMINI panelin HTTPS'i arkasından sunan bir ters
        // vekildir: HTML sayfaları, CSS/JS varlıkları, AJAX çağrıları ve form gönderimleri
        // hepsi oradan geçer. Bunlar tarayıcının kendi istekleridir; bir sayfaya gidilirken ya
        // da bir form gönderilirken özel başlık EKLENEMEZ. Kural buraya da uygulandığında
        // "phpMyAdmin'de aç" düğmesi ham bir 404 JSON'ı gösterir hâle geldi.
        //
        // Koruma kaybolmuyor: bu denetleyici [Authorize(Roles = "Administrator")] ile ÇEREZ
        // tabanlı kimlik doğrular, giriş bağlantısı tek kullanımlık yüksek entropili bir
        // belirteç taşır ve IP kısıtlaması bu metottan ÖNCE zaten uygulanmıştır. Gizli giriş
        // yolu başlığı programatik API erişimine karşı bir tarama önlemidir; tarayıcının
        // gezdiği bir yüzeyde işlevi yoktur. Aynı gerekçeyle /mcp de muaf tutulmuştu.
        if (path.StartsWithSegments("/api/v1/dbtools", out _))
        {
            return false;
        }

        // TEK KULLANIMLIK GİRİŞ BAĞLANTISI (SSO) da aynı sınıfa girer: müşteri faturalama
        // sisteminden gelen bir adrese TIKLAR, yani isteği tarayıcı gezinerek yapar ve özel bir
        // başlık ekleyemez. Kural buraya da uygulansaydı "Panele git" düğmesi ham bir 404 JSON'ı
        // gösterirdi — dbtools'ta yaşanan canlı olayın aynısı.
        //
        // Koruma kaybolmuyor: kimliği belirtecin KENDİSİ taşıyor (256 bit, TEK KULLANIMLIK,
        // dakikalık ömür, sunucuda özeti saklanıyor) ve IP kısıtlaması bu metottan ÖNCE zaten
        // uygulandı. Gizli giriş yolu başlığı programatik API taramasına karşı bir önlemdir;
        // tarayıcının gezdiği bir yüzeyde işlevi yoktur.
        //
        // Muafiyet DAR: belirteci ÜRETEN uç (/api/v1/auth/sso-link/{id}) başka bir segmenttir ve
        // kapsam dışında kalır — StartsWithSegments segment sınırına bakar, "sso-link" ile "sso"
        // eşleşmez.
        if (path.StartsWithSegments("/api/v1/auth/sso", out _))
        {
            return false;
        }

        // Anonim uclar (health, setup) daha yukarida IsAlwaysPublicPath ile ayrildi; buraya gelen
        // her /api istegi kimlige bagli veri tasir ve basligi ister.
        return path.StartsWithSegments("/api", out _);
    }

    /// <summary>
    /// API ANAHTARIYLA gelen istek gizli giriş yolu başlığından muaftır.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Gerekçe /mcp muafiyetiyle AYNI: kimlik yüksek entropili, tek tek iptal edilebilen bir
    /// anahtarla kanıtlanır. Muafiyet olmadan dış entegrasyonlar (WHMCS modülü, müşterinin kendi
    /// betikleri) gizli giriş yolunu da bilmek zorunda kalır — ve yönetici yolu değiştirdiği anda
    /// entegrasyon, sebebi görünmeyen bir 404 ile sessizce kırılır.
    /// </para>
    /// <para>
    /// <b>BEDELİ ÖLÇÜLDÜ ve kabul edildi:</b> burada yalnız başlığın BİÇİMİNE bakılır, anahtar
    /// henüz doğrulanmamıştır (doğrulama kimlik katmanında olur). Yani sahte bir
    /// <c>Authorization: Bearer adap_…</c> gönderen bir tarayıcı, eskiden aldığı 404 yerine artık
    /// 401 alır. Kaybedilen şey ucun VARLIĞININ gizlenmesidir, erişim değil — istek yine kimlik
    /// doğrulamasında durur. Aynı özellik /mcp muafiyetiyle zaten kaybedilmişti; IP kısıtlaması da
    /// bu metottan ÖNCE uygulanır ve kaldırılmıyor.
    /// </para>
    /// </remarks>
    internal static bool CarriesApiToken(HttpRequest request) => PanelAuthentication.CarriesToken(request);

    private static Task RejectApiRequestAsync(HttpContext context, int statusCode, string message)
    {
        context.Response.Clear();
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json; charset=utf-8";
        return context.Response.WriteAsJsonAsync(ApiResponse<object?>.Fail(message, statusCode));
    }
}
