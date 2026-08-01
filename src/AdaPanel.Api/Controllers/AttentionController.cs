using AdaPanel.Core.Dtos.Advisor;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AdaPanel.Api.Controllers;

// Menü rozetleri + bildirim çekmecesi. Sunucu-geneli operasyonel sinyaller (servisler, DNS,
// güncellemeler) topladığı için admin-only — bayi/müşteriye sunucu iç durumu sızmaz.
[ApiController]
[Authorize(Roles = "Administrator")]
[Route("api/v1/attention")]
public sealed class AttentionController(IAttentionService attentionService) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ApiResponse<AttentionSummaryDto>>> GetAsync(CancellationToken cancellationToken)
        => Ok(ApiResponse<AttentionSummaryDto>.Ok(await attentionService.GetSummaryAsync(cancellationToken)));
}
