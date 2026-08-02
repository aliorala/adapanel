using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.PanelAccess;
using AdaPanel.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AdaPanel.Api.Controllers;

/// <summary>
/// Panelin kendi alan adıyla dışarıya açılması (ters vekil + Let's Encrypt).
/// <para>
/// Yalnız YÖNETİCİ: bu uç nokta IIS'te site oluşturur, sertifika alır ve panelin dış adresini
/// belirler — sunucu genelinde etkili, kiracıya ait olmayan bir işlemdir.
/// </para>
/// </summary>
[ApiController]
[Authorize(Roles = "Administrator")]
[Route("api/v1/panel-access")]
public sealed class PanelAccessController(IPanelPublicAccessService panelPublicAccessService) : ControllerBase
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
        return Ok(ApiResponse<PanelAccessStatusDto>.Ok(result, "panelAccess.setup_completed"));
    }
}
