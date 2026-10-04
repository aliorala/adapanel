using System.Security.Claims;
using AdaPanel.Api.Extensions;
using AdaPanel.Core.Common;
using AdaPanel.Core.Enums;
using AdaPanel.Core.Interfaces;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace AdaPanel.Api.Security;

/// <summary>The actor accepted before a file publication waits for site or content IO.</summary>
internal sealed record FileActorAuthority(Guid UserId, Guid SubscriptionId, string Role, string CredentialStamp,
    CookieSessionValidator.SourceActor? Cookie)
{
    internal static async Task<FileActorAuthority> CaptureAsync(AdaPanelDbContext db, ICustomerScopeService scopes,
        SessionRevocationStore sessions, ClaimsPrincipal principal, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var cookie = PanelAuthentication.IsTokenAuthenticated(principal)
            ? null : CookieSessionValidator.CaptureSourceActor(principal);
        var userId = principal.GetRequiredUserId();
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(item => item.Id == userId, token);
        if (user is null || !user.IsActive || user.Role is not (UserRole.Administrator or UserRole.Customer or UserRole.Reseller) ||
            principal.FindFirstValue(ClaimTypes.Role) != user.Role.ToString())
            throw new AppException("auth.unauthorized", 401, 401);
        var stamp = CookieSessionValidator.ComputeStamp(user.PasswordHash);
        if (cookie is not null && (cookie.UserId != userId || cookie.Role != user.Role.ToString() ||
            cookie.CredentialStamp != stamp || await sessions.IsRevokedAsync(cookie.SessionId, token)))
            throw new AppException("auth.unauthorized", 401, 401);
        if (cookie?.Delegation is { } delegation)
        {
            var admin = await db.Users.AsNoTracking().SingleOrDefaultAsync(item => item.Id == delegation.UserId, token);
            if (admin is not { IsActive: true, Role: UserRole.Administrator } ||
                CookieSessionValidator.ComputeStamp(admin.PasswordHash) != delegation.CredentialStamp ||
                await sessions.IsRevokedAsync(delegation.SessionId, token))
                throw new AppException("auth.unauthorized", 401, 401);
        }

        var subscription = await CustomerAccess.GetSubscriptionUserIdAsync(scopes, principal, token);
        if (subscription != userId)
        {
            var parent = await db.Users.AsNoTracking().SingleOrDefaultAsync(item => item.Id == subscription, token);
            if (parent is not { IsActive: true, Role: UserRole.Customer } ||
                await scopes.GetParentSubscriptionAsync(subscription, token) is not null)
                throw new AppException("auth.forbidden", 403, 403);
        }
        await CustomerAccess.EnsureSectionAsync(scopes, principal, "files", token);
        if (CustomerAccess.IsScoped(principal) && (await scopes.GetScopeAsync(subscription, token)).IsSuspended)
            throw new AppException("subscription.suspended_readonly", 403, 403);
        token.ThrowIfCancellationRequested();
        return new(userId, subscription, user.Role.ToString(), stamp, cookie);
    }

    internal async Task RevalidateAsync(AdaPanelDbContext db, ICustomerScopeService scopes,
        SessionRevocationStore sessions, ClaimsPrincipal principal, CancellationToken token)
    {
        if (await CaptureAsync(db, scopes, sessions, principal, token) != this)
            throw new AppException("auth.unauthorized", 401, 401);
    }
}
