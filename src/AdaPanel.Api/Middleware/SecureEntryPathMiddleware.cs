using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Interfaces;

namespace AdaPanel.Api.Middleware;

public sealed class SecureEntryPathMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, ISetupService setupService, IPanelSecurityService securityService)
    {
        var path = context.Request.Path;
        if (IsAlwaysPublicPath(path))
        {
            await next(context);
            return;
        }

        var setupStatus = await setupService.GetStatusAsync(context.RequestAborted);
        if (!setupStatus.IsInitialized)
        {
            await next(context);
            return;
        }

        var isAllowedIp = await securityService.IsIpAllowedAsync(context.Connection.RemoteIpAddress?.ToString(), context.RequestAborted);
        if (!isAllowedIp)
        {
            await RejectApiRequestAsync(context, StatusCodes.Status403Forbidden, "security.ip_not_allowed");
            return;
        }

        // MCP ucu: kimlik doğrulaması bearer anahtarıyla yapılır. Gizli giriş yolu başlığını burada
        // zorunlu kılmak MCP istemcisini gereksiz kırılganlaştırır — anahtar zaten yüksek entropili
        // bir sırdır ve tek tek iptal edilebilir. IP kısıtlaması ise YUKARIDA uygulanmış durumda,
        // yani kaldırılmıyor.
        //
        // NOT: IP kısıtlaması kullanılıyorsa yapay zeka sağlayıcısının çıkış adresleri listeye
        // eklenmelidir; aksi hâlde bağlantı 403 alır.
        if (path.StartsWithSegments("/mcp", out _))
        {
            await next(context);
            return;
        }

        if (path.StartsWithSegments("/api", out _))
        {
            if (RequiresSecureEntryHeader(path))
            {
                var expectedPath = await securityService.GetSecureEntryPathAsync(context.RequestAborted);
                var providedPath = context.Request.Headers["X-AdaPanel-Entry-Path"].ToString().Trim().Trim('/').ToLowerInvariant();

                if (!string.Equals(providedPath, expectedPath, StringComparison.OrdinalIgnoreCase))
                {
                    await RejectApiRequestAsync(context, StatusCodes.Status404NotFound, "security.invalid_entry_path");
                    return;
                }
            }

            await next(context);
            return;
        }

        var secureEntryPath = await securityService.GetSecureEntryPathAsync(context.RequestAborted);
        if (!path.HasValue || path == "/")
        {
            context.Response.Redirect($"/{secureEntryPath}");
            return;
        }

        if (path.StartsWithSegments($"/{secureEntryPath}", out var remainder))
        {
            context.Request.Path = remainder.HasValue ? remainder : "/";
            await next(context);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status404NotFound;
    }

    private static bool IsAlwaysPublicPath(PathString path)
    {
        return path.StartsWithSegments("/assets", out _)
            || path.StartsWithSegments("/swagger", out _)
            || path.StartsWithSegments("/favicon", out _)
            || path.StartsWithSegments("/robots.txt", out _)
            || path.StartsWithSegments("/index.html", out _)
            || path.StartsWithSegments("/api/v1/setup", out _)
            || path.StartsWithSegments("/api/v1/health", out _);
    }

    /// <summary>
    /// Gizli giriş yolu başlığı ZORUNLU MU?
    /// <para>
    /// Kural TERSİNE çevrildi: <c>/api</c> altındaki HER ŞEY başlık ister, yalnız aşağıdaki kısa
    /// muafiyet listesi istemez. Önceki hâli bunun tersiydi — korunan uçlar tek tek sayılıyordu ve
    /// listeye eklenmeyen her YENİ denetleyici sessizce korumasız kalıyordu. Denetimde tam olarak bu
    /// oldu: advisor, antivirus, attention, dns/server, dns/template, monitoring/*, server-health,
    /// sub-users, wordpress, migration, service-plans, dbtools, api-tokens, error-reports ve
    /// panel-access uçları listeye hiç girmemişti. Sayıma dayalı koruma, unutmayı "açık" olarak
    /// cezalandırır; kurala dayalı koruma ise en fazla gereksiz sıkılık üretir.
    /// </para>
    /// </summary>
    internal static bool RequiresSecureEntryHeader(PathString path)
    {
        // TARAYICININ KENDİSİNİN GEZDİĞİ yollar başlık isteyemez.
        //
        // /api/v1/dbtools, phpMyAdmin'in TAMAMINI panelin HTTPS'i arkasından sunan bir ters
        // vekildir: HTML sayfaları, CSS/JS varlıkları, AJAX çağrıları ve form gönderimleri
        // hepsi oradan geçer. Bunlar tarayıcının kendi istekleridir; bir sayfaya gidilirken ya
        // da bir form gönderilirken özel başlık EKLENEMEZ. Kural buraya da uygulandığında
        // "phpMyAdmin'de aç" düğmesi ham bir 404 JSON'ı gösterir hâle geldi.
        //
        // Koruma kaybolmuyor: bu denetleyici [Authorize(Roles = "Administrator")] ile ÇEREZ
        // tabanlı kimlik doğrular, giriş bağlantısı tek kullanımlık yüksek entropili bir
        // belirteç taşır ve IP kısıtlaması bu metottan ÖNCE zaten uygulanmıştır. Gizli giriş
        // yolu başlığı programatik API erişimine karşı bir tarama önlemidir; tarayıcının
        // gezdiği bir yüzeyde işlevi yoktur. Aynı gerekçeyle /mcp de muaf tutulmuştu.
        if (path.StartsWithSegments("/api/v1/dbtools", out _))
        {
            return false;
        }

        // Anonim uclar (health, setup) daha yukarida IsAlwaysPublicPath ile ayrildi; buraya gelen
        // her /api istegi kimlige bagli veri tasir ve basligi ister.
        return path.StartsWithSegments("/api", out _);
    }

    private static Task RejectApiRequestAsync(HttpContext context, int statusCode, string message)
    {
        context.Response.Clear();
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json; charset=utf-8";
        return context.Response.WriteAsJsonAsync(ApiResponse<object?>.Fail(message, statusCode));
    }
}
