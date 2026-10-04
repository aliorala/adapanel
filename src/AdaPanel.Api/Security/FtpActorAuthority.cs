using System.Security.Claims;
using AdaPanel.Api.Extensions;
using AdaPanel.Core.Common;
using AdaPanel.Core.Enums;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace AdaPanel.Api.Security;

/// <summary>The authenticated identity accepted before FTP account/native waits.</summary>
internal sealed class FtpActorAuthority
{
    private readonly Guid userId;
    private readonly string role;
    private readonly CookieSessionValidator.SourceActor? cookie;
    private string? acceptedStamp;
    internal ClaimsPrincipal Principal { get; }
    internal Guid UserId => userId;

    internal FtpActorAuthority(ClaimsPrincipal principal)
    {
        Principal = new ClaimsPrincipal(principal.Identities.Select(identity => new ClaimsIdentity(identity)));
        userId = Principal.GetRequiredUserId();
        role = Principal.FindFirst(ClaimTypes.Role)?.Value ?? "";
        if (role is not ("Administrator" or "Reseller")) throw new AppException("auth.forbidden", 403, 403);
        cookie = PanelAuthentication.IsTokenAuthenticated(Principal)
            ? null : CookieSessionValidator.CaptureSourceActor(Principal);
    }

    internal async Task ValidateAsync(AdaPanelDbContext db, SessionRevocationStore sessions, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var actor = await db.Users.AsNoTracking().SingleOrDefaultAsync(user => user.Id == userId, token);
        if (actor is null || !actor.IsActive || actor.Role is not (UserRole.Administrator or UserRole.Reseller) || actor.Role.ToString() != role)
            Unauthorized();
        var stamp = CookieSessionValidator.ComputeStamp(actor!.PasswordHash);
        if (acceptedStamp is not null && acceptedStamp != stamp) Unauthorized();
        if (cookie is not null && (cookie.UserId != userId || cookie.Role != role || cookie.CredentialStamp != stamp ||
            await sessions.IsRevokedAsync(cookie.SessionId, token))) Unauthorized();
        if (cookie?.Delegation is { } delegation)
        {
            var admin = await db.Users.AsNoTracking().SingleOrDefaultAsync(user => user.Id == delegation.UserId, token);
            if (admin is not { IsActive: true, Role: UserRole.Administrator } ||
                CookieSessionValidator.ComputeStamp(admin.PasswordHash) != delegation.CredentialStamp ||
                await sessions.IsRevokedAsync(delegation.SessionId, token)) Unauthorized();
        }
        acceptedStamp ??= stamp;
        token.ThrowIfCancellationRequested();
    }

    private static void Unauthorized() => throw new AppException("auth.unauthorized", 401, 401);
}
