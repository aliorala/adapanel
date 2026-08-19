using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Store;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Audit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AdaPanel.Api.Controllers;

// DENETİM KAYDI: uygulama kurulumu sitenin belge köküne DIŞARIDAN İNDİRİLEN KOD yazar ve
// `cleanInstall` açıkken hedef klasörün MEVCUT İÇERİĞİNİ kenara taşıyıp klasörü boşaltır. Yani hem
// tedarik zinciri sorusu ("bu sitedeki kod nereden geldi") hem veri kaybı sorusu ("sitemin içeriği
// nereye gitti") bu kayda bağlıdır.
//
// Üretilen yönetici PAROLASI (AppInstallResultDto.AdminPassword) kayda ASLA yazılmaz; kullanıcı adı
// ve adres yazılır — parolayı yazmak, kaydı sitenin yönetici kimliğinin ikinci deposu yapardı.
[ApiController]
[Authorize(Roles = "Administrator")]
[Route("api/v1/store")]
public sealed class StoreController(
    IAppStoreService appStoreService,
    ICustomerScopeService customerScopes,
    IAuditService auditService) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ApiResponse<IReadOnlyCollection<AppTemplateDto>>>> GetTemplatesAsync(CancellationToken cancellationToken)
    {
        var templates = await appStoreService.GetTemplatesAsync(cancellationToken);
        return Ok(ApiResponse<IReadOnlyCollection<AppTemplateDto>>.Ok(templates));
    }

    [HttpPost("install")]
    public async Task<ActionResult<ApiResponse<AppInstallResultDto>>> InstallAsync([FromBody] InstallAppTemplateRequest request, CancellationToken cancellationToken)
    {
        var result = await appStoreService.InstallAsync(request, cancellationToken);

        Guid? siteSahibi = null;
        try
        {
            // Kiracı, kendi sitesine yönetici tarafından kurulan uygulamayı görebilmeli.
            siteSahibi = await customerScopes.GetSiteOwnerUserIdAsync(request.SiteName, cancellationToken);
        }
        catch
        {
            // Sahip çözülemezse kayıt sunucu geneli olarak yazılır; hiç yazmamak çok daha kötü olurdu.
        }

        await auditService.AuditAsync(
            HttpContext,
            "store.app.installed",
            $"site:{request.SiteName}",
            $"App template '{request.TemplateId}' installed into '{request.TargetPath}' "
            + $"(existing content wiped first: {request.CleanInstall}); "
            + $"admin user: '{result.AdminUser}', admin url: '{result.AdminUrl}'.",
            siteSahibi);

        return Ok(ApiResponse<AppInstallResultDto>.Ok(result, "store.installed"));
    }
}
