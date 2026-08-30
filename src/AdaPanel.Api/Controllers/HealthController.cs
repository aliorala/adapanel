using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Interfaces;
using AdaPanel.Api.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace AdaPanel.Api.Controllers;

[ApiController]
// KIMLIKSIZ CANLILIK UCU. Bu denetleyicide [Authorize] HIC yok -- yani ucu anonim, ve olmasi
// da gerekiyor: arayuz panele girmeden ONCE kurulum yapilmis mi diye buraya bakiyor ve dis
// izleme araclari da tipik olarak bu ucu yoklar. Sinir bu yuzden PublicRead (IP basina
// 300/dk): dis bir izleme aracinin makul yoklama hizindan (dakikada birkac kez) kat kat
// genis, ama kimliksiz bir uctan surekli kurulum durumu okutmayi ucuz olmaktan cikariyor.
[EnableRateLimiting(PanelRateLimits.PublicRead)]
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

