using AdaPanel.Api.Extensions;
using AdaPanel.Core.Common;
using AdaPanel.Core.Interfaces;
using Microsoft.AspNetCore.Mvc.Filters;

namespace AdaPanel.Api.Security;

/// <summary>
/// Alt-kullanıcı (abonelik altı ek kullanıcı) bölüm izni denetimi (Plesk "additional user" izin matrisi).
/// Controller'a konur; oturumdaki kullanıcı bir alt-kullanıcıysa ve izin listesi bu bölümü içermiyorsa 403.
/// Top-level müşteri, bayi ve admin etkilenmez (izin listesi yalnız alt-kullanıcıda bulunur).
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class RequireSubUserSectionAttribute(string section) : Attribute, IAsyncAuthorizationFilter
{
    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var user = context.HttpContext.User;
        if (!CustomerAccess.IsCustomer(user))
        {
            return;
        }

        var scopes = context.HttpContext.RequestServices.GetRequiredService<ICustomerScopeService>();
        var cancellationToken = context.HttpContext.RequestAborted;
        var userId = user.GetRequiredUserId();

        if (await scopes.GetParentSubscriptionAsync(userId, cancellationToken) is null)
        {
            return;
        }

        var allowed = await scopes.GetSubUserSectionsAsync(userId, cancellationToken);
        if (allowed is not null && !allowed.Contains(section, StringComparer.OrdinalIgnoreCase))
        {
            throw new AppException("auth.section_forbidden", 403, 403);
        }
    }
}
