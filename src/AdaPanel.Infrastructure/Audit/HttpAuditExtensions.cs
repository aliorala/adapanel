using System.Security.Claims;
using AdaPanel.Core.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AdaPanel.Infrastructure.Audit;

/// <summary>
/// Bir HTTP isteği bağlamında denetim kaydı yazmanın TEK yolu.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden yardımcı:</b> "kim (claims) + nereden (uzak IP) + ne zaman" üçlüsü her uçta aynı
/// şekilde çözülür. Her denetleyiciye elle kopyalandığında biri IP'yi, biri kullanıcıyı unutur ve
/// kayıt tam da lazım olduğunda eksik çıkar. Panelde bu kopyalama zaten başlamıştı
/// (DatabasesController'ın kendi özel <c>WriteAuditAsync</c>'i).
/// </para>
/// <para>
/// <b>Neden yutuyor (ama sessiz değil):</b> denetim kaydı ASIL İŞLEMİN yan ürünüdür. Posta kutusu
/// zaten açıldıktan sonra kayıt yazımı patlarsa istek 500 dönmemeli — kullanıcı işlemin
/// başarısız olduğunu sanıp tekrar dener, ikinci kez açılır. Ama sessizce yutmak da denetimi
/// kâğıt üstünde bırakır: hata <c>LogError</c> ile günlüğe düşer.
/// </para>
/// <para>
/// <b>Neden CancellationToken.None:</b> kayıt, işlem BİTTİKTEN sonra yazılır. İsteğin iptali
/// (kullanıcı sekmeyi kapattı) yazımı iptal etseydi, gerçekleşmiş bir değişiklik hiç iz
/// bırakmadan kalırdı — üstelik iptal, izi silmenin kolay bir yolu hâline gelirdi.
/// </para>
/// </remarks>
public static class HttpAuditExtensions
{
    public static async Task AuditAsync(
        this IAuditService auditService,
        HttpContext httpContext,
        string action,
        string resource,
        string details)
    {
        try
        {
            await auditService.WriteAsync(
                action,
                resource,
                details,
                ResolveUserId(httpContext.User),
                httpContext.Connection.RemoteIpAddress?.ToString(),
                CancellationToken.None);
        }
        catch (Exception exception)
        {
            httpContext.RequestServices
                .GetService<ILoggerFactory>()?
                .CreateLogger("AdaPanel.Audit")
                .LogError(
                    exception,
                    "Denetim kaydi YAZILAMADI (asil islem geri alinmadi): {Action} → {Resource}",
                    action,
                    resource);
        }
    }

    /// <summary>Kimlik çözülemezse null ("system") — çözülemedi diye kaydı hiç yazmamak daha kötü olurdu.</summary>
    private static Guid? ResolveUserId(ClaimsPrincipal? principal)
    {
        var value = principal?.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(value, out var userId) ? userId : null;
    }
}
