using AdaPanel.Api.Extensions;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Terminal;
using AdaPanel.Core.Interfaces;
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
        await auditService.WriteAsync(
            "terminal.command.executed",
            "terminal",
            $"PowerShell command finished with exit code {result.ExitCode} in '{result.WorkingDirectory}'.",
            User.GetRequiredUserId(),
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken);

        return Ok(ApiResponse<TerminalCommandResultDto>.Ok(result, "terminal.executed"));
    }
}
