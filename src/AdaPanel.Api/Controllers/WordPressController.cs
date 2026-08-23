using AdaPanel.Api.Security;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Audit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AdaPanel.Api.Controllers;

// WORDPRESS TOOLKIT ÜRÜNÜN AMİRAL GEMİSİ MÜŞTERİ ÖZELLİĞİDİR — ve müşteriye kapalıydı.
//
// "Administrator,Reseller" kısıtı yüzünden abonelik sahibi kendi WordPress'ini güncelleyemiyor,
// bakım moduna alamıyor, sertleştiremiyor, staging klonu açamıyordu. Panelin sattığı özelliği
// tam da onu satın alan kullanamıyordu.
//
// Her uç kaynağın çağırana ait olduğunu doğrular: liste kapsamla SÜZÜLÜR
// (GetEffectiveSitesAsync), tekil eylemler EnsureSiteAsync'ten geçer, klon ayrıca alan adı
// iddiasını (EnsureDomainClaimAsync) ve abonelik site kotasını denetler.
//
// DENETİM KAYDI: bu denetleyicinin durum değiştiren ALTI ucunun hiçbiri iz bırakmıyordu.
// En ağırı GİRİŞ BAĞLANTISI: uç, WordPress'e YÖNETİCİ OTURUMU açan tek kullanımlık sihirli bir
// bağlantı üretir — panelin kimliğiyle sitenin içine girmenin yoludur ve panelde hiçbir kaydı
// yoktu. İkincisi VERİ KOPYALAMA: kaynağın dosya ve veritabanını hedefe YIKICI biçimde aynalar
// (/MIR), yani canlı içeriği geri dönüşsüz ezebilir. Klonlama YENİ BİR SİTE oluşturur ve onu
// kaynağın sahibinin kapsamına ekler; bakım kipi siteyi ziyaretçilere kapatır; sertleştirme ve
// güncelleme sitenin kodunu değiştirir.
//
// KİRACI BAĞI: her uç bir SİTE üzerinde çalışır ve bu uçlar yönetici/bayiye de açıktır. Kaydın
// konusu yazılmazsa, kendi sitesine yönetici oturumu açılan kiracı bunu kendi denetim ekranında
// göremezdi.
[ApiController]
[Authorize(Roles = "Administrator,Reseller,Customer")]
[RequireSubUserSection("sites")]
[Route("api/v1/wordpress")]
public sealed class WordPressController(
    IWordPressToolkitService wordPressToolkitService,
    ICustomerScopeService customerScopes,
    IAuditService auditService) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ApiResponse<IReadOnlyCollection<WordPressSiteDto>>>> GetAllAsync(CancellationToken cancellationToken)
    {
        var installs = await wordPressToolkitService.DetectAllAsync(cancellationToken);

        // Bayi/müşteri yalnızca etkin kapsamındaki sitelerin WP kurulumlarını görür.
        var allowed = await CustomerAccess.GetEffectiveSitesAsync(customerScopes, User, cancellationToken);
        if (allowed is not null)
        {
            installs = installs
                .Where(item => allowed.Contains(item.SiteName, StringComparer.OrdinalIgnoreCase))
                .ToArray();
        }

        return Ok(ApiResponse<IReadOnlyCollection<WordPressSiteDto>>.Ok(installs));
    }

    [HttpGet("{siteName}")]
    public async Task<ActionResult<ApiResponse<WordPressSiteDto>>> DetectAsync(string siteName, CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        var install = await wordPressToolkitService.DetectAsync(siteName, cancellationToken);
        return Ok(ApiResponse<WordPressSiteDto>.Ok(install));
    }

    [HttpPost("{siteName}/maintenance")]
    public async Task<ActionResult<ApiResponse<WordPressSiteDto>>> SetMaintenanceAsync(
        string siteName,
        [FromBody] SetWordPressMaintenanceRequest request,
        CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        var install = await wordPressToolkitService.SetMaintenanceAsync(siteName, request.Enabled, cancellationToken);

        await WpAuditAsync(
            "wordpress.maintenance_changed",
            siteName,
            $"WordPress maintenance mode set to {request.Enabled}; site is "
            + (request.Enabled ? "hidden from visitors." : "visible again."),
            cancellationToken);

        return Ok(ApiResponse<WordPressSiteDto>.Ok(install, "wp.maintenance_updated"));
    }

    // panel "Giriş yap": yöneticiyi siteye tek tıkla sokan, tek kullanımlık ve kısa ömürlü sihirli bağlantı üretir.
    [HttpPost("{siteName}/login-link")]
    public async Task<ActionResult<ApiResponse<WordPressLoginLinkDto>>> CreateLoginLinkAsync(string siteName, CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        var link = await wordPressToolkitService.CreateLoginLinkAsync(siteName, cancellationToken);

        // BAĞLANTININ KENDİSİ ASLA YAZILMAZ: içindeki tek kullanımlık jeton, o siteye yönetici
        // olarak girmenin ta kendisidir; kayda düşerse denetim kaydı bir giriş anahtarı deposuna
        // dönüşür ve yedeklere taşınır. Yazılan şey OLGUDUR: kim, hangi siteye, hangi WordPress
        // kullanıcısı adına oturum açma hakkı üretti.
        await WpAuditAsync(
            "wordpress.login_link_created",
            siteName,
            "One-time WordPress administrator login link was created "
            + $"(valid for {link.ExpiresInSeconds} second(s)).",
            cancellationToken);

        return Ok(ApiResponse<WordPressLoginLinkDto>.Ok(link));
    }

    // panel "Güvenlik": WordPress güvenlik önlemleri demetini uygular (sürüm gizleme, XML-RPC pingback, yazar taraması,
    // dosya düzenleyici, anahtar yenileme).
    [HttpPost("{siteName}/harden")]
    public async Task<ActionResult<ApiResponse<WordPressHardenResultDto>>> HardenAsync(string siteName, CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        var result = await wordPressToolkitService.HardenAsync(siteName, cancellationToken);

        await WpAuditAsync(
            "wordpress.hardened",
            siteName,
            $"WordPress hardening applied ({result.Applied.Count} measure(s): {string.Join(", ", result.Applied)}).",
            cancellationToken);

        return Ok(ApiResponse<WordPressHardenResultDto>.Ok(result, "wp.hardened"));
    }

    // panel "Klonla": kaynak WP'yi yeni bir staging sitesine kopyalar. Klon, kaynağın sahibi müşterinin kapsamına
    // eklenir (Plesk: klon aynı abonelikte kalır) → Bayi de kendi sitesinin klonunu görür ve yönetir.
    [HttpPost("{siteName}/clone")]
    public async Task<ActionResult<ApiResponse<WordPressCloneResultDto>>> CloneAsync(
        string siteName,
        [FromBody] CloneWordPressRequest request,
        [FromServices] IIisSiteService iisSiteService,
        [FromServices] IMailService mailService,
        [FromServices] ILogger<WordPressController> logger,
        CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);

        // GÜVENLİK (çapraz-kiracı): klon YENİ BİR SİTE OLUŞTURUR ve host adını buradan alır, yani
        // site oluşturma/bağlama uçlarıyla aynı alan adı iddiasını üretir. Burada denetlenmezse
        // kısıt bu yoldan dolanılırdı: hedefe "kurban.com" yazan bayi, kurbanın izinli mail
        // domainlerini devralır (GetAllowedMailDomainsAsync bağlamalardan türetir) ve kurbanın
        // posta hesaplarını yönetmeye başlar. Dahası klon SİLİNİRKEN cascade, kurbanın mail
        // domainini hesaplarıyla birlikte siler.
        //
        // Hedef site YENİ olduğu için ownSiteName geçilmiyor: kaynak sitenin kendi host adı bu
        // iddiaya gerekçe olamaz.
        await CustomerAccess.EnsureDomainClaimAsync(
            customerScopes, iisSiteService, mailService, User, request.TargetHostName, null, logger, cancellationToken);

        // HEDEF SİTE ADI DA BİR SAHİPLİK İDDİASIDIR — ve klon yolunda hiç denetlenmiyordu.
        //
        // SitesController site oluştururken adı bilinçli olarak iddia sayıp aynı fonksiyondan
        // geçiriyor; klon ise YALNIZ host adını soruyordu. Oysa klon da tam olarak bir site
        // oluşturur (CloneAsync → iisSiteService.CreateSiteAsync) ve WordPressToolkitService'teki
        // ad doğrulaması (IsSafeName) NOKTA KABUL EDER, yani "kurban.com" geçerli bir hedef adıdır.
        // Açılan yol:
        //   1. müşteri TargetSiteName="kurban.com" ile klonlar;
        //   2. aşağıdaki atama yeni siteyi saldırganın kapsamına EKLER;
        //   3. CustomerScopeService.GetAllowedMailDomainsAsync site ADINI izinli posta alan adı
        //      sayar → kurbanın posta kutuları listelenir, PAROLALARI DEĞİŞTİRİLİR, alias ile
        //      postası yönlendirilir;
        //   4. DNS bölgesi de site adından türediği için devralınır.
        // Yani host adı denetlenirken adın denetlenmemesi, kısıtın yanından dolaşılan kapıydı.
        //
        // İKİNCİ BİR KURAL YAZILMIYOR: host adıyla aynı fonksiyon çağrılıyor. Ayrı yazılsaydı
        // biri www/alt-alan-adı kuralını alır, diğeri almazdı — panelde bu hata sınıfı zaten
        // yaşandı. ownSiteName geçilmiyor: hedef YENİ bir site, kaynağın kendi adı bu iddiaya
        // gerekçe olamaz.
        await CustomerAccess.EnsureDomainClaimAsync(
            customerScopes, iisSiteService, mailService, User, request.TargetSiteName, null, logger, cancellationToken);

        // ...ve ad boşta olsa bile ÜZERİNE YAZILAMAZ. CloneAsync'in kendi çakışma denetimi
        // GetSitesAsync'e bakıyor, o ise SİSTEM SİTELERİNİ gizler — gizlenmiş bir siteyi "yok"
        // saymak, tam da üzerine yazılmasına izin vermek olurdu. Bu denetim GetSiteAsync kullanır.
        await CustomerAccess.EnsureSiteNameIsClaimableAsync(
            customerScopes, iisSiteService, User, request.TargetSiteName, cancellationToken);

        // Kaynağın sahibi müşteri varsa: önce kota denetimi (Plesk gibi). Sahibi yoksa (yönetici sitesi) sınırsız.
        var ownerId = await customerScopes.GetSiteOwnerUserIdAsync(siteName, cancellationToken);
        if (ownerId is Guid quotaOwner)
        {
            var ownerScope = await customerScopes.GetScopeAsync(quotaOwner, cancellationToken);
            if (ownerScope.MaxSites > 0 && ownerScope.Sites.Count >= ownerScope.MaxSites)
            {
                throw new AppException("wp.clone_quota", 403, 403);
            }
        }

        var result = await wordPressToolkitService.CloneAsync(siteName, request, cancellationToken);

        // Klonu kaynağın kapsamına ata → müşteri/bayi yeni siteyi görür.
        if (ownerId is Guid assignOwner)
        {
            var scope = await customerScopes.GetScopeAsync(assignOwner, cancellationToken);
            if (!scope.Sites.Contains(result.TargetSiteName, StringComparer.OrdinalIgnoreCase))
            {
                await customerScopes.SetScopeAsync(assignOwner, scope.Sites.Append(result.TargetSiteName), scope.MaxSites, scope.MaxDiskMb, cancellationToken);
            }
        }

        // KAYDIN KONUSU KAYNAĞIN SAHİBİDİR, hedefin değil: hedef site bu istekle DOĞDU ve kapsama
        // yukarıda daha yeni eklendi. Kaynağın sahibi, klonun kime ait olduğunu da belirler.
        await WpAuditAsync(
            "wordpress.cloned",
            siteName,
            $"WordPress site cloned to '{result.TargetSiteName}' (host '{request.TargetHostName}'); "
            + $"URL rewritten: {result.UrlRewritten}.",
            cancellationToken,
            ownerId);

        // KISMİ BAŞARI GİZLENMEZ: adres değiştirilemediyse klon üretim adresini servis eder ve
        // kullanıcının bunu BİLMESİ gerekir — aksi hâlde staging sandığı yerde canlı veriyi değiştirir.
        return Ok(ApiResponse<WordPressCloneResultDto>.Ok(
            result,
            result.UrlRewritten ? "wp.clone_done" : "wp.clone_url_not_replaced"));
    }

    // panel "Veri Kopyala": kaynak WP'nin dosya/DB'sini VAR OLAN hedefe taşır (staging→canlı). İki siteye de erişim gerekir.
    [HttpPost("{siteName}/copy-data")]
    public async Task<ActionResult<ApiResponse<WordPressCopyDataResultDto>>> CopyDataAsync(
        string siteName,
        [FromBody] CopyWordPressDataRequest request,
        CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, request.TargetSiteName ?? string.Empty, cancellationToken);
        var result = await wordPressToolkitService.CopyDataAsync(siteName, request, cancellationToken);

        // KAYIT HEDEFE YAZILIR: yıkım orada olur (hedefin dosyaları ve veritabanı kaynağınkiyle
        // AYNALANIR). "Sitem neden dünkü staging içeriğine döndü" sorusunu soracak olan da hedefin
        // sahibidir; kaydı kaynağa bağlamak, onu görmesi gereken kiracıdan gizlemek olurdu.
        await WpAuditAsync(
            "wordpress.data_copied",
            request.TargetSiteName ?? string.Empty,
            $"WordPress data mirrored from '{siteName}' onto '{request.TargetSiteName}' "
            + $"(destructive mirror; URL rewritten: {result.UrlRewritten}).",
            cancellationToken);

        return Ok(ApiResponse<WordPressCopyDataResultDto>.Ok(
            result,
            result.UrlRewritten ? "wp.copy_done" : "wp.copy_url_not_replaced"));
    }

    /// <summary>Sitedeki güncellenebilir öğeler (çekirdek + eklenti + tema).</summary>
    [HttpGet("{siteName}/updates")]
    public async Task<ActionResult<ApiResponse<WordPressUpdatesDto>>> GetUpdatesAsync(string siteName, CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        return Ok(ApiResponse<WordPressUpdatesDto>.Ok(await wordPressToolkitService.GetUpdatesAsync(siteName, cancellationToken)));
    }

    /// <summary>Seçilen öğeleri günceller.</summary>
    [HttpPost("{siteName}/updates")]
    public async Task<ActionResult<ApiResponse<WordPressUpdateResultDto>>> ApplyUpdatesAsync(
        string siteName,
        [FromBody] WordPressUpdateRequest request,
        CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        var result = await wordPressToolkitService.ApplyUpdatesAsync(siteName, request, cancellationToken);

        // Kısmi başarı GİZLENMEZ: bir öğe güncellenemediyse mesaj bunu söyler, çünkü kullanıcının
        // hangi eklentinin geride kaldığını bilmesi gerekir.
        var basarisiz = result.Results.Count(item => !item.Success);

        // KISMİ SONUÇ KAYDA DA GİRER: "güncellendi" demek ile hepsinin güncellenmiş olması aynı şey
        // değil. Kaç öğenin geride kaldığı yazılmazsa kayıt, bir olaydan sonra yanlış hikâye anlatır.
        await WpAuditAsync(
            "wordpress.updates_applied",
            siteName,
            $"WordPress updates applied: {result.Results.Count - basarisiz} succeeded, {basarisiz} failed.",
            cancellationToken);

        return Ok(ApiResponse<WordPressUpdateResultDto>.Ok(
            result,
            basarisiz == 0 ? "wp.updates_applied" : "wp.updates_partial"));
    }

    /// <summary>
    /// WordPress işlemlerinin ortak denetim izi (yapan = oturumdaki kullanıcı, konu = sitenin sahibi).
    /// </summary>
    /// <param name="sahip">
    /// Sahip çağrı yerinde zaten çözülmüşse (klonlama kotayı denetlerken okuyor) yeniden sorulmasın diye.
    /// </param>
    private async Task WpAuditAsync(
        string action,
        string siteName,
        string ayrinti,
        CancellationToken cancellationToken,
        Guid? sahip = null)
        => await auditService.AuditAsync(
            HttpContext,
            action,
            $"wordpress:{siteName}",
            ayrinti,
            sahip ?? await AuditSubject.SiteOwnerAsync(customerScopes, siteName, cancellationToken));
}
