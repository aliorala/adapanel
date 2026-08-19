using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.PanelAccess;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Audit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AdaPanel.Api.Controllers;

/// <summary>
/// Panelin kendi alan adıyla dışarıya açılması (ters vekil + Let's Encrypt).
/// <para>
/// Yalnız YÖNETİCİ: bu uç nokta IIS'te site oluşturur, sertifika alır ve panelin dış adresini
/// belirler — sunucu genelinde etkili, kiracıya ait olmayan bir işlemdir.
/// </para>
/// <para>
/// <b>Denetim kaydı:</b> bu uç PANELİN KENDİSİNİ internete açar — yönetim arayüzünün dış adresini
/// belirler, IIS'te ters vekil sitesi kurar ve sertifika alır. Panelin dış yüzeyini değiştiren tek
/// yol budur; iz bırakmazsa "panel ne zamandır dışarıdan erişilebilir ve bu adresi kim koydu"
/// sorusu cevapsız kalır. MCP uç noktası da aynı adresle açılır, yani yapay zeka istemcilerinin
/// sunucuya giriş kapısı da burada belirlenir.
/// </para>
/// </summary>
[ApiController]
[Authorize(Roles = "Administrator")]
[Route("api/v1/panel-access")]
public sealed class PanelAccessController(
    IPanelPublicAccessService panelPublicAccessService,
    IAuditService auditService) : ControllerBase
{
    /// <summary>Mevcut durumu ölçer; hiçbir şeyi değiştirmez.</summary>
    [HttpGet]
    public async Task<ActionResult<ApiResponse<PanelAccessStatusDto>>> GetAsync(CancellationToken cancellationToken)
        => Ok(ApiResponse<PanelAccessStatusDto>.Ok(await panelPublicAccessService.GetStatusAsync(cancellationToken)));

    /// <summary>
    /// Kurulumu yapar. Yanıt her adımın sonucunu ayrı ayrı taşır; bir adım başarısızsa
    /// <c>overallStatus</c> "fail" olur ve hangi adımın patladığı adım listesinden görülür.
    /// </summary>
    [HttpPost("setup")]
    public async Task<ActionResult<ApiResponse<PanelAccessStatusDto>>> SetupAsync(
        [FromBody] SetupPanelAccessRequest request,
        CancellationToken cancellationToken)
    {
        var result = await panelPublicAccessService.SetupAsync(request, cancellationToken);

        // İSTENEN alan adı ile ÖLÇÜLEN sonuç birlikte yazılır: "kurdum" demek ile kurulmuş olmak
        // aynı şey değil (adımlardan biri patlarsa overallStatus fail olur ama uç yine 200 döner).
        await auditService.AuditAsync(
            HttpContext,
            "panel.access.setup",
            $"panel-host:{request.HostName}",
            $"Panel public access setup for '{request.HostName}' "
            + $"(staging ACME: {request.UseStaging}); result: {result.OverallStatus}, "
            + $"certificate: {result.CertificateSource}, panel url: '{result.PanelUrl}'.");

        return Ok(ApiResponse<PanelAccessStatusDto>.Ok(result, "panelAccess.setup_completed"));
    }
}
