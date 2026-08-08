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

    /// <summary>
    /// Sorgu GEÇMİŞİ: biten sorguların filtrelenebilir kaydı + "hangi sorgu çok çalışmış" özeti.
    /// </summary>
    /// <remarks>
    /// Canlı liste yalnız O ANI gösterir ve sorgular saniyeler içinde gelip gider; bu uç geriye
    /// dönük bakmayı sağlar. Aynı güvenlik sınırı geçerli (Administrator-only): yanıt BAŞKA
    /// müşterilerin sorgu metinlerini içerir.
    /// </remarks>
    [HttpGet("history")]
    public async Task<ActionResult<ApiResponse<QueryLogPageDto>>> GetHistoryAsync(
        [FromServices] IQueryLogService queryLogService,
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        [FromQuery] string source = "",
        [FromQuery] string siteName = "",
        [FromQuery] string target = "",
        [FromQuery] string user = "",
        [FromQuery] int minDurationSeconds = 0,
        [FromQuery] string search = "",
        [FromQuery] bool remoteOnly = false,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 100,
        CancellationToken cancellationToken = default)
    {
        var page = await queryLogService.QueryAsync(
            new QueryLogFilterDto
            {
                FromUtc = from,
                ToUtc = to,
                Source = source,
                SiteName = siteName,
                Target = target,
                User = user,
                MinDurationSeconds = minDurationSeconds,
                Search = search,
                RemoteOnly = remoteOnly,
                Skip = skip,
                Take = take
            },
            cancellationToken);

        return Ok(ApiResponse<QueryLogPageDto>.Ok(page));
    }
}
