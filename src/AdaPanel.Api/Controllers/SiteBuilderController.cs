using AdaPanel.Api.Security;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.SiteBuilder;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Audit;
using AdaPanel.Infrastructure.SiteBuilder;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AdaPanel.Api.Controllers;

/// <summary>
/// Site kurucu (Plesk "Web Presence Builder" karşılığı): hazır şablondan tek sayfalık site kurup
/// yayınlar.
/// </summary>
/// <remarks>
/// <para>
/// <b>Rol:</b> müşteri ve bayi dahil. Bu, kendi sitesinin içeriğini düzenleme işidir; yönetici
/// yetkisi istemek özelliğin var olma sebebini ortadan kaldırırdı (teknik olmayan kullanıcı).
/// Kapsam her uçta <see cref="CustomerAccess.EnsureSiteAsync"/> ile kilitlenir — bu olmadan
/// kiracı, istekteki site adını değiştirip BAŞKASININ sitesine yayın yapabilirdi.
/// </para>
/// <para>
/// <b>Neden yayın ayrı bir uç, hatta iki uç (plan + yayın):</b> yayın müşterinin dosyalarının
/// üzerine yazar. Planı ayrı uç yapmak, "ne olacağını göster" ile "yap"ı ayırır; arayüz onay
/// kutusunu planla doldurur ve kullanıcı ne onayladığını bilir.
/// </para>
/// </remarks>
[ApiController]
[Authorize(Roles = "Administrator,Reseller,Customer")]
[RequireSubUserSection("sites")]
[Route("api/v1/sitebuilder")]
public sealed class SiteBuilderController(
    ISiteBuilderService siteBuilderService,
    ICustomerScopeService customerScopes,
    IAuditService auditService) : ControllerBase
{
    /// <summary>Şablon/palet/yazı tipi seçenekleri. Siteye bağlı değil, kapsam denetimi gerekmez.</summary>
    [HttpGet("catalog")]
    public ActionResult<ApiResponse<SiteBuilderCatalogDto>> GetCatalog()
    {
        return Ok(ApiResponse<SiteBuilderCatalogDto>.Ok(siteBuilderService.GetCatalog()));
    }

    [HttpGet("{siteName}")]
    public async Task<ActionResult<ApiResponse<SiteBuilderProjectDto>>> GetProjectAsync(
        string siteName,
        CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        var project = await siteBuilderService.GetProjectAsync(siteName, cancellationToken);
        return Ok(ApiResponse<SiteBuilderProjectDto>.Ok(project));
    }

    [HttpPut("{siteName}")]
    public async Task<ActionResult<ApiResponse<SiteBuilderProjectDto>>> SaveProjectAsync(
        string siteName,
        [FromBody] SaveSiteBuilderProjectRequest request,
        CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        var project = await siteBuilderService.SaveProjectAsync(siteName, request, cancellationToken);

        // Tasarım KAYDI siteyi henüz değiştirmez; yine de iz bırakır — "sitemin içeriğini kim
        // değiştirdi" sorusu, yayın kaydından önceki adımı da kapsar.
        await auditService.AuditAsync(
            HttpContext,
            "sitebuilder.project.saved",
            siteName,
            $"Site builder design saved (template '{project.TemplateId}', palette '{project.PaletteId}').",
            await CustomerAccess.GetSubscriptionUserIdAsync(customerScopes, User, cancellationToken));

        return Ok(ApiResponse<SiteBuilderProjectDto>.Ok(project, "sitebuilder.saved"));
    }

    /// <summary>
    /// Panelde gösterilecek önizleme. HTML, JSON gövdesi içinde METİN olarak döner.
    /// </summary>
    /// <remarks>
    /// <b>Neden JSON, neden <c>text/html</c> değil:</b> HTML'i doğrudan servis etseydik sayfa
    /// PANELİN kendi kökeninde açılırdı. Kaçırmada tek bir eksik olsaydı, gömülen script
    /// panelin oturumuyla çalışırdı — yani müşterinin kendi sitesine gömdüğü bir script, panele
    /// giren yöneticinin yetkisini ele geçirirdi. Arayüz bu metni yalıtılmış (sandbox) bir
    /// çerçevede gösterir; oradaki script çalışamaz.
    /// </remarks>
    [HttpGet("{siteName}/preview")]
    public async Task<ActionResult<ApiResponse<string>>> PreviewAsync(string siteName, CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        var html = await siteBuilderService.RenderPreviewAsync(siteName, cancellationToken);
        return Ok(ApiResponse<string>.Ok(html));
    }

    /// <summary>Önizlemedeki görseller. Yalnız o siteye yüklenmiş, doğrulanmış görseller.</summary>
    [HttpGet("{siteName}/images/{imageName}")]
    public async Task<IActionResult> GetImageAsync(string siteName, string imageName, CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        var (path, contentType) = await siteBuilderService.OpenImageAsync(siteName, imageName, cancellationToken);

        // nosniff: tarayıcı içerik türünü TAHMİN etmesin. Bu uç yalnız doğrulanmış görsel
        // döndürür, ama tür tahmini bir gün yanlış bir dosyayı HTML sayarsa panel kökeninde
        // çalışırdı — kapatmanın maliyeti sıfır.
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        return PhysicalFile(path, contentType);
    }

    [HttpGet("{siteName}/images")]
    public async Task<ActionResult<ApiResponse<IReadOnlyCollection<SiteBuilderImageDto>>>> ListImagesAsync(
        string siteName,
        CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        var images = await siteBuilderService.GetImagesAsync(siteName, cancellationToken);
        return Ok(ApiResponse<IReadOnlyCollection<SiteBuilderImageDto>>.Ok(images));
    }

    /// <summary>
    /// Görsel yükleme. Site klasörüne DEĞİL, panelin veri köküne yazar (yayına kadar müşterinin
    /// dosyalarına dokunulmaz).
    /// </summary>
    [HttpPost("{siteName}/images")]
    [RequestSizeLimit(SiteBuilderCatalog.MaxImageBytes + (64 * 1024))]
    public async Task<ActionResult<ApiResponse<SiteBuilderImageDto>>> UploadImageAsync(
        string siteName,
        IFormFile file,
        CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        if (file is null || file.Length <= 0)
        {
            throw new AppException("sitebuilder.image_empty", 400, 400);
        }

        await using var stream = file.OpenReadStream();
        var image = await siteBuilderService.UploadImageAsync(siteName, stream, file.Length, cancellationToken);

        // DOSYA ADI KAYDA YAZILMAZ: gelen ad kullanıcı metnidir ve kayda ham geçirilmesi
        // denetim görünümünde bir enjeksiyon yüzeyi olurdu. Saklanan (panelin ürettiği) ad yazılır.
        await auditService.AuditAsync(
            HttpContext,
            "sitebuilder.image.uploaded",
            siteName,
            $"Site builder image stored as '{image.Name}' ({image.SizeBytes} bytes).",
            await CustomerAccess.GetSubscriptionUserIdAsync(customerScopes, User, cancellationToken));

        return Ok(ApiResponse<SiteBuilderImageDto>.Ok(image, "sitebuilder.image_uploaded"));
    }

    [HttpDelete("{siteName}/images/{imageName}")]
    public async Task<ActionResult<ApiResponse<object>>> DeleteImageAsync(
        string siteName,
        string imageName,
        CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        await siteBuilderService.DeleteImageAsync(siteName, imageName, cancellationToken);

        await auditService.AuditAsync(
            HttpContext,
            "sitebuilder.image.deleted",
            siteName,
            "Site builder image deleted.",
            await CustomerAccess.GetSubscriptionUserIdAsync(customerScopes, User, cancellationToken));

        return Ok(ApiResponse<object>.Ok(new { }, "sitebuilder.image_deleted"));
    }

    /// <summary>
    /// Yayın planı: nereye yazılacak, hangi dosyalar ezilecek. DİSKE YAZMAZ.
    /// </summary>
    /// <remarks>
    /// GET olması bilinçli: yan etkisi olmayan bir sorgu. Böylece arayüz, kullanıcı "Yayınla"
    /// ekranını her açtığında planı yeniden alabilir ve denetim kaydını gürültüye boğmaz.
    /// </remarks>
    [HttpGet("{siteName}/publish-plan")]
    public async Task<ActionResult<ApiResponse<SiteBuilderPublishPlanDto>>> PlanPublishAsync(
        string siteName,
        CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        var plan = await siteBuilderService.PlanPublishAsync(siteName, cancellationToken);
        return Ok(ApiResponse<SiteBuilderPublishPlanDto>.Ok(plan));
    }

    [HttpPost("{siteName}/publish")]
    public async Task<ActionResult<ApiResponse<SiteBuilderPublishResultDto>>> PublishAsync(
        string siteName,
        [FromBody] PublishSiteBuilderRequest request,
        CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        var result = await siteBuilderService.PublishAsync(siteName, request ?? new PublishSiteBuilderRequest(), cancellationToken);

        // EZİLEN DOSYALAR ADIYLA KAYDA GİRER. Bir müşteri "sitem gitti" dediğinde cevabı veren
        // tek kayıt budur: kim, ne zaman, hangi klasörde, hangi dosyaların üzerine yazdı.
        await auditService.AuditAsync(
            HttpContext,
            "sitebuilder.published",
            siteName,
            $"Site builder published to '{result.TargetPath}'; wrote {result.WrittenFiles.Count} file(s); "
            + $"overwrote {result.OverwrittenFiles.Count} pre-existing file(s): "
            + (result.OverwrittenFiles.Count == 0 ? "none" : string.Join(", ", result.OverwrittenFiles)),
            await CustomerAccess.GetSubscriptionUserIdAsync(customerScopes, User, cancellationToken));

        return Ok(ApiResponse<SiteBuilderPublishResultDto>.Ok(result, "sitebuilder.published"));
    }
}
