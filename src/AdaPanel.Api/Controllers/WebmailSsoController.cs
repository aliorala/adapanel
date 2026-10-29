using System.Net;
using AdaPanel.Api.Security;
using AdaPanel.Core.Common;
using AdaPanel.Core.Interfaces;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Mail;
using AdaPanel.Infrastructure.Audit;
using AdaPanel.Infrastructure.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace AdaPanel.Api.Controllers;

[ApiController]
[AllowAnonymous]
[IgnoreAntiforgeryToken]
[Route("api/internal/webmail-sso")]
[RequestSizeLimit(2048)]
[EnableRateLimiting(PanelRateLimits.PublicRead)]
public sealed class WebmailSsoController(WebmailSsoTickets tickets, WebmailSsoDeployment deployment,
    WebmailSsoService sso, AdaPanelDbContext db, ICustomerScopeService scopes, SessionRevocationStore sessions,
    IAuditService auditService, IIisSiteService? iisSites = null) : ControllerBase
{
    public sealed class ConsumeRequest
    {
        public string Ticket { get; set; } = "";
        public string Host { get; set; } = "";
        public string Origin { get; set; } = "";
    }
    public sealed class RevokeRequest { public string Lease { get; set; } = ""; }

    [HttpPost("consume")]
    [Consumes("application/x-www-form-urlencoded")]
    public async Task<IActionResult> ConsumeAsync([FromForm] ConsumeRequest request, CancellationToken token)
    {
        await EnsureBrokerAsync(token);
        var grant = tickets.Take(request.Ticket, request.Host, request.Origin);
        async Task Authorize(CancellationToken ct)
        {
            if (await WebmailActorAuthority.CaptureAsync(db, scopes, sessions, grant.Actor, grant.Domain, ct) != grant.Authority)
                throw new AppException("auth.unauthorized", 401, 401);
        }
        var login = await sso.ConsumeAsync(grant, Authorize, token, async _ =>
        {
            await AuditSignonAsync(grant, token);
        });
        Response.Headers.CacheControl = "no-store";
        return Ok(login);
    }

    internal async Task AuditSignonAsync(WebmailSsoTickets.Grant grant, CancellationToken token)
    {
        HttpContext.User = grant.Actor;
        await auditService.AuditAsync(HttpContext, "mail.webmail.sso", grant.Address, "Selected mailbox webmail sign-in granted.",
            await AuditSubject.MailDomainOwnerAsync(scopes, iisSites ?? HttpContext.RequestServices.GetRequiredService<IIisSiteService>(), grant.Domain, token));
    }

    [HttpPost("revoke")]
    [Consumes("application/x-www-form-urlencoded")]
    public async Task<IActionResult> RevokeAsync([FromForm] RevokeRequest request, CancellationToken token)
    {
        await EnsureBrokerAsync(token);
        if (request.Lease.Length > 100) throw new AppException("mail.webmail_sso_invalid", 403, 403);
        var domain = await sso.GetLeaseDomainAsync(request.Lease, token);
        await sso.RevokeAsync(request.Lease, token);
        await AuditRevocationAsync(domain, token);
        Response.Headers.CacheControl = "no-store";
        return Ok(new { revoked = true });
    }

    internal async Task AuditRevocationAsync(string? domain, CancellationToken token) =>
        await auditService.AuditAsync(HttpContext, "mail.webmail.sso.revoked", "webmail-sso",
            "Webmail broker requested revocation of its owned app credential.",
            await AuditSubject.MailDomainOwnerAsync(scopes, iisSites ?? HttpContext.RequestServices.GetRequiredService<IIisSiteService>(), domain, token));

    private async Task EnsureBrokerAsync(CancellationToken token)
    {
        // Check the direct socket at both ends. ARR/forwarded requests must not turn the
        // public panel endpoint into a usable credential broker.
        if (HttpContext.Connection.RemoteIpAddress is not { } remote || !IPAddress.IsLoopback(remote) ||
            HttpContext.Connection.LocalIpAddress is not { } local || !IPAddress.IsLoopback(local) ||
            !Request.Host.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            Request.Headers.Keys.Any(x => x.StartsWith("X-Forwarded-", StringComparison.OrdinalIgnoreCase) || x.Equals("Forwarded", StringComparison.OrdinalIgnoreCase)) ||
            !await deployment.AuthenticateBrokerAsync(Request.Headers["X-AdaPanel-Webmail-Broker"].ToString(), token))
            throw new AppException("mail.webmail_sso_invalid", 403, 403);
    }
}
