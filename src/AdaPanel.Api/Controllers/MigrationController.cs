using System.Text.Json;
using AdaPanel.Api.Extensions;
using AdaPanel.Api.Security;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Migration;
using AdaPanel.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;

namespace AdaPanel.Api.Controllers;

// Plesk/cPanel/ham sunucu → AdaPanel site göçü (tek akış): site oluştur + dosya çek (FTP/SFTP) + DB aktar.
// Admin-only. Her adım bağımsız → kısmi başarı olabilir; sonuç adım-adım döner.
[ApiController]
[Authorize(Roles = "Administrator")]
[Route("api/v1/migration")]
public sealed class MigrationController(ISiteMigrationService migrationService) : ControllerBase
{
    [HttpPost("site")]
    [RequestSizeLimit(8192)]
    public async Task<ActionResult<ApiResponse<MigrateSiteResultDto>>> MigrateSiteAsync(
        [FromBody] MigrateSiteRequest request,
        CancellationToken cancellationToken)
    {
        var result = await migrationService.MigrateSiteAsync(
            request,
            User.GetRequiredUserId(),
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            onProgress: null,
            cancellationToken);

        return Ok(ApiResponse<MigrateSiteResultDto>.Ok(
            result,
            result.OverallSuccess ? "migration.site.success" : "migration.site.partial"));
    }

    // Aynı göç, ama CANLI: her aşama (plan/başla/sonuç/bitti) üretildiği an SSE (text/event-stream) ile akar →
    // istemci "hangi adımda ne yapılıyor"u anında gösterir. Tek istek; göç bitince akış kapanır. İstemci sekmeyi
    // kapatırsa istek iptal olur (mevcut /site ile aynı davranış). Her olay tek satır: "data: {json}\n\n".
    [HttpPost("site/stream")]
    [RequestSizeLimit(8192)]
    public async Task MigrateSiteStreamAsync(
        [FromBody] MigrateSiteRequest request,
        CancellationToken cancellationToken)
    {
        Response.Headers.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers["X-Accel-Buffering"] = "no"; // ara proxy varsa tamponlamayı kapat
        HttpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        var serializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        async Task WriteEventAsync(MigrationProgressEvent progressEvent, CancellationToken ct)
        {
            var json = JsonSerializer.Serialize(progressEvent, serializerOptions);
            await Response.WriteAsync($"data: {json}\n\n", ct);
            await Response.Body.FlushAsync(ct);
        }

        try
        {
            await migrationService.MigrateSiteAsync(
                request,
                User.GetRequiredUserId(),
                HttpContext.Connection.RemoteIpAddress?.ToString(),
                WriteEventAsync,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // İstemci bağlantıyı kapattı (sekme kapandı/yenilendi) → sessizce bitir.
        }
        catch (Exception exception)
        {
            // Beklenmeyen hata → akışa bir "done(fail)" çerçevesi yaz ki istemci sonsuz "çalışıyor"da takılmasın.
            try
            {
                await WriteEventAsync(
                    new MigrationProgressEvent
                    {
                        Phase = "done",
                        OverallSuccess = false,
                        Message = exception is AppException appException ? appException.MessageKey : "migration.site.partial"
                    },
                    HttpContext.RequestAborted);
            }
            catch
            {
                // bağlantı zaten kapanmış olabilir → yut
            }
        }
    }

    // Plesk REST API keşfi: domain'leri otomatik listele (sihirbazı doldurmak için). Best-effort.
    [HttpPost("plesk/discover")]
    [RequestSizeLimit(8192)]
    public async Task<ActionResult<ApiResponse<PleskDiscoverResultDto>>> DiscoverPleskAsync(
        [FromBody] PleskDiscoverRequest request,
        CancellationToken cancellationToken)
    {
        var result = await migrationService.DiscoverPleskAsync(request, cancellationToken);
        return Ok(ApiResponse<PleskDiscoverResultDto>.Ok(result));
    }

    // cPanel/WHM keşfi: hesap/domain'leri otomatik listele (WHM listaccts). Best-effort.
    [HttpPost("cpanel/discover")]
    [RequestSizeLimit(8192)]
    public async Task<ActionResult<ApiResponse<PleskDiscoverResultDto>>> DiscoverCpanelAsync(
        [FromBody] CpanelDiscoverRequest request,
        CancellationToken cancellationToken)
    {
        var result = await migrationService.DiscoverCpanelAsync(request, cancellationToken);
        return Ok(ApiResponse<PleskDiscoverResultDto>.Ok(result));
    }

    // Göç ÖNCESİ ön-kontrol: site adı + dosya/DB kaynağı erişilebilirliği. Göç başlatmadan "bağlanır mı" der.
    [HttpPost("validate")]
    [RequestSizeLimit(8192)]
    public async Task<ActionResult<ApiResponse<MigrationValidationResultDto>>> ValidateAsync(
        [FromBody] MigrateSiteRequest request,
        CancellationToken cancellationToken)
    {
        var result = await migrationService.ValidateMigrationAsync(request, cancellationToken);
        return Ok(ApiResponse<MigrationValidationResultDto>.Ok(result));
    }
}
