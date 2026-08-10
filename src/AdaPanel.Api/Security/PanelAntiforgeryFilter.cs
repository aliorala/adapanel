using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace AdaPanel.Api.Security;

/// <summary>
/// CSRF doğrulamasını ÇEREZLE gelen isteklerde uygular, ANAHTARLA gelenlerde atlar.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden gerekli:</b> panelde küresel <c>AutoValidateAntiforgeryToken</c> vardı ve durum değiştiren
/// her isteğe <c>X-CSRF-TOKEN</c> şartı koyuyordu. CSRF koruması ÇEREZE karşıdır: saldırgan sayfası
/// kurbanın tarayıcısına istek yaptırdığında çerez KENDİLİĞİNDEN gider. <c>Authorization</c> başlığı
/// ise kendiliğinden gitmez — istemcinin anahtarı bilmesi ve başlığı açıkça eklemesi gerekir
/// (tarayıcıdan çapraz kaynakla bunu yapmak ayrıca CORS ön-kontrolü ister ve panel CORS açmaz).
/// Dolayısıyla anahtarla gelen istekte antiforgery koruma DEĞİL, yalnız engeldir: dış entegrasyonun
/// önce <c>/auth/csrf</c> çağırıp çerez taşıması gerekirdi ki bu, anahtarın var oluş sebebini
/// ortadan kaldırırdı.
/// </para>
/// <para>
/// <b>Neden uç bazında değil burada:</b> muafiyet <c>[IgnoreAntiforgeryToken]</c> ile uç uç
/// verilseydi, o uç ÇEREZLE gelen isteklerde de korumasız kalırdı — yani gerçek bir CSRF deliği
/// açılırdı. Karar isteğin KİMLİK YOLUNA bağlıdır, ucun kendisine değil; bu yüzden tek küresel
/// filtrede, çalışma anında verilir.
/// </para>
/// <para>
/// <b>Neden <c>AutoValidateAntiforgeryTokenAttribute</c> yerine bu:</b> o öznitelik kararını uç
/// meta verisinden verir (istek başına değiştirilemez), dolayısıyla "çerezde iste, anahtarda isteme"
/// ayrımı onunla ifade edilemiyordu.
/// </para>
/// <para>
/// Doğrulama başarısızsa <see cref="AntiforgeryValidationException"/> YUKARI BIRAKILIR:
/// <c>ApiExceptionHandlingMiddleware</c> onu zaten yakalayıp çevirisi olan
/// <c>security.invalid_csrf_token</c> anahtarıyla 400 döndürüyor. Öznitelik ise gövdesiz bir 400
/// üretiyordu ve kullanıcı sebebini göremiyordu.
/// </para>
/// </remarks>
public sealed class PanelAntiforgeryFilter(IAntiforgery antiforgery) : IAsyncAuthorizationFilter
{
    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var http = context.HttpContext;

        // Okuma isteği CSRF'in konusu değil (AutoValidate da aynı fiilleri muaf tutuyordu).
        if (!PanelAuthentication.IsStateChanging(http.Request.Method))
        {
            return;
        }

        // Ucun kendi muafiyeti korunur: DbToolsProxyController (phpMyAdmin/Adminer POST'ları panelin
        // token'ını taşımaz) ve GitController web kancası bu özniteliği taşıyor.
        if (http.GetEndpoint()?.Metadata.GetMetadata<IgnoreAntiforgeryTokenAttribute>() is not null)
        {
            return;
        }

        // ANAHTARLA doğrulanmış istek: çerez kullanılmadığı için CSRF yüzeyi yok.
        if (PanelAuthentication.IsTokenAuthenticated(http.User))
        {
            return;
        }

        await antiforgery.ValidateRequestAsync(http);
    }
}
