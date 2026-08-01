using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Monitoring;
using AdaPanel.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AdaPanel.Api.Controllers;

/// <summary>
/// "Bu paylaşılan süreci KİM kullanıyor?" ucu — süreç listesinin cevaplayamadığı soruyu cevaplar.
///
/// YALNIZ Administrator: yanıt BAŞKA MÜŞTERİLERİN çalışan sorgularını ve istek URL'lerini içerir.
/// Müşteri/bayi rollerine açılırsa bir kiracı diğerinin verisini okuyabilir; bu yüzden rol kısıtı
/// bu ucun güvenlik sınırıdır (sorgu metni ayrıca serviste kısaltılır ve parola içerenler maskelenir).
/// </summary>
[ApiController]
[Authorize(Roles = "Administrator")]
[Route("api/v1/monitoring/consumers")]
public sealed class ResourceAttributionController(IResourceAttributionService resourceAttributionService) : ControllerBase
{
    /// <param name="source">"mysql" | "mariadb" | "postgresql" | "iis".</param>
    [HttpGet]
    public async Task<ActionResult<ApiResponse<ResourceConsumerSnapshotDto>>> GetAsync(
        [FromQuery] string source = "mariadb",
        [FromQuery] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await resourceAttributionService.GetConsumersAsync(source, limit, cancellationToken);
        return Ok(ApiResponse<ResourceConsumerSnapshotDto>.Ok(snapshot));
    }
}
