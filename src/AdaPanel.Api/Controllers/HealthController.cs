using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace AdaPanel.Api.Controllers;

[ApiController]
[Route("api/v1/health")]
public sealed class HealthController(ISetupService setupService) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ApiResponse<object>>> GetAsync(CancellationToken cancellationToken)
    {
        var setupStatus = await setupService.GetStatusAsync(cancellationToken);

        // Anonim uç: yalnız liveness + kurulum durumu (frontend yönlendirmesi için). Sunucu adı gibi
        // gereksiz iç bilgi sızdırma (machineName kaldırıldı).
        var payload = new
        {
            status = "ok",
            utcNow = DateTime.UtcNow,
            setup = setupStatus
        };

        return Ok(ApiResponse<object>.Ok(payload));
    }
}

