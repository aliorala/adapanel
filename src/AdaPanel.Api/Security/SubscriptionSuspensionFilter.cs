using AdaPanel.Core.Interfaces;
using Microsoft.AspNetCore.Mvc.Filters;

namespace AdaPanel.Api.Security;

/// <summary>
/// <see cref="SubscriptionSuspension"/> kuralını BÜTÜN MVC uçlarına uygulayan küresel filtre.
/// </summary>
/// <remarks>
/// <para>
/// Program.cs'te <c>options.Filters.Add</c> ile bir kez kaydedilir. Controller'lara tek tek
/// serpiştirilmiyor: yaptırımın "unutulabilir" olması, olmamasıyla aynı kapıya çıkardı — panelde
/// 50'nin üzerinde controller var ve yeni eklenen her biri sessizce muaf olurdu.
/// </para>
/// <para>
/// Yetkilendirme filtresi olarak çalışır: kimlik doğrulandıktan SONRA ama eylem çalışmadan ÖNCE.
/// Kimliği doğrulanmamış istek zaten <c>[Authorize]</c> tarafından reddedilir.
/// </para>
/// </remarks>
public sealed class SubscriptionSuspensionFilter : IAsyncAuthorizationFilter
{
    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var scopes = context.HttpContext.RequestServices.GetRequiredService<ICustomerScopeService>();

        await SubscriptionSuspension.EnsureAllowedAsync(
            scopes,
            context.HttpContext.User,
            context.HttpContext.Request.Method,
            context.HttpContext.Request.Path.Value,
            context.HttpContext.RequestAborted);
    }
}
