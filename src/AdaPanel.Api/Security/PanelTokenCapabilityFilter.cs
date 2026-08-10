using Microsoft.AspNetCore.Mvc.Filters;

namespace AdaPanel.Api.Security;

/// <summary>
/// <see cref="PanelTokenCapabilities"/> kuralını BÜTÜN MVC uçlarına uygulayan küresel filtre.
/// </summary>
/// <remarks>
/// Program.cs'te bir kez kaydedilir. Controller'lara tek tek serpiştirilmiyor: yaptırımın
/// "unutulabilir" olması, olmamasıyla aynı kapıya çıkardı — yeni eklenen her controller sessizce
/// muaf olurdu. Çerezle gelen istekte hiçbir şey yapmaz; karar <see cref="PanelTokenCapabilities"/>
/// içinde tek yerde.
/// </remarks>
public sealed class PanelTokenCapabilityFilter : IAuthorizationFilter
{
    public void OnAuthorization(AuthorizationFilterContext context)
    {
        PanelTokenCapabilities.EnsureAllowed(
            context.HttpContext.User,
            context.HttpContext.Request.Method,
            context.HttpContext.Request.Path.Value);
    }
}
