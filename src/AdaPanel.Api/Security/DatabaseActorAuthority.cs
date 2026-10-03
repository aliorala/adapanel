using System.Security.Claims;
using AdaPanel.Api.Extensions;
using AdaPanel.Core.Common;
using AdaPanel.Core.Enums;
using AdaPanel.Core.Interfaces;
using AdaPanel.Data.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AdaPanel.Api.Security;

/// <summary>The authenticated actor and subscription approved before a database mutation queues.</summary>
internal sealed record DatabaseActorAuthority(Guid UserId, Guid SubscriptionId, string Role, string CredentialStamp)
{
    public static async Task<DatabaseActorAuthority> CaptureAsync(
        AdaPanelDbContext db, ICustomerScopeService scopes, ClaimsPrincipal principal, CancellationToken token)
    {
        var userId = principal.GetRequiredUserId();
        // Authentication ran before the request waited. Tracked rows from that phase cannot
        // establish that this account still exists, has this role, or remains enabled now.
        var actor = await db.Users.AsNoTracking().SingleOrDefaultAsync(user => user.Id == userId, token);
        if (actor is null || !actor.IsActive ||
            actor.Role is not (UserRole.Administrator or UserRole.Reseller or UserRole.Customer) ||
            principal.FindFirst(ClaimTypes.Role)?.Value != actor.Role.ToString())
            throw new AppException("auth.unauthorized", 401, 401);

        var stamp = CookieSessionValidator.ComputeStamp(actor.PasswordHash);
        if (principal.FindFirst(CookieSessionValidator.CredentialStampClaim)?.Value is { } sessionStamp && sessionStamp != stamp)
            throw new AppException("auth.unauthorized", 401, 401);

        var subscriptionId = await CustomerAccess.GetSubscriptionUserIdAsync(scopes, principal, token);
        if (subscriptionId != userId)
        {
            var parent = await db.Users.AsNoTracking().SingleOrDefaultAsync(user => user.Id == subscriptionId, token);
            if (parent is null || !parent.IsActive || parent.Role != UserRole.Customer ||
                await scopes.GetParentSubscriptionAsync(subscriptionId, token) is not null)
                throw new AppException("auth.forbidden", 403, 403);
        }
        await CustomerAccess.EnsureSectionAsync(scopes, principal, "database", token);
        return new(userId, subscriptionId, actor.Role.ToString(), stamp);
    }

    public async Task RevalidateAsync(
        AdaPanelDbContext db, ICustomerScopeService scopes, ClaimsPrincipal principal, CancellationToken token)
    {
        var current = await CaptureAsync(db, scopes, principal, token);
        if (current != this)
            throw new AppException("auth.unauthorized", 401, 401);
    }
}
