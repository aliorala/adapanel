using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Terminal;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Audit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AdaPanel.Api.Controllers;

[ApiController]
[Authorize(Roles = "Administrator")]
[Route("api/v1/terminal")]
public sealed class TerminalController(
    ITerminalService terminalService,
    IAuditService auditService) : ControllerBase
{
    [HttpPost("execute")]
    public async Task<ActionResult<ApiResponse<TerminalCommandResultDto>>> ExecuteAsync(
        [FromBody] ExecuteTerminalCommandRequest request,
        CancellationToken cancellationToken)
    {
        var result = await terminalService.ExecuteAsync(request, cancellationToken);

        // KOMUTUN KENDİSİ kayda yazılır.
        //
        // Önceden yalnızca çıkış kodu ve çalışma dizini yazılıyordu; yani "bir komut çalıştı"
        // biliniyor ama HANGİ komut olduğu bilinmiyordu. Panel LocalSystem olarak koştuğu için bu
        // uç, sunucuda SYSTEM yetkisiyle keyfî kod çalıştırmanın ta kendisidir: komut kaydedilmezse
        // bir olaydan sonra "sunucuda ne yapıldı" sorusu CEVAPSIZ kalır — denetim kaydının var
        // olma sebebi tam olarak budur.
        //
        // Komut, yazma yolundaki merkezî temizleyiciden geçer (anahtar=değer biçimli sırlar
        // maskelenir, 2000 karaktere kırpılır); ayrıca "denetim yazımı asıl işlemi bozmaz"
        // güvencesi için AuditAsync yardımcısı kullanılır.
        await auditService.AuditAsync(
            HttpContext,
            "terminal.command.executed",
            "terminal",
            $"PowerShell command in '{result.WorkingDirectory}' finished with exit code {result.ExitCode}"
            + (result.TimedOut ? " (timed out)" : string.Empty)
            + $". Command: {request.Command}");

        return Ok(ApiResponse<TerminalCommandResultDto>.Ok(result, "terminal.executed"));
    }
}
