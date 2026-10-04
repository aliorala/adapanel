using System.Security.Claims;
using AdaPanel.Api.Security;
using AdaPanel.Api.Extensions;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Auth;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Entities;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Security;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace AdaPanel.Api.Controllers;

/// <remarks>
/// <b>YALNIZ ÇEREZ ŞEMASI.</b> <c>/api/v1/**</c> uçları artık kişisel API anahtarını da kabul ediyor;
/// oturum ve hesap güvenliği uçları kabul ETMEMELİ. Sebep yetki yükseltmesi: sınırlı bir anahtarı ele
/// geçiren biri parolayı değiştirip, iki adımlı doğrulamayı kapatıp ya da kurtarma kodlarını
/// yenileyerek hesabın tamamını devralabilirdi — anahtarın yetkisi hesabın yetkisine yükselirdi.
/// Anahtar sunucudan sunucuya iş içindir, hesap yönetimi için değil.
/// <para>
/// <c>[AllowAnonymous]</c> uçlar (giriş, csrf, oturum sorgusu) bundan etkilenmez; bir entegrasyon
/// <c>GET /api/v1/auth/session</c> ile anahtarının kimin adına çalıştığını hâlâ sorabilir.
/// </para>
/// </remarks>
[ApiController]
[Authorize(AuthenticationSchemes = CookieAuthenticationDefaults.AuthenticationScheme)]
[Route("api/v1/auth")]
public sealed class AuthController(
    IAntiforgery antiforgery,
    IAuthService authService,
    IAuditService auditService,
    IPanelSecurityService securityService,
    SessionRevocationStore sessionRevocations) : ControllerBase
{
    [AllowAnonymous]
    // ARAYUZUN ACILIS UCLARI (csrf + session) DA TAVANLI.
    //
    // Ikisi de ucuz ve arayuzun her acilisinda cagriliyor, ama kimliksiz: sinirsiz birakildiginda
    // "csrf" her istekte yeni bir antiforgery cerezi URETIR ve "session" her istekte veritabanina
    // gider. PublicRead (IP basina 300/dk) secildi cunku bu uclar OTURUM ONCESIDIR -- oturum
    // bolumlemesi (kuresel kova) burada henuz bir kimlik bulamaz, geriye IP kalir.
    // 300/dk fazlasiyla genis: arayuz jetonu ONBELLEKLIYOR (frontend/src/api/client.ts:29) ve
    // acilis basina bir kez cagiriyor; ayni NAT arkasindaki on yonetici surekli sayfa yenilese
    // bile dakikada ~20 istek eder.
    [EnableRateLimiting(PanelRateLimits.PublicRead)]
    [HttpGet("csrf")]
    public ActionResult<ApiResponse<object>> GetCsrfToken()
    {
        var tokens = antiforgery.GetAndStoreTokens(HttpContext);
        return Ok(ApiResponse<object>.Ok(new
        {
            requestToken = tokens.RequestToken ?? string.Empty
        }));
    }

    [AllowAnonymous]
    [EnableRateLimiting(PanelRateLimits.PublicRead)]
    [HttpGet("session")]
    public async Task<ActionResult<ApiResponse<CurrentSessionDto>>> GetSessionAsync(CancellationToken cancellationToken)
    {
        var session = await authService.GetCurrentSessionAsync(User, cancellationToken);

        if ((User.Identity?.IsAuthenticated ?? false) && !session.IsAuthenticated)
        {
            await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        }

        return Ok(ApiResponse<CurrentSessionDto>.Ok(session));
    }

    [AllowAnonymous]
    [EnableRateLimiting(PanelRateLimits.Login)]
    [HttpPost("login")]
    public async Task<ActionResult<ApiResponse<LoginResultDto>>> LoginAsync(
        [FromBody] LoginRequest request,
        CancellationToken cancellationToken)
    {
        var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
        var result = await authService.LoginAsync(request, ipAddress, cancellationToken);

        if (result.IsAuthenticated && result.User is not null)
        {
            await SignInAsync(result.User, request.RememberMe);
        }

        return Ok(ApiResponse<LoginResultDto>.Ok(result, "auth.login.success"));
    }

    [AllowAnonymous]
    [EnableRateLimiting(PanelRateLimits.Login)]
    [HttpPost("password/forgot")]
    public async Task<ActionResult<ApiResponse<object>>> ForgotPasswordAsync(
        [FromBody] ForgotPasswordRequest request,
        CancellationToken cancellationToken)
    {
        // Kullanıcı numaralandırmasını engellemek için her zaman aynı yanıt döner.
        await authService.RequestPasswordResetAsync(request.Email, cancellationToken);
        return Ok(ApiResponse<object>.Ok(new { }, "auth.password_reset.requested"));
    }

    [AllowAnonymous]
    [EnableRateLimiting(PanelRateLimits.Login)]
    [HttpPost("password/reset")]
    public async Task<ActionResult<ApiResponse<object>>> ResetPasswordAsync(
        [FromBody] ResetPasswordRequest request,
        CancellationToken cancellationToken)
    {
        await authService.ResetPasswordAsync(request.Token, request.NewPassword, cancellationToken);
        return Ok(ApiResponse<object>.Ok(new { }, "auth.password_reset.completed"));
    }

    /// <summary>
    /// Yönetici (ya da bayi, YALNIZ kendi müşterisi için) BAŞKA bir kullanıcının parolasını belirler.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Neden burada, <c>UsersController</c>'da değil:</b> bu bir KİMLİK BİLGİSİ işlemidir ve
    /// panelde kimlik bilgisi yöneten denetleyiciler sınıf düzeyinde ÇEREZ şemasına sabitlenmiştir
    /// (bkz. sınıf açıklaması ve <c>PanelTokenCapabilities</c>). Uç kullanıcı yönetimine konsaydı
    /// varsayılan şemayla API anahtarını da kabul ederdi: salt-okur olmayan bir anahtarı ele geçiren
    /// biri, başka bir yöneticinin parolasını belirleyip tam yetkili bir oturum açabilir, yani
    /// anahtarın kısıtı kâğıt üzerinde kalırdı. Kural şemaya bağlandığı için yeni uç eklendiğinde
    /// unutulması da mümkün değil.
    /// </para>
    /// <para>
    /// Hız sınırı parola değiştirmeyle aynı politikadan geçer: burada mevcut parola denenmiyor ama
    /// uç, hedef kullanıcı kimliklerini deneme yüzeyidir; sınır o gürültüyü ölçülebilir kılar.
    /// </para>
    /// </remarks>
    [Authorize(Roles = "Administrator,Reseller")]
    [EnableRateLimiting(PanelRateLimits.PasswordChange)]
    [HttpPost("password/set/{userId:guid}")]
    public async Task<ActionResult<ApiResponse<SetUserPasswordResultDto>>> SetUserPasswordAsync(
        Guid userId,
        [FromBody] SetUserPasswordRequest request,
        CancellationToken cancellationToken)
    {
        var result = await authService.SetPasswordForUserAsync(
            userId,
            request,
            User.GetRequiredUserId(),
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken);

        return Ok(ApiResponse<SetUserPasswordResultDto>.Ok(result, "users.password_set.success"));
    }

    /// <summary>
    /// Hedef kullanıcı için TEK KULLANIMLIK, kısa ömürlü bir giriş bağlantısı üretir.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Neden kimliğe bürünme (impersonate) yetmiyordu:</b> o uç ÇAĞIRANIN oturum çerezini hedef
    /// kullanıcıya çevirir. Faturalama sistemi (WHMCS) paneli SUNUCUDAN SUNUCUYA çağırdığı için o
    /// çerez faturalama sunucusunda kalır; müşterinin tarayıcısına taşınamaz ve başka bir alan adı
    /// için geçerli de olmaz. Yani "Panele git" düğmesi hiç çalışmıyordu.
    /// </para>
    /// <para>
    /// Dönen adres müşterinin tarayıcısına YÖNLENDİRİLİR; adres belirteci çerezle takas eder.
    /// </para>
    /// </remarks>
    [Authorize(Roles = "Administrator,Reseller")]
    [HttpPost("sso-link/{userId:guid}")]
    public async Task<ActionResult<ApiResponse<SsoLinkDto>>> CreateSsoLinkAsync(
        Guid userId,
        CancellationToken cancellationToken)
    {
        // ADRES İSTEKTEN ALINMAZ. Buradan eskiden `Request.Host.Value` geçiliyordu ("panel adresi
        // ayarlarda boşsa isteğin geldiği adres kullanılsın" diye). `Host` istemcinin serbest
        // metnidir — panel `AllowedHosts: "*"` ile gelen her ana adı kabul eder — ve bu bağlantı
        // TEK KULLANIMLIK BİR GİRİŞ BELİRTECİ taşır. Ölçüldü: yedek yalnız `ServerAddress` boşken
        // devreye girebiliyordu, ama `PanelPreferencesService` onu asla boş bırakmıyor; yani yedek
        // hiçbir zaman çalışmıyordu, sadece istismar yüzeyi olarak duruyordu.
        var link = await authService.CreateSsoLinkAsync(
            userId,
            User.GetRequiredUserId(),
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken);

        return Ok(ApiResponse<SsoLinkDto>.Ok(link, "auth.sso_link.created"));
    }

    /// <summary>
    /// Tek kullanımlık giriş belirtecini oturum çerezine takas eder. Müşterinin TARAYICISI gezer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Neden JSON değil SAYFA döner:</b> bu ucu bir insan tarayıcıyla açar. JSON zarfı dönseydi
    /// müşteri, başarılı girişten sonra ekranda ham bir <c>{"success":true…}</c> görürdü.
    /// </para>
    /// <para>
    /// <b>Neden yönlendirme (302) değil, ara sayfa:</b> panelin oturum çerezi
    /// <c>SameSite=Strict</c>. Faturalama sisteminden gelen bu gezinti ÇAPRAZ SİTEDİR; hemen 302 ile
    /// panele atsaydık tarayıcı, yönlendirme zinciri çapraz site sayıldığı için taze çerezi bir
    /// sonraki isteğe EKLEMEYEBİLİR ve müşteri giriş ekranına düşerdi. Ara sayfa panelin KENDİ
    /// kaynağında durur; oradan yapılan gezinti aynı sitedir ve çerez gider. Sayfa yalnız
    /// <c>meta refresh</c> kullanır — panelin içerik güvenliği ilkesi satır içi betiği bloklar.
    /// </para>
    /// <para>
    /// <b>Neden kimlik doğrulaması yok:</b> kimliği belirtecin kendisi taşıyor. Uç, gizli giriş yolu
    /// başlığından da muaftır (<c>SecureEntryPathMiddleware</c>) çünkü tarayıcı gezintisi özel başlık
    /// ekleyemez; IP kısıtlaması ise bu uçtan ÖNCE uygulanmaya devam eder.
    /// </para>
    /// </remarks>
    [AllowAnonymous]
    [EnableRateLimiting(PanelRateLimits.Login)]
    [HttpGet("sso")]
    public async Task<IActionResult> RedeemSsoLinkAsync(
        [FromQuery] string? token,
        CancellationToken cancellationToken)
    {
        var securePath = await securityService.GetSecureEntryPathAsync(cancellationToken);
        var panelUrl = "/" + securePath.Trim('/') + "/";

        var user = await authService.TryRedeemSsoTokenAsync(
            token ?? string.Empty,
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken);

        if (user is null)
        {
            return SsoPage(panelUrl, basarili: false);
        }

        await SignInAsync(user, isPersistent: false);
        return SsoPage(panelUrl, basarili: true);
    }

    /// <summary>
    /// Giriş bağlantısının sonuç sayfası. İki dilde birden yazılır: bu sayfa panelin arayüzünden
    /// ÖNCE gelir, kullanıcının dil tercihi henüz yüklenmemiştir ve tek dil seçmek müşterilerin
    /// yarısına anlaşılmaz bir ekran göstermek olurdu.
    /// </summary>
    private ContentResult SsoPage(string panelUrl, bool basarili)
    {
        var hedef = System.Net.WebUtility.HtmlEncode(panelUrl);
        var yenile = basarili ? $"<meta http-equiv=\"refresh\" content=\"0;url={hedef}\">" : string.Empty;
        var baslik = basarili ? "Panele yönlendiriliyorsunuz" : "Giriş bağlantısı geçersiz";
        var govde = basarili
            ? "<p>Giriş yapıldı, panele yönlendiriliyorsunuz.<br>Signed in, redirecting to the panel.</p>"
            : "<p>Bu giriş bağlantısı geçersiz, süresi dolmuş ya da daha önce kullanılmış. "
              + "Bağlantılar tek kullanımlıktır ve birkaç dakika geçerlidir.<br>"
              + "This sign-in link is invalid, expired or has already been used. "
              + "Links are single-use and valid for a few minutes.</p>";

        // Belirteç ADRESTE geçtiği için sayfa ÖNBELLEĞE ALINMAZ; ayrıca panelin küresel
        // Referrer-Policy: same-origin başlığı adresin üçüncü taraflara sızmasını engeller.
        Response.Headers.CacheControl = "no-store";

        // Stil AYRI bir sabit: ham dize içine gömülseydi CSS'in süslü parantezleri enterpolasyon
        // delikleriyle karışırdı (derleyici hatası). Satır içi <style> panelin içerik güvenliği
        // ilkesinde açıkça izinlidir ("style-src 'self' 'unsafe-inline'"), satır içi BETİK değildir.
        const string stil = "body{font-family:system-ui,Segoe UI,Arial,sans-serif;"
            + "margin:4rem auto;max-width:34rem;padding:0 1rem;line-height:1.6}";

        return new ContentResult
        {
            StatusCode = basarili ? StatusCodes.Status200OK : StatusCodes.Status400BadRequest,
            ContentType = "text/html; charset=utf-8",
            Content = $"""
                <!doctype html>
                <html lang="tr">
                <head>
                <meta charset="utf-8">
                <meta name="viewport" content="width=device-width, initial-scale=1">
                {yenile}
                <title>{baslik}</title>
                <style>{stil}</style>
                </head>
                <body>
                <h1>{baslik}</h1>
                {govde}
                <p><a href="{hedef}">Panele git / Go to the panel</a></p>
                </body>
                </html>
                """
        };
    }

    /// <summary>panel "log in as customer": yönetici, hedef kullanıcı olarak oturum açar.</summary>
    /// <remarks>
    /// <para>
    /// <b>HEDEF YÖNETİCİ OLAMAZ.</b> Uç, hedefin rolüne hiç bakmıyordu: bir yönetici BAŞKA bir
    /// yönetici olarak oturum açabiliyordu. Bu, panelin yöneticiler arasına koyduğu her sınırı
    /// anlamsız kılıyordu — en somut örneği terminal ucu: SYSTEM kabuğu sunucu SAHİBİNE ayrılmışken
    /// ikinci bir yönetici sahibi taklit edip aynı kabuğu alabilirdi. Özelliğin adı zaten "log in as
    /// customer"; kısıt onu daraltmıyor, adının söylediği şeye eşitliyor.
    /// </para>
    /// <para>
    /// İkinci uç <c>TerminalController</c>'da: taklit edilen bir oturum, hedef sunucu sahibi OLSA
    /// BİLE terminale giremez. İki koruma aynı deliğin iki ucudur; biri kalkarsa diğeri tutar.
    /// </para>
    /// </remarks>
    [Authorize(Roles = "Administrator")]
    [HttpPost("impersonate/{userId:guid}")]
    public async Task<ActionResult<ApiResponse<CurrentUserDto>>> ImpersonateAsync(Guid userId, CancellationToken cancellationToken)
    {
        var adminId = User.GetRequiredUserId();
        var adminName = User.Identity?.Name ?? "admin";
        if (userId == adminId)
        {
            return BadRequest(ApiResponse<object>.Fail("auth.cannot_impersonate_self"));
        }

        var target = await authService.GetUserByIdAsync(userId, cancellationToken);
        if (string.Equals(target.Role, nameof(Core.Enums.UserRole.Administrator), StringComparison.OrdinalIgnoreCase))
        {
            return StatusCode(
                StatusCodes.Status403Forbidden,
                ApiResponse<object>.Fail("auth.cannot_impersonate_administrator", StatusCodes.Status403Forbidden));
        }

        await SignInAsync(target, isPersistent: false, impersonatorId: adminId, impersonatorName: adminName);
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        await auditService.WriteAsync("auth.impersonate.started", "auth", $"'{adminName}' impersonating '{target.UserName}'.", adminId, ip, cancellationToken);
        return Ok(ApiResponse<CurrentUserDto>.Ok(target, "auth.impersonate.started"));
    }

    /// <summary>Impersonation'ı bitirir: asıl yönetici hesabına geri döner.</summary>
    [Authorize]
    [HttpPost("impersonate/stop")]
    public async Task<ActionResult<ApiResponse<CurrentUserDto>>> StopImpersonateAsync(CancellationToken cancellationToken)
    {
        var impersonatorId = User.FindFirstValue("impersonator_id");
        if (!Guid.TryParse(impersonatorId, out var adminId))
        {
            return BadRequest(ApiResponse<object>.Fail("auth.not_impersonating"));
        }

        var admin = await authService.GetUserByIdAsync(adminId, cancellationToken);
        await SignInAsync(admin, isPersistent: false);
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        await auditService.WriteAsync("auth.impersonate.stopped", "auth", $"Returned to '{admin.UserName}'.", adminId, ip, cancellationToken);
        return Ok(ApiResponse<CurrentUserDto>.Ok(admin, "auth.impersonate.stopped"));
    }

    /// <summary>
    /// Çıkış. Çerez YALNIZCA tarayıcıdan silinmez, SUNUCUDA da geçersiz kılınır.
    /// </summary>
    /// <remarks>
    /// Panelin oturum çerezi kendi kendine yeterlidir (kimlik ve bitiş anı çerezin içinde şifreli
    /// olarak taşınır, sunucuda kayıt tutulmaz); bu yüzden <c>SignOutAsync</c> tek başına yalnız
    /// tarayıcıdaki kopyayı siler ve çerezin KENDİSİ 12 saat (kalıcı seçilmişse 14 gün) boyunca
    /// çalışmaya devam ederdi. Yani çerezi daha önce ele geçirmiş biri için "Çıkış" düğmesinin
    /// hiçbir anlamı yoktu. Oturum kimliği kara listeye yazılarak çerez sunucu tarafında da ölür;
    /// gerekçenin tamamı <see cref="SessionRevocationStore"/>'da.
    /// </remarks>
    [Authorize]
    [HttpPost("logout")]
    public async Task<ActionResult<ApiResponse<object>>> LogoutAsync(CancellationToken cancellationToken)
    {
        var userId = User.GetRequiredUserId();
        var userName = User.Identity?.Name ?? "unknown";
        var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();

        // ÖNCE kara listeye yaz, SONRA çerezi sil. Ters sırada yapılsaydı ve kara liste yazımı
        // (disk dolu, ACL) patlasaydı, kullanıcı "çıkış yaptım" görürken çerez sunucuda geçerli
        // kalırdı — sessiz bir yarım koruma. Bu sırada yazma patlarsa çıkış da BAŞARISIZ olur ve
        // kullanıcı bunu görür.
        // ÖNCE kara listeye yaz, SONRA çerezi sil. Ters sırada yapılsaydı ve kara liste yazımı
        // (disk dolu, ACL) patlasaydı, kullanıcı "çıkış yaptım" görürken çerez sunucuda geçerli
        // kalırdı — sessiz bir yarım koruma. Bu sırada yazma patlarsa çıkış da BAŞARISIZ olur ve
        // kullanıcı bunu görür.
        await sessionRevocations.RevokeAsync(
            User.FindFirstValue(SessionRevocationStore.SessionIdClaim),
            cancellationToken);

        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        await auditService.WriteAsync("auth.logout.succeeded", "auth", $"User '{userName}' signed out.", userId, ipAddress, cancellationToken);

        return Ok(ApiResponse<object>.Ok(new { }, "auth.logout.success"));
    }

    [Authorize]
    [HttpPost("2fa/begin")]
    public async Task<ActionResult<ApiResponse<TwoFactorSetupDto>>> BeginTwoFactorSetupAsync(CancellationToken cancellationToken)
    {
        var result = await authService.BeginTwoFactorSetupAsync(User.GetRequiredUserId(), CaptureTwoFactorApproval(), cancellationToken);
        return Ok(ApiResponse<TwoFactorSetupDto>.Ok(result));
    }

    [Authorize]
    [HttpPost("2fa/confirm")]
    public async Task<ActionResult<ApiResponse<TwoFactorConfirmDto>>> ConfirmTwoFactorSetupAsync(
        [FromBody] VerifyTwoFactorRequest request,
        CancellationToken cancellationToken)
    {
        var sourceSessionId = User.FindFirstValue(SessionRevocationStore.SessionIdClaim) ?? string.Empty;
        var result = await authService.ConfirmTwoFactorSetupAsync(
            User.GetRequiredUserId(),
            request.Code,
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            CaptureTwoFactorApproval(),
            cancellationToken);

        var authentication = await HttpContext.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        await SignInAsync(result.User, authentication.Properties?.IsPersistent ?? false,
            sourceSessionId: sourceSessionId);
        return Ok(ApiResponse<TwoFactorConfirmDto>.Ok(result, "auth.2fa.confirmed"));
    }

    [Authorize]
    [HttpPost("2fa/recovery-codes")]
    public async Task<ActionResult<ApiResponse<RecoveryCodesDto>>> RegenerateRecoveryCodesAsync(CancellationToken cancellationToken)
    {
        var userId = User.GetRequiredUserId();
        var result = await authService.RegenerateRecoveryCodesAsync(userId,
            HttpContext.Connection.RemoteIpAddress?.ToString(), CaptureTwoFactorApproval(), cancellationToken);

        // İZ YOKTU. Kurtarma kodları 2FA'yı ATLAMANIN yoludur: yenilemek eskilerini geçersiz kılar
        // ve YENİ bir geçerli küme üretir. Ele geçirilmiş bir oturum bunu tek adımda yaparak kalıcı
        // bir arka kapı bırakabilirdi ve hiçbir iz kalmazdı. KODLARIN KENDİSİ ASLA YAZILMAZ —
        // yalnızca olgu ve kaç kod üretildiği.
        //
        // KİRACI BAĞI YOK: kimlik doğrulama kullanıcının KENDİ hesabına aittir, bir aboneliğin
        // kaynağına değil; aktör (UserId) zaten konunun ta kendisidir ve kayıt kullanıcının
        // görünümünde bu yüzden çıkar.
        // The service commits this audit together with the new recovery generation, so an
        // audit failure cannot invalidate the old codes without returning the new codes.

        return Ok(ApiResponse<RecoveryCodesDto>.Ok(result, "auth.2fa.recovery_regenerated"));
    }

    /// <summary>
    /// İki adımlı doğrulamayı kapatır. MEVCUT PAROLA şart — yani bu uç bir parola deneme yüzeyidir.
    /// </summary>
    /// <remarks>
    /// Hız sınırı parola değiştirmeyle AYNI politikadan geçiyor (5 deneme / 15 dk / IP) ve bu
    /// eksikti: uç yalnız küresel oturum tavanının (600/dk) altındaydı, yani çerezi ele geçiren
    /// biri dakikada yüzlerce parola deneyerek ikinci faktörü kaldırmayı zorlayabilirdi. Kapatma,
    /// hesabı kalıcı devralmanın son adımıdır; parola değiştirmekle aynı korumayı hak eder.
    /// </remarks>
    [Authorize]
    [EnableRateLimiting(PanelRateLimits.PasswordChange)]
    [HttpPost("2fa/disable")]
    public async Task<ActionResult<ApiResponse<CurrentUserDto>>> DisableTwoFactorAsync(
        [FromBody] DisableTwoFactorRequest request,
        CancellationToken cancellationToken)
    {
        var sourceSessionId = User.FindFirstValue(SessionRevocationStore.SessionIdClaim) ?? string.Empty;
        var user = await authService.DisableTwoFactorAsync(
            User.GetRequiredUserId(),
            request.Password,
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            CaptureTwoFactorApproval(),
            cancellationToken);

        var authentication = await HttpContext.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        await SignInAsync(user, authentication.Properties?.IsPersistent ?? false,
            sourceSessionId: sourceSessionId);
        return Ok(ApiResponse<CurrentUserDto>.Ok(user, "auth.2fa.disabled"));
    }

    private Func<PanelUser, CancellationToken, Task> CaptureTwoFactorApproval()
    {
        // Immutable request authority: a queued action must not adopt a newer password,
        // role or login session merely because the service reloaded the same user ID.
        var userId = User.GetRequiredUserId();
        var authenticated = User.Identity?.IsAuthenticated == true;
        var role = User.FindFirstValue(ClaimTypes.Role);
        var stamp = User.FindFirstValue(CookieSessionValidator.CredentialStampClaim);
        var sessionId = User.FindFirstValue(SessionRevocationStore.SessionIdClaim);
        return async (current, token) =>
        {
            token.ThrowIfCancellationRequested();
            if (!authenticated || current.Id != userId || !current.IsActive || role != current.Role.ToString() ||
                string.IsNullOrWhiteSpace(stamp) || stamp != CookieSessionValidator.ComputeStamp(current.PasswordHash) ||
                string.IsNullOrWhiteSpace(sessionId) || await sessionRevocations.IsRevokedAsync(sessionId, token))
                throw new AppException("auth.unauthorized", 401, 401);
            token.ThrowIfCancellationRequested();
        };
    }

    /// <summary>
    /// Kullanıcının KENDİ parolasını değiştirmesi.
    /// <para>
    /// Yalnız ÇEREZ şeması kabul edilir. API anahtarıyla parola değiştirilebilseydi, sınırlı bir
    /// anahtarı ele geçiren biri parolayı değiştirip hesabın tamamını devralabilirdi — anahtarın
    /// yetkisi hesabın yetkisine yükselirdi. Anahtar sunucudan sunucuya iş içindir, hesap yönetimi
    /// için değil.
    /// </para>
    /// <para>
    /// Hız sınırı mevcut parolayı deneme yanılmaya karşı korur: çerez çalınmış olsa bile parola
    /// tahmin edilerek doğrulama aşılamasın.
    /// </para>
    /// </summary>
    [Authorize(AuthenticationSchemes = CookieAuthenticationDefaults.AuthenticationScheme)]
    [EnableRateLimiting(PanelRateLimits.PasswordChange)]
    [HttpPost("change-password")]
    public async Task<ActionResult<ApiResponse<ChangePasswordResultDto>>> ChangePasswordAsync(
        [FromBody] ChangePasswordRequest request,
        CancellationToken cancellationToken)
    {
        var result = await authService.ChangePasswordAsync(
            User.GetRequiredUserId(),
            request,
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken);

        return Ok(ApiResponse<ChangePasswordResultDto>.Ok(result, "auth.password_changed"));
    }

    private Task SignInAsync(CurrentUserDto user, bool isPersistent, Guid? impersonatorId = null,
        string? impersonatorName = null, string? sourceSessionId = null)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.UserName),
            new(ClaimTypes.Email, user.Email),
            new(ClaimTypes.Role, user.Role),
            new("preferred_language", user.PreferredLanguage),
            new("two_factor_enabled", user.IsTwoFactorEnabled.ToString())
        };

        if (impersonatorId.HasValue && !string.IsNullOrWhiteSpace(impersonatorName))
        {
            claims.Add(new Claim("impersonator_id", impersonatorId.Value.ToString()));
            claims.Add(new Claim("impersonator_name", impersonatorName));
        }

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
        var properties = new AuthenticationProperties
        {
            IsPersistent = isPersistent,
            AllowRefresh = true,
            IssuedUtc = DateTimeOffset.UtcNow,
            ExpiresUtc = isPersistent ? DateTimeOffset.UtcNow.AddDays(14) : null
        };
        properties.Parameters[CookieSessionValidator.AcceptedIdentityParameter] = user.AuthenticationEvidence;
        if (sourceSessionId is not null)
            properties.Parameters[CookieSessionValidator.SourceSessionParameter] = sourceSessionId;

        return HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal, properties);
    }
}
