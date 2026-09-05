using AdaPanel.Api.Extensions;
using AdaPanel.Api.Security;
using AdaPanel.Core.Dtos.Asp;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Audit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AdaPanel.Api.Controllers;

[ApiController]
[Authorize(Roles = "Administrator,Reseller")]
[Route("api/v1/sites")]
public sealed class AspController(
    IAspNetService aspNetService,
    ICustomerScopeService customerScopes,
    IAuditService auditService) : ControllerBase
{
    [HttpGet("{siteName}/asp")]
    public async Task<ActionResult<ApiResponse<AspSiteConfigurationDto>>> GetSiteConfigurationAsync(
        string siteName,
        CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        var configuration = await aspNetService.GetSiteConfigurationAsync(siteName, cancellationToken);
        return Ok(ApiResponse<AspSiteConfigurationDto>.Ok(configuration));
    }

    [HttpPost("{siteName}/asp/classic")]
    public async Task<ActionResult<ApiResponse<AspSiteConfigurationDto>>> SetClassicAspAsync(
        string siteName,
        [FromBody] UpdateClassicAspRequest request,
        CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        var configuration = await aspNetService.SetClassicAspAsync(siteName, request, cancellationToken);
        await WriteAuditAsync("aspnet.classic.updated", siteName, cancellationToken);
        return Ok(ApiResponse<AspSiteConfigurationDto>.Ok(configuration, "aspnet.classic_asp_updated"));
    }

    [HttpPost("{siteName}/dotnet")]
    public async Task<ActionResult<ApiResponse<AspSiteConfigurationDto>>> SetDotNetVersionAsync(
        string siteName,
        [FromBody] UpdateDotNetRuntimeRequest request,
        CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        var configuration = await aspNetService.SetDotNetVersionAsync(siteName, request, cancellationToken);
        await WriteAuditAsync("aspnet.dotnet.updated", siteName, cancellationToken);
        return Ok(ApiResponse<AspSiteConfigurationDto>.Ok(configuration, "aspnet.dotnet_updated"));
    }

    [HttpPost("{siteName}/mixed")]
    public async Task<ActionResult<ApiResponse<AspSiteConfigurationDto>>> EnableMixedHandlersAsync(
        string siteName,
        [FromBody] EnableMixedHandlersRequest request,
        CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        var configuration = await aspNetService.EnableMixedHandlersAsync(siteName, request, cancellationToken);
        await WriteAuditAsync("aspnet.mixed.updated", siteName, cancellationToken, HandlerOrderDetail(configuration));

        // SADE "etkinleştirildi" ARTIK KOŞULLU. Handler listenin BAŞINA konamadıysa (devralınan
        // StaticFile önde kalır) her .php/.asp isteği 404.3 döner; panel bunu "başarılı" diye
        // yazıyordu. Ölçülemediyse de "başarılı" DEMEYİZ.
        var notice = configuration.HandlerOrderNoticeKey;
        return Ok(ApiResponse<AspSiteConfigurationDto>.Ok(
            configuration,
            notice.Length > 0 ? notice : "aspnet.mixed_handlers_updated"));
    }

    /// <summary>
    /// Denetim kaydına ÖLÇÜLEN sırayı yazar — KISMI BAŞARIYI ADIYLA (hangi handler önümüzde).
    /// </summary>
    private static string HandlerOrderDetail(AspSiteConfigurationDto configuration)
    {
        static string Anlat(string etiket, HandlerOrderMeasurement olcum)
            => olcum.Unverified
                ? $"{etiket}: order UNVERIFIED"
                : olcum.Missing
                    ? $"{etiket}: handler MISSING after commit"
                    : olcum.PrecedesCatchAll
                        ? $"{etiket}: precedes catch-all (index {olcum.HandlerIndex})"
                        : $"{etiket}: SHADOWED by '{olcum.BlockingHandlerName}' (index {olcum.CatchAllIndex} < {olcum.HandlerIndex}) -> 404.3";

        var parcalar = new List<string>();
        if (configuration.HasPhpHandler)
        {
            parcalar.Add(Anlat("php handler", configuration.PhpHandlerOrder));
        }

        if (configuration.ClassicAspEnabled)
        {
            parcalar.Add(Anlat("classic asp handler", configuration.ClassicAspHandlerOrder));
        }

        return parcalar.Count == 0 ? string.Empty : string.Join("; ", parcalar);
    }

    /// <remarks>
    /// <b>Kiracı bağı:</b> kayıt <c>WriteAsync</c> ile konusuz yazılıyordu. Bu uçlar yöneticiye ve
    /// bayiye de açık; onların kiracının sitesinde yaptığı değişiklik, kaydın <c>UserId</c>'si
    /// kendileri olduğu için kiracının denetim görünümünde HİÇ çıkmıyordu. Konu artık sitenin
    /// abonelik sahibidir; site sahipsizse (yöneticinin kendi sitesi) boş kalır.
    /// </remarks>
    private async Task WriteAuditAsync(string action, string siteName, CancellationToken cancellationToken, string detail = "")
    {
        await auditService.WriteForSubscriptionAsync(
            action,
            $"aspnet:{siteName}",
            $"ASP/ASP.NET settings for '{siteName}' were updated.{(detail.Length > 0 ? " " + detail + "." : string.Empty)}",
            User.GetRequiredUserId(),
            await AuditSubject.SiteOwnerAsync(customerScopes, siteName, cancellationToken),
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken);
    }
}
