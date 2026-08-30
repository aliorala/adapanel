using System.Security.Claims;
using AdaPanel.Api.Extensions;
using AdaPanel.Api.Security;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Settings;
using AdaPanel.Core.Enums;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Audit;
using AdaPanel.Infrastructure.Branding;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace AdaPanel.Api.Controllers;

/// <summary>
/// Özel marka (white-label): panel adı, logo, favicon, vurgu rengi.
/// </summary>
/// <remarks>
/// <para>
/// <b>Model Plesk'ten alındı</b> (önce araştırıldı, sonra kopyalandı): yönetici SUNUCU GENELİ
/// markayı belirler ve GİRİŞ EKRANINDA görünen marka budur; bayi ise kendi markasını belirler ve
/// onu yalnızca kendi müşterileri, panele girdikten sonra görür. Plesk'te de merkezî giriş sayfası
/// bayi bazında markalanamaz — giriş anında kullanıcının kim olduğu, dolayısıyla hangi bayiye ait
/// olduğu henüz bilinmez. Bunu adres/alan adıyla tahmin etmeye çalışmak, kimlik doğrulanmadan
/// önce "bu sunucuda şu bayi var" bilgisini herkese açardı.
/// </para>
/// <para>
/// <b>KAPSAM İSTEKTEN ALINMAZ.</b> Bütün yazma uçları <see cref="ResolveScope"/> ile çalışır ve o
/// yalnızca oturumun rolüne + kimliğine bakar: yönetici → sunucu, bayi → KENDİSİ. Yani bir bayinin
/// başka bir bayinin markasına yazması, bir denetimin unutulmasıyla değil, ancak bu metodun
/// değiştirilmesiyle mümkün olur. Denetim kaydı da aynı kapsamla yazılır.
/// </para>
/// </remarks>
[ApiController]
[Authorize(Roles = "Administrator,Reseller")]
[Route("api/v1/branding")]
public sealed class BrandingController(
    IBrandingService brandingService,
    IAuditService auditService) : ControllerBase
{
    /// <summary>
    /// İzleyicinin GÖRECEĞİ marka. Kimliksiz istek sunucu markasını alır — giriş ekranı bu ucu
    /// kullanır, çünkü marka tam da kullanıcı henüz giriş yapmamışken gerekir.
    /// </summary>
    /// <remarks>
    /// Anonim olması bir sızıntı DEĞİL: yanıt yalnız sunucu markasını içerir (bayi markaları
    /// yalnızca kimliği çözülmüş isteklerde döner) ve uç zaten gizli giriş yolunun + IP
    /// kısıtlamasının arkasındadır (SecureEntryPathMiddleware).
    /// </remarks>
    [AllowAnonymous]
    // Sinifin TEK kimliksiz ucu, dolayisiyla hiz siniri de yalniz burada. Giris ekrani her
    // acilista bir kez cagirir; 300/dk bir insanin sayfayi yenilemesine fazlasiyla yeter ve
    // kimliksiz bir uctan veritabani okutmayi ucuz olmaktan cikarir.
    [EnableRateLimiting(PanelRateLimits.PublicRead)]
    [HttpGet]
    public async Task<ActionResult<ApiResponse<BrandingDto>>> GetAsync(CancellationToken cancellationToken)
    {
        var (userId, role) = ResolveViewer(User);
        return Ok(ApiResponse<BrandingDto>.Ok(await brandingService.GetEffectiveAsync(userId, role, cancellationToken)));
    }

    /// <summary>Düzenleme formunun okuduğu HAM kayıt (çağıranın KENDİ kapsamı).</summary>
    [HttpGet("settings")]
    public async Task<ActionResult<ApiResponse<BrandingDto>>> GetSettingsAsync(CancellationToken cancellationToken)
        => Ok(ApiResponse<BrandingDto>.Ok(await brandingService.GetScopeAsync(ResolveScope(User), cancellationToken)));

    [HttpPut("settings")]
    public async Task<ActionResult<ApiResponse<BrandingDto>>> UpdateSettingsAsync(
        [FromBody] UpdateBrandingRequest request,
        CancellationToken cancellationToken)
    {
        var scope = ResolveScope(User);
        var saved = await brandingService.UpdateAsync(scope, request, cancellationToken);
        await WriteAuditAsync(
            scope,
            "branding.updated",
            $"Panel name '{saved.PanelName}', accent '{saved.AccentColor}', logo link '{saved.LogoLinkUrl}'.");

        return Ok(ApiResponse<BrandingDto>.Ok(saved, "branding.saved"));
    }

    /// <summary>
    /// Logo yükleme.
    /// </summary>
    /// <remarks>
    /// <c>RequestSizeLimit</c> içerik denetiminin YERİNE geçmez, ondan öncedir: sınırı burada
    /// koymazsak yüzlerce megabaytlık bir gövde önce belleğe/diske alınır, ancak sonra
    /// "çok büyük" denirdi. Asıl karar <see cref="BrandingImageRules"/>'da ve İÇERİĞE bakar.
    /// </remarks>
    [HttpPost("logo")]
    [RequestSizeLimit(BrandingImageRules.MaxLogoBytes + (64 * 1024))]
    public Task<ActionResult<ApiResponse<BrandingDto>>> UploadLogoAsync(IFormFile file, CancellationToken cancellationToken)
        => UploadAsync(BrandingImageKind.Logo, file, cancellationToken);

    [HttpDelete("logo")]
    public Task<ActionResult<ApiResponse<BrandingDto>>> RemoveLogoAsync(CancellationToken cancellationToken)
        => RemoveAsync(BrandingImageKind.Logo, cancellationToken);

    [HttpPost("favicon")]
    [RequestSizeLimit(BrandingImageRules.MaxFaviconBytes + (64 * 1024))]
    public Task<ActionResult<ApiResponse<BrandingDto>>> UploadFaviconAsync(IFormFile file, CancellationToken cancellationToken)
        => UploadAsync(BrandingImageKind.Favicon, file, cancellationToken);

    [HttpDelete("favicon")]
    public Task<ActionResult<ApiResponse<BrandingDto>>> RemoveFaviconAsync(CancellationToken cancellationToken)
        => RemoveAsync(BrandingImageKind.Favicon, cancellationToken);

    private async Task<ActionResult<ApiResponse<BrandingDto>>> UploadAsync(
        BrandingImageKind kind,
        IFormFile? file,
        CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0)
        {
            throw new AppException("branding.image_empty", 400, 400);
        }

        if (file.Length > BrandingImageRules.MaxBytes(kind))
        {
            // Belleğe hiç almadan reddet: sınır zaten aşılmışsa dosyayı okumanın faydası yok.
            throw new AppException("branding.image_too_large", 413, 413);
        }

        using var buffer = new MemoryStream();
        await using (var stream = file.OpenReadStream())
        {
            await stream.CopyToAsync(buffer, cancellationToken);
        }

        var scope = ResolveScope(User);
        var saved = await brandingService.SetImageAsync(scope, kind, buffer.ToArray(), cancellationToken);
        await WriteAuditAsync(scope, $"branding.{Name(kind)}_uploaded", $"{buffer.Length} bytes.");
        return Ok(ApiResponse<BrandingDto>.Ok(saved, "branding.saved"));
    }

    private async Task<ActionResult<ApiResponse<BrandingDto>>> RemoveAsync(BrandingImageKind kind, CancellationToken cancellationToken)
    {
        var scope = ResolveScope(User);
        var saved = await brandingService.RemoveImageAsync(scope, kind, cancellationToken);
        await WriteAuditAsync(scope, $"branding.{Name(kind)}_removed", "Removed.");
        return Ok(ApiResponse<BrandingDto>.Ok(saved, "branding.saved"));
    }

    private static string Name(BrandingImageKind kind) => kind == BrandingImageKind.Logo ? "logo" : "favicon";

    /// <summary>
    /// Çağıranın YAZABİLECEĞİ tek kapsam. Yönetici sunucu markasını, bayi yalnız kendi markasını
    /// düzenler; başka bir rol buraya hiç gelemez (denetleyici <c>Administrator,Reseller</c>'a kilitli).
    /// </summary>
    /// <remarks>
    /// <c>internal</c>: bu kural özelliğin bütün izolasyonunu taşıyor, dolayısıyla doğrudan test
    /// edilebilmeli. "Denetleyicide bir yerde vardır" varsaymak, tam da denetlenmeyen şeydir.
    /// </remarks>
    internal static BrandingScope ResolveScope(ClaimsPrincipal user)
        => user.IsInRole(nameof(UserRole.Reseller))
            ? new BrandingScope(user.GetRequiredUserId())
            : BrandingScope.Server;

    /// <summary>Kimliksiz istekte (giriş ekranı) ikisi de null döner → sunucu markası.</summary>
    internal static (Guid? UserId, UserRole? Role) ResolveViewer(ClaimsPrincipal user)
    {
        if (!(user.Identity?.IsAuthenticated ?? false))
        {
            return (null, null);
        }

        var rawId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(rawId, out var userId))
        {
            return (null, null);
        }

        return Enum.TryParse<UserRole>(user.FindFirstValue(ClaimTypes.Role), out var role)
            ? (userId, role)
            : (userId, null);
    }

    /// <summary>
    /// Denetim kaydı. Bayi kapsamındaki değişiklik KONUSU bayinin kendisi olarak yazılır; böylece
    /// kayıt, bayinin kendi denetim görünümünde de çıkar (yöneticinin oradaki müdahalesi dahil).
    /// </summary>
    private Task WriteAuditAsync(BrandingScope scope, string action, string details)
        => auditService.AuditAsync(
            HttpContext,
            action,
            scope.IsServer ? "branding/server" : $"branding/reseller/{scope.StorageKey}",
            details,
            scope.ResellerId);
}
