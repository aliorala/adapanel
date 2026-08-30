namespace AdaPanel.Api.Security;

/// <summary>
/// Panelin güvenlik başlıkları — TEK KAYNAK.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden ayrı sınıf — ölçülmüş boşluk.</b> Başlıklar tek bir ara katmanda satır içi
/// yazılıyordu ve iki yol o ara katmana HİÇ UĞRAMIYORDU:
/// </para>
/// <list type="number">
/// <item><b>Hata yanıtları.</b> <c>ApiExceptionHandlingMiddleware</c> daha DIŞTA duruyor ve
/// yanıtı yazmadan önce <c>Response.Clear()</c> çağırıyor — bu, iç ara katmanın koyduğu
/// başlıkları da siler. Yani her <c>AppException</c> (yanlış parola, kota aşımı, CSRF reddi) ve
/// her 500, CSP/nosniff/X-Frame-Options/Referrer-Policy <b>olmadan</b> gidiyordu.</item>
/// <item><b>429 yanıtları.</b> Hız sınırlayıcı başlık ara katmanından ÖNCE kayıtlıydı; isteği
/// reddettiğinde zinciri kısa devre ettiği için başlıklar hiç yazılmıyordu.</item>
/// </list>
/// <para>
/// İkisi de "koruma var ama o yolda uygulanmıyor" sınıfıdır — bu depoda defalarca ölçülen kalıp.
/// Başlıkları tek bir yerden okunur hâle getirmek, üçüncü bir yolun aynı boşluğa düşmesini
/// zorlaştırır.
/// </para>
/// <para>
/// <b>HSTS burada YOK ve bu bir karardır</b> — panel kendi imzaladığı sertifikayla geldiği için
/// HSTS yöneticiyi kendi panelinden kalıcı olarak kilitler. Gerekçesi <c>Program.cs</c>'te ve
/// <c>PanelSecurityHeaderTests</c>'te yazılı.
/// </para>
/// </remarks>
public static class PanelSecurityHeaders
{
    /// <summary>
    /// DbTools ters vekil yolu — panelin katı başlıkları BİLEREK uygulanmaz.
    /// </summary>
    /// <remarks>
    /// Katı CSP (<c>script-src 'self'</c>) phpMyAdmin'in satır içi betiklerini bloklar; menü
    /// genişliği hesaplanamaz ve içerik sol menünün altına kayar (kullanıcı bildirimi). Bu
    /// araçlar zaten panelin kimlik doğrulamalı HTTPS vekilinin arkasında.
    /// </remarks>
    private const string MuafYol = "/api/v1/dbtools";

    public const string ContentSecurityPolicy =
        "default-src 'self'; img-src 'self' data:; style-src 'self' 'unsafe-inline'; " +
        "script-src 'self'; connect-src 'self'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'";

    /// <summary>
    /// Başlıkları uygular. Yanıt zaten başladıysa ya da muaf yoldaysa hiçbir şey yapmaz.
    /// </summary>
    public static void Apply(HttpContext context)
    {
        // Yanıt gövdesi akmaya başladıysa başlık yazmak istisna atar. Sessizce geçmek doğrudur:
        // bu noktada yapılacak bir şey yok ve hata fırlatmak asıl yanıtı bozardı.
        if (context.Response.HasStarted || context.Request.Path.StartsWithSegments(MuafYol))
        {
            return;
        }

        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["X-Frame-Options"] = "DENY";
        context.Response.Headers["Referrer-Policy"] = "same-origin";
        context.Response.Headers["Content-Security-Policy"] = ContentSecurityPolicy;
    }
}
