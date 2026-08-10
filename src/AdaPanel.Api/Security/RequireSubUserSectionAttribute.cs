using AdaPanel.Core.Interfaces;
using Microsoft.AspNetCore.Mvc.Filters;

namespace AdaPanel.Api.Security;

/// <summary>
/// Alt-kullanıcı (abonelik altı ek kullanıcı) bölüm izni denetimi (Plesk "additional user" izin matrisi).
/// Controller'a konur; oturumdaki kullanıcı bir alt-kullanıcıysa ve izin listesi bu bölümü içermiyorsa 403.
/// Top-level müşteri, bayi ve admin etkilenmez (izin listesi yalnız alt-kullanıcıda bulunur).
/// </summary>
/// <remarks>
/// Kararın KENDİSİ burada değil <see cref="CustomerAccess.EnsureSectionAsync"/> içinde. Sebebi
/// ölçüldü: öznitelik bir MVC filtresidir ve MCP araçları MVC boru hattından geçmez, dolayısıyla
/// kural burada yazılı kaldığı sürece MCP tarafında HİÇ uygulanmıyordu. Bu sınıf artık yalnız
/// kablolama yapar.
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class RequireSubUserSectionAttribute(string section) : Attribute, IAsyncAuthorizationFilter
{
    public Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var scopes = context.HttpContext.RequestServices.GetRequiredService<ICustomerScopeService>();

        return CustomerAccess.EnsureSectionAsync(
            scopes,
            context.HttpContext.User,
            section,
            context.HttpContext.RequestAborted);
    }
}
