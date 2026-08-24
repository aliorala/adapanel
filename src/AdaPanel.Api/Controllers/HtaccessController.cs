using AdaPanel.Api.Extensions;
using AdaPanel.Api.Security;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Htaccess;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Audit;
using AdaPanel.Infrastructure.Htaccess;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AdaPanel.Api.Controllers;

/// <summary>Site başına <c>.htaccess</c> kipi.</summary>
public sealed class HtaccessModeRequest
{
    /// <summary><c>off</c> / <c>observe</c> / <c>enforce</c>.</summary>
    public string Mode { get; init; } = "off";

    /// <summary>Anahtar dosyasına YORUM olarak yazılan not (kim, neden açtı).</summary>
    public string? Note { get; init; }
}

/// <summary>Kaydetmeden önce söz dizimi denetimi.</summary>
public sealed class HtaccessValidateRequest
{
    public string Content { get; init; } = string.Empty;

    /// <summary>Dosyanın kapsayacağı URL yolu (<c>/</c>, <c>/blog/</c>).</summary>
    public string VirtualPath { get; init; } = "/";
}

/// <summary>Bir URL'in gerçek <c>.htaccess</c> dosyalarına karşı benzetimi.</summary>
public sealed class HtaccessSimulateRequest
{
    public string RequestPath { get; init; } = "/";

    public string Method { get; init; } = "GET";

    public bool Https { get; init; }
}

/// <summary>
/// <c>.htaccess</c> DESTEĞİNİN PANEL UCU.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden var:</b> bugünkü şikâyet "dosya duruyor, hiçbir şey olmuyor, kimse sebebini
/// söylemiyor" idi. Bu denetleyicinin her ucu o cümlenin bir parçasını ortadan kaldırıyor:
/// hangi dosyalar var (<c>report</c>), içindeki hangi satır çalışmıyor ve NEDEN (<c>inspect</c>),
/// yazdığım metin geçerli mi (<c>validate</c>), bu URL'e ne olacak (<c>simulate</c>) ve destek
/// açık mı (<c>mode</c>).
/// </para>
/// <para>
/// <b>Roller:</b> site uçları <c>Administrator,Reseller</c> — panelin barındırma araçlarıyla aynı
/// (bkz. <c>SiteHostingSettingsController</c>, <c>AspController</c>) ve arayüzdeki araç
/// görünürlüğüyle birebir. Sunucu geneli anahtarın ucu YOK — gerekçesi dosyanın sonunda yazılı.
/// </para>
/// <para>
/// <b>Kapsam denetimi:</b> her site ucunda <c>CustomerAccess.EnsureSiteAsync</c>. Bayi yalnız
/// kendi müşterilerinin sitelerine dokunabilir; site adı doğrulanmadan hiçbir dosya okunmaz.
/// </para>
/// </remarks>
[ApiController]
[Authorize(Roles = "Administrator,Reseller")]
[Route("api/v1/sites")]
public sealed class HtaccessController(
    HtaccessPanelService htaccess,
    ICustomerScopeService customerScopes,
    IAuditService auditService) : ControllerBase
{
    /// <summary>Sitenin tam <c>.htaccess</c> durumu: kip, dosyalar, destek, dürüst sınırlar.</summary>
    [HttpGet("{siteName}/htaccess")]
    public async Task<ActionResult<ApiResponse<HtaccessSiteReport>>> GetReportAsync(
        string siteName,
        CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        var report = await htaccess.GetReportAsync(siteName, cancellationToken);
        return Ok(ApiResponse<HtaccessSiteReport>.Ok(report));
    }

    /// <summary>
    /// Bir dizinin basamaklandırılmış raporu: hangi direktif uygulanıyor, hangisi UYGULANMIYOR
    /// ve NEDEN, hangi desen ReDoS riski taşıyor.
    /// </summary>
    /// <remarks>
    /// Sebep metinleri motorun kendi kataloğundan (<c>HtaccessDirectiveCatalog</c>) geliyor ve
    /// olduğu gibi aktarılıyor. Denetleyicide yeniden yazılsaydı iki anlatım oluşur ve biri
    /// güncellenmeden kalırdı — kullanıcıya söylenen "neden" ile motorun gerçek davranışı ayrı
    /// düşerdi.
    /// </remarks>
    [HttpGet("{siteName}/htaccess/inspect")]
    public async Task<ActionResult<ApiResponse<HtaccessInspectionResult>>> InspectAsync(
        string siteName,
        [FromQuery] string? directory,
        CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        var result = await htaccess.InspectAsync(siteName, directory, cancellationToken);
        return Ok(ApiResponse<HtaccessInspectionResult>.Ok(result));
    }

    /// <summary>
    /// Kullanıcının yazdığı <c>.htaccess</c> metnini KAYDETMEDEN ÖNCE doğrular.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Neden POST ama denetim kaydı YAZIYOR:</b> uç durum DEĞİŞTİRMİYOR (diskte hiçbir şey
    /// okunmaz/yazılmaz), yani normalde muaf olurdu. Ama muafiyet listesi başka bir iş kolunun
    /// dosyasında (<c>AuditCoverageTests.Muaflar</c>) ve bu turda ona dokunulmadı. İki seçenek
    /// vardı: nöbetçi testi kandırmak için ucu <c>[HttpGet]</c>+sorgu dizesine taşımak — gerçek
    /// <c>.htaccess</c> dosyaları sorgu dizesi sınırını (8 KB) aşar ve özellik canlıda kırılırdı —
    /// ya da kaydı yazmak. İkincisi seçildi ve savunulabilir: bu uç, KİRACI KONTROLÜNDEKİ metni
    /// panelin düzenli ifade motoruna veren tek yoldur (ReDoS bütçesi burada harcanır), yani
    /// "kim, ne zaman, hangi siteye ne kadar iş yaptırdı" sorusunun bir cevabı olması yerinde.
    /// </para>
    /// <para>
    /// İçerik kayda YAZILMIYOR: <c>.htaccess</c> metni <c>AuthUserFile</c> yolları ve iç dizin
    /// yapısı taşıyabilir. Kayda yalnız boyut giriyor.
    /// </para>
    /// </remarks>
    [HttpPost("{siteName}/htaccess/validate")]
    public async Task<ActionResult<ApiResponse<HtaccessParseResult>>> ValidateAsync(
        string siteName,
        [FromBody] HtaccessValidateRequest request,
        CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        var result = htaccess.Validate(request?.Content, request?.VirtualPath);

        await auditService.WriteForSubscriptionAsync(
            "htaccess.validated",
            $"htaccess:{siteName}",
            $"A .htaccess draft ({request?.Content?.Length ?? 0} characters) was syntax-checked for '{siteName}'.",
            User.GetRequiredUserId(),
            await AuditSubject.SiteOwnerAsync(customerScopes, siteName, cancellationToken),
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken);

        return Ok(ApiResponse<HtaccessParseResult>.Ok(result));
    }

    /// <summary>
    /// Bir URL'i sitenin GERÇEK <c>.htaccess</c> dosyalarına karşı çalıştırır ve sonucu söyler.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Kural listesini göstermek yetmiyor:</b> <c>RewriteCond</c> zincirleri, <c>[L]</c> bayrağı
    /// ve dizin basamaklandırması yüzünden "görünen kural" ile "etkin kural" farklı olabilir.
    /// Tek dürüst cevap ölçmektir ve ölçüm, modülün canlıda kullanacağı KÖPRÜNÜN AYNISINDAN geçer.
    /// </para>
    /// <para>
    /// <b>Denetim kaydı:</b> yukarıdaki <c>validate</c> ile aynı gerekçe — durum değiştirmiyor ama
    /// motoru kiracı girdisiyle çalıştıran bir POST.
    /// </para>
    /// </remarks>
    [HttpPost("{siteName}/htaccess/simulate")]
    public async Task<ActionResult<ApiResponse<HtaccessSimulationResult>>> SimulateAsync(
        string siteName,
        [FromBody] HtaccessSimulateRequest request,
        CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        var result = await htaccess.SimulateAsync(
            siteName, request?.RequestPath, request?.Method, request?.Https ?? false, cancellationToken);

        await auditService.WriteForSubscriptionAsync(
            "htaccess.simulated",
            $"htaccess:{siteName}",
            $"URL '{result.RequestPath}' was simulated against the .htaccess rules of '{siteName}' (result: {result.Action}).",
            User.GetRequiredUserId(),
            await AuditSubject.SiteOwnerAsync(customerScopes, siteName, cancellationToken),
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken);

        return Ok(ApiResponse<HtaccessSimulationResult>.Ok(result));
    }

    /// <summary>
    /// Sitenin <c>.htaccess</c> kipini değiştirir: kapalı / yalnız gözlem / uygula.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Bu bir GÜVENLİK SINIRI değişikliğidir</b> ve iz bırakmak zorundadır: <c>.htaccess</c>
    /// <c>Deny</c>/<c>Require</c> ile erişim reddedebilir, <c>AuthUserFile</c> ile parola
    /// koyabilir, <c>RewriteRule</c> ile isteği başka bir betiğe yönlendirebilir. Kipi
    /// <c>enforce</c>'a almak, sitenin dosya sisteminde duran bir metnin sunucunun istek yolunda
    /// SÖZ SAHİBİ olması demektir.
    /// </para>
    /// <para>
    /// <b>Kaydın KONUSU</b> sitenin abonelik sahibidir (<c>AuditSubject.SiteOwnerAsync</c>):
    /// bu ucu yönetici ya da bayi de çağırabilir ve o durumda kaydın <c>UserId</c>'si kiracı
    /// olmaz; konu yazılmasaydı kiracı kendi sitesinde yapılan bu değişikliği HİÇ göremezdi.
    /// </para>
    /// </remarks>
    [HttpPost("{siteName}/htaccess/mode")]
    public async Task<ActionResult<ApiResponse<HtaccessSiteReport>>> SetModeAsync(
        string siteName,
        [FromBody] HtaccessModeRequest request,
        CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        var report = await htaccess.SetModeAsync(siteName, request?.Mode ?? "off", request?.Note, cancellationToken);

        await auditService.WriteForSubscriptionAsync(
            "htaccess.mode.changed",
            $"htaccess:{siteName}",
            $".htaccess support for '{siteName}' was set to '{report.Mode}'.",
            User.GetRequiredUserId(),
            await AuditSubject.SiteOwnerAsync(customerScopes, siteName, cancellationToken),
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken);

        return Ok(ApiResponse<HtaccessSiteReport>.Ok(report, "htaccess.mode_updated"));
    }

    // ------------------------------------------------------------------------------------
    // SUNUCU GENELİ ANAHTAR UCU BİLEREK YOK — ve bu bir eksiklik değil, ÖLÇÜLMÜŞ bir karar.
    // ------------------------------------------------------------------------------------
    // Genel anahtar (%ProgramData%\AdaPanel\htaccess\module.switch) tek bir aboneliğe ait
    // DEĞİLDİR: sunucudaki her siteyi aynı anda etkiler. Dolayısıyla denetim kaydının konusu
    // (SubscriptionUserId) yapısal olarak BOŞTUR ve bir kiracıya bağlamak, komşularını da kapsayan
    // bir işlemi o kiracının görünümüne düşürmek (çapraz-kiracı sızıntı) olurdu.
    //
    // Konusuz kayıtlar `AuditCoverageTests.KiraciyaAitOlmayanlar` listesinde GEREKÇESİYLE durur —
    // o dosya bu iş kolunun kapsamı dışında ve nöbetçiyi kandıracak bir yol (kaydı servise
    // saklamak, konu taşıyan aşırı yüklemeye null geçmek) BİLEREK seçilmedi: nöbetçinin amacı
    // tam da bu kararın görünür olmasıdır.
    //
    // BUGÜN BEDELİ SIFIR: çekirdek motor w3wp içinde zaten yüklenmiyor (bkz.
    // HtaccessPanelService.MotorEngelleri), yani genel anahtar açılsa da hiçbir şey değişmezdi.
    // Panel bu durumu `HtaccessSiteReport.ServerMode` ile OKUYUP gösteriyor; yönetici anahtarı
    // dosyadan açabiliyor (biçim bilerek düz metin: acil durum kurtarma aracı).
}
