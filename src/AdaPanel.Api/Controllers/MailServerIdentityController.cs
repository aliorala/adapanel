using AdaPanel.Infrastructure.Audit;
using AdaPanel.Api.Extensions;
using AdaPanel.Api.Security;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Mail;
using AdaPanel.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AdaPanel.Api.Controllers;

[ApiController]
[Authorize(Roles = "Administrator")]
[RequireSubUserSection("mail")]
[Route("api/v1/mail/server-identity")]
public sealed class MailServerIdentityController(IMailService mailService, IAuditService auditService) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ApiResponse<MailServerIdentityDto>>> GetAsync(CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        return Ok(ApiResponse<MailServerIdentityDto>.Ok(await mailService.GetServerIdentityAsync(cancellationToken)));
    }

    [HttpPost]
    public async Task<ActionResult<ApiResponse<MailServerIdentityDto>>> SetAsync(
        [FromBody] SetMailServerIdentityRequest request, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        var result = await mailService.SetServerIdentityAsync(request, cancellationToken);
        await auditService.AuditAsync(HttpContext, "mail.server_identity.updated", result.ConfiguredHostName,
            $"Mail server identity request completed. Applied: {result.Applied}; supported: {result.Supported}; steps: {string.Join(",", result.Steps.Select(s => s.Key + "=" + s.Status))}.");
        return Ok(ApiResponse<MailServerIdentityDto>.Ok(result));
    }
}
