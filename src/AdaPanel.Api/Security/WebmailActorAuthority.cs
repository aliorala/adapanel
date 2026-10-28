using System.Security.Claims;
using AdaPanel.Core.Common;
using AdaPanel.Core.Enums;
using AdaPanel.Core.Interfaces;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace AdaPanel.Api.Security;

internal static class WebmailActorAuthority
{
    internal static async Task<string> CaptureAsync(AdaPanelDbContext db, ICustomerScopeService scopes,
        SessionRevocationStore sessions, ClaimsPrincipal principal, string domain, CancellationToken token)
    {
        if (!Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var actorId)) throw Unauthorized();
        var actor = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == actorId, token);
        if (actor is null || !actor.IsActive || actor.Role is not (UserRole.Administrator or UserRole.Reseller or UserRole.Customer) ||
            actor.Role.ToString() != principal.FindFirstValue(ClaimTypes.Role)) throw Unauthorized();
        var stamp = CookieSessionValidator.ComputeStamp(actor.PasswordHash);
        if (principal.FindFirstValue(CookieSessionValidator.CredentialStampClaim) is { } claimStamp && claimStamp != stamp) throw Unauthorized();
        string tokenStamp = "";
        if (PanelAuthentication.IsTokenAuthenticated(principal))
        {
            var hash = principal.FindFirstValue("adapanel_webmail_token_hash");
            if (string.IsNullOrEmpty(hash)) throw Unauthorized();
            var apiToken = await db.ApiTokens.AsNoTracking().SingleOrDefaultAsync(x => x.TokenHash == hash, token);
            if (apiToken is null || apiToken.UserId != actorId || apiToken.RevokedAtUtc is not null ||
                apiToken.ExpiresAtUtc <= DateTime.UtcNow) throw Unauthorized();
            tokenStamp = apiToken.Id.ToString("N");
        }
        else
        {
            var source = CookieSessionValidator.CaptureSourceActor(principal);
            if (source.UserId != actorId || source.CredentialStamp != stamp || await sessions.IsRevokedAsync(source.SessionId, token)) throw Unauthorized();
            if (source.Delegation is { } delegated)
            {
                var admin = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == delegated.UserId, token);
                if (admin is not { IsActive: true, Role: UserRole.Administrator } ||
                    CookieSessionValidator.ComputeStamp(admin.PasswordHash) != delegated.CredentialStamp ||
                    await sessions.IsRevokedAsync(delegated.SessionId, token)) throw Unauthorized();
            }
        }
        var subscription = await CustomerAccess.GetSubscriptionUserIdAsync(scopes, principal, token);
        if (subscription != actorId)
        {
            var parent = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == subscription, token);
            if (parent is not { IsActive: true, Role: UserRole.Customer } || await scopes.GetParentSubscriptionAsync(subscription, token) is not null) throw Unauthorized();
        }
        await CustomerAccess.EnsureSectionAsync(scopes, principal, "mail", token);
        await CustomerAccess.EnsureMailDomainAsync(scopes, principal, domain, token);
        if (CustomerAccess.IsScoped(principal))
        {
            var scope = await scopes.GetScopeAsync(subscription, token);
            if (scope.IsSuspended || scope.IsExpired) throw new AppException("subscription.suspended_readonly", 403, 403);
        }
        return $"{actorId:N}:{subscription:N}:{actor.Role}:{stamp}:{tokenStamp}";
    }
    private static AppException Unauthorized() => new("auth.unauthorized", 401, 401);
}
