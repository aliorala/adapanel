using AdaPanel.Api.Security;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
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
[ApiController]
[Authorize(Roles = "Administrator,Reseller,Customer")]
[RequireSubUserSection("sites")]
[Route("api/v1/wordpress")]
public sealed class WordPressController(
    IWordPressToolkitService wordPressToolkitService,
    ICustomerScopeService customerScopes) : ControllerBase
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
        return Ok(ApiResponse<WordPressSiteDto>.Ok(install, "wp.maintenance_updated"));
    }

    // panel "Giriş yap": yöneticiyi siteye tek tıkla sokan, tek kullanımlık ve kısa ömürlü sihirli bağlantı üretir.
    [HttpPost("{siteName}/login-link")]
    public async Task<ActionResult<ApiResponse<WordPressLoginLinkDto>>> CreateLoginLinkAsync(string siteName, CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        var link = await wordPressToolkitService.CreateLoginLinkAsync(siteName, cancellationToken);
        return Ok(ApiResponse<WordPressLoginLinkDto>.Ok(link));
    }

    // panel "Güvenlik": WordPress güvenlik önlemleri demetini uygular (sürüm gizleme, XML-RPC pingback, yazar taraması,
    // dosya düzenleyici, anahtar yenileme).
    [HttpPost("{siteName}/harden")]
    public async Task<ActionResult<ApiResponse<WordPressHardenResultDto>>> HardenAsync(string siteName, CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        var result = await wordPressToolkitService.HardenAsync(siteName, cancellationToken);
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

        return Ok(ApiResponse<WordPressCloneResultDto>.Ok(result, "wp.clone_done"));
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
        return Ok(ApiResponse<WordPressCopyDataResultDto>.Ok(result, "wp.copy_done"));
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
        return Ok(ApiResponse<WordPressUpdateResultDto>.Ok(
            result,
            basarisiz == 0 ? "wp.updates_applied" : "wp.updates_partial"));
    }

}
