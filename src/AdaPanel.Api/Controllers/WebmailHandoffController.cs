using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using AdaPanel.Api.Security;
using AdaPanel.Core.Common;
using AdaPanel.Core.Interfaces;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Mail;
using AdaPanel.Infrastructure.Audit;
using AdaPanel.Infrastructure.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AdaPanel.Api.Controllers;

/// <summary>A fresh same-origin document supplies a narrowly scoped cross-origin form policy.</summary>
[ApiController]
[Authorize(Roles = "Administrator,Customer,Reseller")]
[RequireSubUserSection("mail")]
[IgnoreAntiforgeryToken]
[Route("api/v1/mail/webmail/handoff")]
[RequestSizeLimit(2048)]
public sealed class WebmailHandoffController(WebmailSsoTickets tickets, AdaPanelDbContext db,
    ICustomerScopeService scopes, SessionRevocationStore sessions, IAuditService auditService,
    IIisSiteService iisSiteService) : ControllerBase
{
    [HttpPost]
    [Consumes("application/x-www-form-urlencoded")]
    public async Task<IActionResult> PostAsync([FromForm(Name = "adapanel_sso")] string handoffToken, CancellationToken token)
    {
        var origin = Request.Scheme + "://" + Request.Host.ToUriComponent();
        if (Request.Headers.Origin.ToString() != origin ||
            !Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var actorId)) throw Invalid();
        var grant = tickets.Inspect(handoffToken, actorId, origin);
        if (await WebmailActorAuthority.CaptureAsync(db, scopes, sessions, User, grant.Domain, token) != grant.Authority)
            throw Invalid();
        // Validate the destination again before inserting it into HTML or CSP. Host input never
        // determines the credential target; the ticket already owns that immutable account ID.
        var host = "webmail." + WebmailRequestValidation.Domain(grant.Domain);
        if (grant.Host != host) throw Invalid();
        await AuditHandoffAsync(grant, token);
        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        Response.Headers.CacheControl = "no-store, max-age=0";
        Response.Headers.Pragma = "no-cache";
        Response.Headers["Referrer-Policy"] = "strict-origin";
        Response.Headers["Content-Security-Policy"] = "default-src 'none'; script-src 'nonce-" + nonce
            + "'; form-action https://" + host + "; base-uri 'none'; frame-ancestors 'none'";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers["X-Frame-Options"] = "DENY";
        var encoder = HtmlEncoder.Default;
        return Content("<!doctype html><html lang=\"en\"><meta charset=\"utf-8\"><title>Webmail</title>"
            + "<form id=\"adapanel-webmail-handoff\" method=\"post\" action=\"" + encoder.Encode("https://" + host + "/") + "\">"
            + "<input type=\"hidden\" name=\"adapanel_sso\" value=\"" + encoder.Encode(handoffToken) + "\">"
            + "<noscript><button type=\"submit\">Continue to webmail</button></noscript></form>"
            + "<script nonce=\"" + encoder.Encode(nonce) + "\">window.opener=null;document.getElementById('adapanel-webmail-handoff').submit();</script></html>",
            "text/html; charset=utf-8");
    }
    internal async Task AuditHandoffAsync(WebmailSsoTickets.Grant grant, CancellationToken token) =>
        await auditService.AuditAsync(HttpContext, "mail.webmail.handoff", grant.Address,
            "Authorized mailbox handoff document issued.",
            await AuditSubject.MailDomainOwnerAsync(scopes, iisSiteService, grant.Domain, token));
    private static AppException Invalid() => new("mail.webmail_sso_invalid", 403, 403);
}
