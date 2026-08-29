using AdaPanel.Api.Extensions;
using AdaPanel.Api.Security;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Htaccess;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Audit;
using AdaPanel.Infrastructure.Htaccess;
using AdaPanel.Infrastructure.Htaccess.Hosting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AdaPanel.Api.Controllers;

/// <summary>Site başına ve sunucu geneli <c>.htaccess</c> kipi.</summary>
public sealed class HtaccessModeRequest
{
    /// <summary><c>off</c> / <c>observe</c> / <c>enforce</c>. SÖYLENMEK ZORUNDA.</summary>
    /// <remarks>
    /// <para>
    /// <b>VARSAYILANI YOK ve bu bir eksiklik değil, ölçülmüş bir açığın kapatılması.</b> Alan
    /// eskiden <c>= "off"</c> ile başlıyordu; yani <c>{}</c> gövdesi (bozuk istemci, yanlış
    /// içerik tipi, eksik alan) <b>geçerli bir "kapat" isteğine</b> dönüşüyordu. Aynı tipin
    /// kullanıldığı iki uçtan biri sunucu genelidir: gövdesiz bir POST, sunucudaki HER sitenin
    /// <c>.htaccess</c> desteğini kapatır, anahtar dosyasını SİLER ve denetim kaydına
    /// "set to 'off'" yazardı — yönetici bunu bilerek yapmış görünürdü.
    /// </para>
    /// <para>
    /// <c>Off</c> KİPİNİN KENDİSİ meşrudur (acil durum kapatma düğmesi); yasaklanan şey kip
    /// SÖYLENMEDİĞİNDE off VARSAYMAKTIR. Boş/eksik değer artık
    /// <c>HtaccessPanelService.ModCozumle</c>'nin zaten var olan reddine düşer
    /// (<c>htaccess.invalid_mode</c>) — "enfroce" yazım hatasıyla aynı yola.
    /// </para>
    /// </remarks>
    public string? Mode { get; init; }

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
/// görünürlüğüyle birebir. Sunucu geneli anahtarın ucu (<c>server-mode</c>) YALNIZ
/// <c>Administrator</c>'dır — gerekçesi o ucun başında yazılı.
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

        // EKSİK KİP "off" SAYILMAZ (gerekçesi HtaccessModeRequest.Mode'da). Bu uç tek siteyi
        // etkiliyor, yani sunucu geneli anahtardaki kadar geniş değil — ama aynı kararı taşıması
        // ZORUNLU, çünkü ikisi AYNI istek tipini kullanıyor: alanın varsayılanı geri konsa iki uç
        // birden açılırdı. Tek site de az değildir: kip sessizce off'a düşünce sitenin
        // yönlendirmeleri ve erişim kuralları durur, denetim kaydı ise değişikliği çağıranın
        // BİLİNÇLİ kararı gibi gösterir — kiracı da olan biteni o kayıttan okur.
        var report = await htaccess.SetModeAsync(siteName, request?.Mode, request?.Note, cancellationToken);

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
    // SUNUCU GENELİ ANAHTAR — neden VAR, neden YALNIZ YÖNETİCİ, neden KONUSUZ denetimli.
    // ------------------------------------------------------------------------------------
    // BU BLOK BİR KEZ YANLIŞ BİR OLGUYA DAYANIYORDU ve düzeltmesi burada yazılı duruyor: eski
    // metin "ucu eklemenin bedeli sıfır, çünkü çekirdek motor w3wp içinde zaten yüklenmiyor"
    // diyordu. O gerekçe ARTIK GEÇERSİZ — derleme zamanı engeli kalktı ve motorun kaynağı net48
    // modül projesinde de derleniyor (ölçüm: HtaccessNet48EngineLoadTests, gerçek bir CLR 4
    // sürecinde). Yani genel anahtar artık özelliği GERÇEKTEN açıyor; ucun olmaması "bedelsiz bir
    // eksiklik" değil, özelliğin panelden hiç kullanılamaması demekti: yönetici module.switch
    // dosyasını ELLE yazmak zorundaydı.
    //
    // DÜZELTMENİN DÜZELTMESİ: bu metin bir sonraki turda "MotorEngelleri() BOŞ liste dönüyor"
    // diye de yazıyordu ve o cümle ölçülmemişti. Derleme zamanı engelinin kalkması, ÇALIŞMA
    // ZAMANINDA engel olmadığı anlamına gelmiyordu: canlıda modül applicationHost.config'te
    // yerinde olmadan panel yine "motor yüklü, engel yok" diyordu. Engeller artık ölçülüyor
    // (HtaccessPanelService.MotorEngelleri) ve eksik kaydın onarımı aşağıdaki register-module
    // ucundadır.
    //
    // Eski metnin HAKLI olan yarısı aynen duruyor ve aşağıdaki iki uç onu uyguluyor:
    //
    //  1) Genel anahtar (%ProgramData%\AdaPanel\htaccess\module.switch) tek bir aboneliğe ait
    //     DEĞİLDİR; sunucudaki HER siteyi aynı anda etkiler. Kaydın konusu (SubscriptionUserId)
    //     bu yüzden yapısal olarak BOŞTUR: bir kiracıya bağlamak, komşularını da kapsayan bir
    //     işlemi o kiracının görünümüne düşürmek (çapraz-kiracı sızıntı) olurdu. Kayıt konu
    //     TAŞIYAMAYAN aşırı yüklemeyle (IAuditService.WriteAsync) yazılıyor — kararı gizlemek
    //     değil, tipe yazmak: konu taşıyan aşırı yüklemeye null geçmek aynı sonucu verir ama
    //     "unutuldu mu, karar mı" sorusunu okuyucuya bırakırdı. Gerekçe
    //     AuditCoverageTests.KiraciyaAitOlmayanlar listesinde yazılı; nöbetçiyi susturan hiçbir
    //     yol (kaydı atlamak, servise saklamak) seçilmedi.
    //
    //  2) Uç YALNIZ Administrator'dır. Bayi de kiracıdır: kendi müşterilerinin sitelerini yönetir,
    //     ama bu düğme onun kapsamının DIŞINDAKİ bütün siteleri de açar/kapatır. Panelde sunucu
    //     geneli her ucun kuralı bu (FirewallController, PhpController.global-settings,
    //     SecurityController); sınıf düzeyindeki "Administrator,Reseller" kapısı bu iki uçta
    //     eylem düzeyinde daraltılıyor — SslController.RenewAllAsync ve
    //     DiskQuotaController.InstallFsrmAsync ile aynı kalıp.

    /// <summary>Sunucu geneli anahtarın durumu: kip, dosya var mı, dosyanın yolu.</summary>
    /// <remarks>
    /// Site adı ALMAZ: genel anahtar hiç sitesi olmayan bir sunucuda da (kurulumdan hemen sonra)
    /// görülebilmeli. Site raporundaki <c>ServerMode</c> alanı aynı değeri gösterir ama bir siteye
    /// bağlıdır; bu uç, arayüzün sunucu geneli ekranının kaynağıdır.
    /// </remarks>
    // ROTA SİTE AĞACININ DIŞINDA (`~/` mutlak yol): denetleyicinin kendi rotası `api/v1/sites`
    // olduğu için `server-mode` orada bir SABİT SEGMENT olurdu ve `SitesController`'ın
    // `GET {siteName}` ucunu "server-mode" adlı bir site için GÖLGELERDİ — o site panelden
    // görüntülenemez hâle gelirdi. Site adı için biçim doğrulaması ölçüldü: `DomainNames`
    // yalnızca normalleştiriyor, böyle bir adı REDDETMİYOR. Olasılık düşük, bedeli ise bir
    // sitenin sessizce erişilemez olması; sabit segmenti parametrenin yanına hiç koymamak
    // bu sınıfı tamamen kapatır.
    [HttpGet("~/api/v1/htaccess/server-mode")]
    [Authorize(Roles = "Administrator")]
    public ActionResult<ApiResponse<HtaccessServerSwitchState>> GetServerMode()
        => Ok(ApiResponse<HtaccessServerSwitchState>.Ok(htaccess.GetServerMode()));

    /// <summary>
    /// Sunucu geneli anahtarı çevirir — <c>.htaccess</c> desteğini AÇAN tek düğme.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Etkisi sitelerin toplamından büyüktür:</b> kapı etkin modu iki anahtarın EN DÜŞÜĞÜ
    /// olarak hesaplar (<c>HtaccessRuntimeGate.Evaluate</c>). Bu yüzden <c>off</c> mutlak bir
    /// kapatma düğmesidir (dosya SİLİNİR), <c>observe</c> ise tek hamlede bütün sunucuyu gözleme
    /// indirir: uygulama durur, günlük akmaya devam eder — bir sorunu tamamen kapatmadan teşhis
    /// etmenin yolu.
    /// </para>
    /// <para>
    /// <b>Denetim kaydı KONUSUZDUR</b> (yukarıdaki 1. maddenin gerekçesi). Kaydın kaynağı
    /// <c>htaccess:server</c>: site kayıtlarının kaynağı <c>htaccess:{siteName}</c> olduğu için
    /// ikisi denetim ekranında karışmaz ve "sunucuyu kim açtı" ile "şu siteyi kim açtı" ayrı ayrı
    /// aranabilir.
    /// </para>
    /// </remarks>
    [HttpPost("~/api/v1/htaccess/server-mode")]
    [Authorize(Roles = "Administrator")]
    public async Task<ActionResult<ApiResponse<HtaccessServerSwitchState>>> SetServerModeAsync(
        [FromBody] HtaccessModeRequest request,
        CancellationToken cancellationToken)
    {
        // EKSİK KİP "off" SAYILMAZ. Buradaki bedel ucun geri kalanıyla aynı ölçekte: gövdesiz bir
        // POST sunucudaki HER sitenin desteğini kapatır, anahtar dosyasını SİLER ve aşağıdaki
        // denetim kaydı bunu yöneticinin bilinçli kararı gibi yazardı. Ret, kaydı yazan satıra
        // GELMEDEN önce olur (ModCozumle fırlatır): "olmayan bir kararın kaydı" da bırakılmaz.
        var state = htaccess.SetServerMode(request?.Mode, request?.Note);

        await auditService.WriteAsync(
            "htaccess.server_mode.changed",
            "htaccess:server",
            $"Server-wide .htaccess support was set to '{state.Mode}' (affects every site on this server).",
            User.GetRequiredUserId(),
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken);

        return Ok(ApiResponse<HtaccessServerSwitchState>.Ok(state, "htaccess.mode_updated"));
    }

    /// <summary>Modülün IIS kaydının ÖLÇÜLMÜŞ hâli — site adı gerekmez.</summary>
    /// <remarks>
    /// Site raporu da aynı ölçümü taşıyor (<c>ModuleRegistration</c>) ama bir siteye bağlıdır; bu
    /// uç, henüz hiç sitesi olmayan bir sunucuda da (kurulumdan hemen sonra) "modül kuruldu mu"
    /// sorusunun cevabıdır — <c>server-mode</c> ucunun varlık gerekçesinin aynısı.
    /// </remarks>
    [HttpGet("~/api/v1/htaccess/module")]
    [Authorize(Roles = "Administrator")]
    public ActionResult<ApiResponse<HtaccessModuleRegistrationState>> GetModuleRegistration()
        => Ok(ApiResponse<HtaccessModuleRegistrationState>.Ok(htaccess.GetModuleRegistration()));

    /// <summary>
    /// Modülü <c>applicationHost.config</c>'e KAYDEDER — eksik/bozuk kaydın panelden onarımı.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Neden var:</b> panel artık kaydın eksikliğini ÖLÇÜYOR (<c>htaccess.blocker.*</c>), ama
    /// bugüne kadar düzeltmenin tek yolu sunucuya girip <c>install-htaccess-module.ps1</c>
    /// çalıştırmaktı. Sorunu gösterip çaresini söylememek, bu iş kolunun düzeltmeye çalıştığı
    /// "hiçbir şey olmuyor, kimse sebebini söylemiyor" davranışının bir başka hâlidir.
    /// </para>
    /// <para>
    /// <b>ROTA SİTE AĞACININ DIŞINDA</b> (<c>~/</c> mutlak yol) ve gerekçesi yukarıdaki
    /// <c>server-mode</c> bloğunda yazılı: bu denetleyicinin rotası <c>api/v1/sites</c> olduğu için
    /// göreli yazılan her sabit segment, <c>SitesController</c>'ın <c>GET {siteName}</c> ucunu aynı
    /// adlı bir site için GÖLGELER. Kayıt zaten sunucu genelidir; site ağacında yeri de yoktur.
    /// </para>
    /// <para>
    /// <b>YALNIZ Administrator.</b> Bu uç <c>applicationHost.config</c>'e yazar, yani sunucudaki
    /// yönetilen kod çalıştıran HER uygulama havuzunu bir kez geri döndürür ve modülü her siteye
    /// yükletir. Bayi de kiracıdır ve bu, kapsamının çok dışındadır — <c>server-mode</c> ile aynı
    /// karar, aynı gerekçe.
    /// </para>
    /// <para>
    /// <b>Denetim kaydı KONUSUZ</b> (<c>WriteAsync</c>): değişiklik tek bir aboneliğe ait değildir,
    /// sunucudaki her siteyi kapsar. Bir kiracıya bağlansaydı komşularını da kapsayan bir işlem o
    /// kiracının denetim görünümüne düşerdi. Kaynak <c>htaccess:server</c> — sunucu geneli
    /// <c>.htaccess</c> kayıtları denetim ekranında tek yerde aranabilsin diye anahtar kipiyle aynı.
    /// </para>
    /// <para>
    /// <b>GÖVDE ALMAZ.</b> Kaydın bütün içeriği (ad, tip, ön koşul) <c>HtaccessModuleRegistration</c>
    /// sabitlerinden ve derlemenin kendisinden gelir. Çağırana bir alan bırakmak, IIS'e panelin
    /// bilmediği bir tip adı yazdırabilmek demekti.
    /// </para>
    /// </remarks>
    [HttpPost("~/api/v1/htaccess/register-module")]
    [Authorize(Roles = "Administrator")]
    public async Task<ActionResult<ApiResponse<HtaccessModuleRegistrationState>>> RegisterModuleAsync(
        CancellationToken cancellationToken)
    {
        // KAYIT ÖNCE YAZILIR, SONRA İZ BIRAKILIR: yazma başarısız olursa (GAC'ta derleme yok,
        // yetki yetmiyor) istisna buradan yukarı gider ve "olmayan bir değişikliğin kaydı"
        // bırakılmaz — server-mode ucundaki kararın aynısı.
        var state = htaccess.RegisterModule();

        await auditService.WriteAsync(
            "htaccess.module.registered",
            "htaccess:server",
            $"The .htaccess IIS module was registered in applicationHost.config (preCondition: '{state.PreCondition}'); affects every managed application pool on this server.",
            User.GetRequiredUserId(),
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken);

        return Ok(ApiResponse<HtaccessModuleRegistrationState>.Ok(state, "htaccess.module_registered"));
    }
}
